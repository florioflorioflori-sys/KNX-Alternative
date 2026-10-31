using System.Buffers.Binary;
using System.Collections.Concurrent;
using KnxAlternative.Core.KnxIp;

namespace KnxAlternative.Core.Management;

public sealed record DeviceInfo(
    IndividualAddress Address,
    ushort MaskVersion,
    string? SerialNumber,
    ushort? ManufacturerId,
    string? OrderInfo,
    string? HardwareType,
    bool? ProgrammingMode)
{
    public string MaskDescription => MaskVersion switch
    {
        0x0010 or 0x0011 or 0x0012 => "System 1 (BCU 1)",
        0x0020 or 0x0021 => "System 2 (BCU 2)",
        0x0025 => "System 2 (BCU 2, Version 5)",
        0x0300 or 0x0310 => "System 300",
        0x0701 or 0x0705 => "System 7 (BIM M112)",
        0x07B0 => "System B",
        0x091A => "KNXnet/IP-Router",
        0x0910 => "Linienkoppler",
        0x5705 => "System 7 (KNX RF)",
        0x57B0 => "System B (Wireless/IP)",
        _ => $"Maske {MaskVersion:X4}",
    };
}

/// <summary>Commissioning services: physical address, programming mode, device info, restart.</summary>
public sealed class DeviceManagement
{
    readonly IKnxConnection _bus;
    public DeviceManagement(IKnxConnection bus) => _bus = bus;

    /// <summary>Sends IndividualAddressRead; every device with the programming LED on answers.</summary>
    public async Task<IReadOnlyList<IndividualAddress>> FindDevicesInProgrammingModeAsync(TimeSpan? wait = null, CancellationToken ct = default)
    {
        var found = new ConcurrentDictionary<IndividualAddress, bool>();
        void Handler(object? s, CemiFrame f)
        {
            if (f.Service == Apci.IndividualAddressResponse && f.MessageCode != CemiMessageCode.LDataCon) found[f.Source] = true;
        }
        _bus.FrameReceived += Handler;
        try
        {
            await _bus.SendAsync(CemiFrame.Broadcast(Apci.IndividualAddressRead), ct);
            await Task.Delay(wait ?? TimeSpan.FromSeconds(2), ct);
        }
        finally
        {
            _bus.FrameReceived -= Handler;
        }
        return found.Keys.OrderBy(a => a).ToList();
    }

    /// <summary>True if a device answers on the address (T_Connect + DeviceDescriptorRead).</summary>
    public async Task<bool> IsAddressOccupiedAsync(IndividualAddress address, CancellationToken ct = default)
    {
        try
        {
            await ReadMaskVersionAsync(address, ct);
            return true;
        }
        catch (KnxException)
        {
            return false;
        }
    }

    /// <summary>
    /// Assigns a physical address the ETS way: exactly one device in programming mode,
    /// target address free, write, verify, then restart to switch the LED off.
    /// </summary>
    public async Task ProgramIndividualAddressAsync(IndividualAddress newAddress, IProgress<string>? progress = null,
        TimeSpan? waitForButton = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (waitForButton ?? TimeSpan.FromMinutes(2));
        progress?.Report($"Prüfe, ob {newAddress} schon vergeben ist …");
        bool occupied = await IsAddressOccupiedAsync(newAddress, ct);

        progress?.Report("Bitte jetzt die Programmiertaste am Gerät drücken …");
        IReadOnlyList<IndividualAddress> inProg;
        while (true)
        {
            inProg = await FindDevicesInProgrammingModeAsync(TimeSpan.FromSeconds(1.5), ct);
            if (inProg.Count > 0) break;
            if (DateTime.UtcNow > deadline) throw new KnxException("Kein Gerät im Programmiermodus gefunden.");
        }
        if (inProg.Count > 1)
            throw new KnxException($"Mehrere Geräte im Programmiermodus: {string.Join(", ", inProg)}. Bitte nur eines aktivieren.");

        var current = inProg[0];
        if (occupied && current != newAddress)
            throw new KnxException($"Die Adresse {newAddress} ist bereits von einem anderen Gerät belegt.");

        if (current != newAddress)
        {
            progress?.Report($"Gerät gefunden ({current}), schreibe {newAddress} …");
            await _bus.SendAsync(CemiFrame.Broadcast(Apci.IndividualAddressWrite, (byte)(newAddress.Raw >> 8), (byte)newAddress.Raw), ct);
            await Task.Delay(300, ct);
            var check = await FindDevicesInProgrammingModeAsync(TimeSpan.FromSeconds(1.5), ct);
            if (!check.Contains(newAddress)) throw new KnxException("Das Gerät meldet die neue Adresse nicht zurück.");
        }

        progress?.Report("Starte Gerät neu …");
        await RestartAsync(newAddress, ct);
        progress?.Report($"Physikalische Adresse {newAddress} erfolgreich programmiert.");
    }

    /// <summary>Assigns a physical address via serial number (no programming button needed).</summary>
    public async Task ProgramIndividualAddressBySerialAsync(byte[] serialNumber, IndividualAddress newAddress, CancellationToken ct = default)
    {
        if (serialNumber.Length != 6) throw new ArgumentException("Seriennummer muss 6 Byte lang sein", nameof(serialNumber));
        if (await IsAddressOccupiedAsync(newAddress, ct))
            throw new KnxException($"Die Adresse {newAddress} ist bereits belegt.");
        byte[] data = [.. serialNumber, (byte)(newAddress.Raw >> 8), (byte)newAddress.Raw, 0, 0, 0, 0];
        await _bus.SendAsync(CemiFrame.Broadcast(Apci.IndividualAddressSerialNumberWrite, data), ct);
        await Task.Delay(300, ct);
        await ReadMaskVersionAsync(newAddress, ct);
    }

