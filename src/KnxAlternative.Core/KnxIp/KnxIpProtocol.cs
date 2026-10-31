using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KnxAlternative.Core.KnxIp;

public static class ServiceType
{
    public const ushort SearchRequest = 0x0201;
    public const ushort SearchResponse = 0x0202;
    public const ushort DescriptionRequest = 0x0203;
    public const ushort DescriptionResponse = 0x0204;
    public const ushort ConnectRequest = 0x0205;
    public const ushort ConnectResponse = 0x0206;
    public const ushort ConnectionStateRequest = 0x0207;
    public const ushort ConnectionStateResponse = 0x0208;
    public const ushort DisconnectRequest = 0x0209;
    public const ushort DisconnectResponse = 0x020A;
    public const ushort TunnelingRequest = 0x0420;
    public const ushort TunnelingAck = 0x0421;
    public const ushort RoutingIndication = 0x0530;
    public const ushort RoutingBusy = 0x0532;
}

public static class KnxIpConstants
{
    public const int Port = 3671;
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.23.12");
}

/// <summary>Builds and parses KNXnet/IP frames.</summary>
public static class KnxIpFrame
{
    public static byte[] Build(ushort serviceType, params byte[][] parts)
    {
        int len = 6 + parts.Sum(p => p.Length);
        var b = new byte[len];
        b[0] = 0x06; b[1] = 0x10;
        b[2] = (byte)(serviceType >> 8); b[3] = (byte)serviceType;
        b[4] = (byte)(len >> 8); b[5] = (byte)len;
        int o = 6;
        foreach (var p in parts) { p.CopyTo(b, o); o += p.Length; }
        return b;
    }

    public static bool TryParse(byte[] data, out ushort serviceType, out ReadOnlyMemory<byte> body)
    {
        serviceType = 0;
        body = default;
        if (data.Length < 6 || data[0] != 0x06 || data[1] != 0x10) return false;
        int len = (data[4] << 8) | data[5];
        if (len > data.Length || len < 6) return false;
        serviceType = (ushort)((data[2] << 8) | data[3]);
        body = data.AsMemory(6, len - 6);
        return true;
    }

    /// <summary>Host protocol address information (UDP). 0.0.0.0:0 selects NAT mode.</summary>
    public static byte[] Hpai(IPEndPoint? ep)
    {
        var b = new byte[8];
        b[0] = 0x08; b[1] = 0x01;
        if (ep != null)
        {
            ep.Address.MapToIPv4().GetAddressBytes().CopyTo(b, 2);
            b[6] = (byte)(ep.Port >> 8); b[7] = (byte)ep.Port;
        }
        return b;
    }

    public static IPEndPoint ParseHpai(ReadOnlySpan<byte> s) =>
        new(new IPAddress(s.Slice(2, 4)), (s[6] << 8) | s[7]);
}

/// <summary>A KNXnet/IP interface or router found on the network.</summary>
public sealed record KnxIpDevice(
    IPEndPoint ControlEndpoint,
    string FriendlyName,
    IndividualAddress Address,
    string SerialNumber,
    string MacAddress,
    bool SupportsTunneling,
    bool SupportsRouting)
{
    public override string ToString() =>
        $"{FriendlyName} ({ControlEndpoint.Address}, {Address}){(SupportsTunneling ? " Tunneling" : "")}{(SupportsRouting ? " Routing" : "")}";

    internal static KnxIpDevice? FromSearchResponse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 8 + 2) return null;
        var ep = KnxIpFrame.ParseHpai(body);
        int o = 8;
        string name = "", serial = "", mac = "";
        var ia = new IndividualAddress(0);
        bool tunnel = false, routing = false;
        while (o + 2 <= body.Length)
        {
            int len = body[o];
            int type = body[o + 1];
            if (len < 2 || o + len > body.Length) break;
            var dib = body.Slice(o, len);
            if (type == 0x01 && len >= 54)
            {
                ia = new IndividualAddress((ushort)((dib[4] << 8) | dib[5]));
                serial = Convert.ToHexString(dib.Slice(8, 6));
                mac = string.Join(":", dib.Slice(18, 6).ToArray().Select(x => x.ToString("X2")));
                name = Encoding.Latin1.GetString(dib.Slice(24, 30)).TrimEnd('\0', ' ');
            }
            else if (type == 0x02)
            {
                for (int i = 2; i + 1 < len; i += 2)
                {
                    if (dib[i] == 0x04) tunnel = true;
                    if (dib[i] == 0x05) routing = true;
                }
            }
            o += len;
        }
        return new KnxIpDevice(ep, name, ia, serial, mac, tunnel, routing);
    }
}

/// <summary>Finds KNXnet/IP devices with a multicast SEARCH_REQUEST on every IPv4 interface.</summary>
public static class KnxIpDiscovery
{
    public static async Task<IReadOnlyList<KnxIpDevice>> SearchAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var results = new Dictionary<string, KnxIpDevice>();
        var locals = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct()
            .ToList();

        var tasks = locals.Select(async local =>
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(local, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                var req = KnxIpFrame.Build(ServiceType.SearchRequest, KnxIpFrame.Hpai((IPEndPoint)udp.Client.LocalEndPoint!));
                await udp.SendAsync(req, new IPEndPoint(KnxIpConstants.MulticastAddress, KnxIpConstants.Port), ct);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult r;
                    try { r = await udp.ReceiveAsync(cts.Token); }
                    catch (OperationCanceledException) { break; }
                    if (KnxIpFrame.TryParse(r.Buffer, out var st, out var body) && st == ServiceType.SearchResponse)
                    {
                        var dev = KnxIpDevice.FromSearchResponse(body.Span);
                        if (dev != null)
                            lock (results) results[dev.ControlEndpoint.ToString() + dev.SerialNumber] = dev;
                    }
                }
            }
            catch (SocketException)
            {
                // interface not usable for multicast
            }
        });
        await Task.WhenAll(tasks);
        return results.Values.OrderBy(d => d.FriendlyName).ToList();
    }
}