    public async Task<ushort> ReadMaskVersionAsync(IndividualAddress address, CancellationToken ct = default)
    {
        await using var tc = new TransportConnection(_bus, address);
        await tc.ConnectAsync(ct);
        var resp = await tc.RequestAsync(Apci.DeviceDescriptorRead, [0x00], Apci.DeviceDescriptorResponse, shortData: true, ct: ct);
        if (resp.Data.Length < 3) throw new KnxException("Ungültige Gerätebeschreibung");
        return (ushort)((resp.Data[1] << 8) | resp.Data[2]);
    }

    /// <summary>Reads mask version and, if supported, device object properties (serial, manufacturer, order number).</summary>
    public async Task<DeviceInfo> ReadDeviceInfoAsync(IndividualAddress address, CancellationToken ct = default)
    {
        await using var tc = new TransportConnection(_bus, address);
        await tc.ConnectAsync(ct);
        var dd = await tc.RequestAsync(Apci.DeviceDescriptorRead, [0x00], Apci.DeviceDescriptorResponse, shortData: true, ct: ct);
        ushort mask = (ushort)((dd.Data[1] << 8) | dd.Data[2]);

        async Task<byte[]?> Prop(byte pid)
        {
            try
            {
                var r = await tc.RequestAsync(Apci.PropertyValueRead, [0x00, pid, 0x10, 0x01], Apci.PropertyValueResponse, ct: ct);
                // response: object, pid, count/start (2 bytes), data; count 0 = error
                if (r.Data.Length <= 4 || (r.Data[2] >> 4) == 0) return null;
                return r.Data[4..];
            }
            catch (KnxException) { return null; }
        }

        string? serial = null, order = null, hw = null;
        ushort? manu = null;
        bool? prog = null;
        if (mask >= 0x0700 || mask == 0x0300 || mask == 0x0310 || (mask & 0xFF00) == 0x5700 || mask == 0x091A)
        {
            var s = await Prop(11); if (s is { Length: 6 }) serial = Convert.ToHexString(s);
            var m = await Prop(12); if (m is { Length: 2 }) manu = BinaryPrimitives.ReadUInt16BigEndian(m);
            var o = await Prop(15); if (o is { Length: 10 }) order = Convert.ToHexString(o);
            var h = await Prop(78); if (h is { Length: 6 }) hw = Convert.ToHexString(h);
            var p = await Prop(54); if (p is { Length: 1 }) prog = (p[0] & 1) == 1;
        }
        return new DeviceInfo(address, mask, serial, manu, order, hw, prog);
    }

    public async Task<byte[]> ReadMemoryAsync(IndividualAddress address, ushort start, int length, CancellationToken ct = default)
    {
        await using var tc = new TransportConnection(_bus, address);
        await tc.ConnectAsync(ct);
        var result = new List<byte>(length);
        while (result.Count < length)
        {
            int n = Math.Min(12, length - result.Count);
            ushort a = (ushort)(start + result.Count);
            var r = await tc.RequestAsync(Apci.MemoryRead, [(byte)n, (byte)(a >> 8), (byte)a], Apci.MemoryResponse, shortData: true, ct: ct);
            if (r.Data.Length < 3 || r.Data[0] == 0) throw new KnxException($"Speicher ab 0x{a:X4} nicht lesbar");
            result.AddRange(r.Data[3..]);
        }
        return result.ToArray();
    }

    /// <summary>Basic restart. The device drops the transport connection, so no reply is expected.</summary>
    public async Task RestartAsync(IndividualAddress address, CancellationToken ct = default)
    {
        var tc = new TransportConnection(_bus, address);
        try
        {
            await tc.ConnectAsync(ct);
            await tc.SendDataAsync(Apci.Restart, [], ct: ct);
        }
        finally
        {
            // no T_Disconnect needed: the device resets its transport layer
            await tc.DisposeAsync();
        }
    }

    /// <summary>Probes every address of a line and reports devices that answer.</summary>
    public async Task<IReadOnlyList<IndividualAddress>> ScanLineAsync(int area, int line, IProgress<(int Done, IndividualAddress? Found)>? progress = null,
        int from = 1, int to = 255, CancellationToken ct = default)
    {
        var found = new List<IndividualAddress>();
        for (int d = from; d <= to; d++)
        {
            ct.ThrowIfCancellationRequested();
            var a = IndividualAddress.From(area, line, d);
            bool ok;
            await using (var tc = new TransportConnection(_bus, a) { Timeout = TimeSpan.FromMilliseconds(800) })
            {
                try
                {
                    await tc.ConnectAsync(ct);
                    await tc.RequestAsync(Apci.DeviceDescriptorRead, [0x00], Apci.DeviceDescriptorResponse, shortData: true, ct: ct);
                    ok = true;
                }
                catch (KnxException) { ok = false; }
            }
            if (ok) found.Add(a);
            progress?.Report((d - from + 1, ok ? a : null));
        }
        return found;
    }
}
