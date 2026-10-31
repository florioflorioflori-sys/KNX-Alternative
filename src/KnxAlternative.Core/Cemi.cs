namespace KnxAlternative.Core;

public enum CemiMessageCode : byte
{
    LDataReq = 0x11,
    LDataCon = 0x2E,
    LDataInd = 0x29,
}

public enum Priority : byte { System = 0, Normal = 1, Urgent = 2, Low = 3 }

/// <summary>Application layer services (10-bit APCI).</summary>
public enum Apci : ushort
{
    GroupValueRead = 0x000,
    GroupValueResponse = 0x040,
    GroupValueWrite = 0x080,
    IndividualAddressWrite = 0x0C0,
    IndividualAddressRead = 0x100,
    IndividualAddressResponse = 0x140,
    AdcRead = 0x180,
    AdcResponse = 0x1C0,
    MemoryRead = 0x200,
    MemoryResponse = 0x240,
    MemoryWrite = 0x280,
    DeviceDescriptorRead = 0x300,
    DeviceDescriptorResponse = 0x340,
    Restart = 0x380,
    AuthorizeRequest = 0x3D1,
    AuthorizeResponse = 0x3D2,
    PropertyValueRead = 0x3D5,
    PropertyValueResponse = 0x3D6,
    PropertyValueWrite = 0x3D7,
    IndividualAddressSerialNumberRead = 0x3DC,
    IndividualAddressSerialNumberResponse = 0x3DD,
    IndividualAddressSerialNumberWrite = 0x3DE,
}

/// <summary>Transport layer control (TPCI) kinds.</summary>
public enum TpduKind
{
    DataGroup,          // UDT to a group address (incl. broadcast 0/0/0)
    DataIndividual,     // UDT connectionless to an individual address
    DataConnected,      // NDT in a transport connection, with sequence number
    Connect,
    Disconnect,
    Ack,
    Nak,
}

/// <summary>A cEMI L_Data frame (standard frame).</summary>
public sealed class CemiFrame
{
    public CemiMessageCode MessageCode { get; init; } = CemiMessageCode.LDataReq;
    public Priority Priority { get; init; } = Priority.Low;
    public bool Repeat { get; init; }
    public bool AckRequested { get; init; }
    /// <summary>In L_Data.con: true if the frame could not be sent.</summary>
    public bool ConfirmError { get; init; }
    public int HopCount { get; init; } = 6;
    public IndividualAddress Source { get; init; }
    /// <summary>Raw destination; interpret with <see cref="IsGroupDestination"/>.</summary>
    public ushort Destination { get; init; }
    public bool IsGroupDestination { get; init; }

    public TpduKind Kind { get; init; }
    public int Sequence { get; init; }
    public Apci? Service { get; init; }
    /// <summary>Data following the APCI. If <see cref="ShortData"/> is set, Data[0] is merged into the low 6 APCI bits.</summary>
    public byte[] Data { get; init; } = [];
    public bool ShortData { get; init; }

    public GroupAddress DestinationGroup => new(Destination);
    public IndividualAddress DestinationIndividual => new(Destination);

    public static CemiFrame GroupWrite(GroupAddress ga, GroupValue v, Priority prio = Priority.Low) => new()
    {
        Destination = ga.Raw, IsGroupDestination = true, Kind = TpduKind.DataGroup, Priority = prio,
        Service = Apci.GroupValueWrite, Data = v.Data, ShortData = v.IsShort,
    };

    public static CemiFrame GroupRead(GroupAddress ga) => new()
    {
        Destination = ga.Raw, IsGroupDestination = true, Kind = TpduKind.DataGroup, Service = Apci.GroupValueRead,
    };

    public static CemiFrame Broadcast(Apci service, params byte[] data) => new()
    {
        Destination = 0, IsGroupDestination = true, Kind = TpduKind.DataGroup, Priority = Priority.System,
        Service = service, Data = data,
    };

    public byte[] ToBytes()
    {
        var tpdu = new List<byte>();
        byte tpci = Kind switch
        {
            TpduKind.DataGroup or TpduKind.DataIndividual => 0x00,
            TpduKind.DataConnected => (byte)(0x40 | ((Sequence & 0x0F) << 2)),
            TpduKind.Connect => 0x80,
            TpduKind.Disconnect => 0x81,
            TpduKind.Ack => (byte)(0xC2 | ((Sequence & 0x0F) << 2)),
            TpduKind.Nak => (byte)(0xC3 | ((Sequence & 0x0F) << 2)),
            _ => 0,
        };

        if (Service is { } apci)
        {
            int a = (int)apci;
            tpdu.Add((byte)(tpci | ((a >> 8) & 0x03)));
            byte second = (byte)(a & 0xFF);
            if (ShortData && Data.Length > 0) second |= (byte)(Data[0] & 0x3F);
            tpdu.Add(second);
            tpdu.AddRange(ShortData ? Data.Skip(1) : Data);
        }
        else
        {
            tpdu.Add(tpci);
        }

        var b = new List<byte>(11 + tpdu.Count)
        {
            (byte)MessageCode,
            0x00, // no additional info
        };
        byte ctrl1 = 0x80; // standard frame
        if (!Repeat) ctrl1 |= 0x20;
        ctrl1 |= 0x10; // system broadcast = normal broadcast
        ctrl1 |= (byte)(((int)Priority & 0x03) << 2);
        if (AckRequested) ctrl1 |= 0x02;
        if (ConfirmError) ctrl1 |= 0x01;
        byte ctrl2 = (byte)(((HopCount & 0x07) << 4) | (IsGroupDestination ? 0x80 : 0));
        b.Add(ctrl1);
        b.Add(ctrl2);
        b.Add((byte)(Source.Raw >> 8));
        b.Add((byte)Source.Raw);
        b.Add((byte)(Destination >> 8));
        b.Add((byte)Destination);
        b.Add((byte)(tpdu.Count - 1));
        b.AddRange(tpdu);
        return b.ToArray();
    }

    public static CemiFrame Parse(ReadOnlySpan<byte> s)
    {
        if (s.Length < 10) throw new FormatException("cEMI-Rahmen zu kurz");
        var code = (CemiMessageCode)s[0];
        int i = 2 + s[1]; // skip additional info
        if (s.Length < i + 7) throw new FormatException("cEMI-Rahmen zu kurz");
        byte ctrl1 = s[i], ctrl2 = s[i + 1];
        var src = new IndividualAddress((ushort)((s[i + 2] << 8) | s[i + 3]));
        ushort dst = (ushort)((s[i + 4] << 8) | s[i + 5]);
        int len = s[i + 6];
        var tpdu = s[(i + 7)..];
        if (tpdu.Length < len + 1) throw new FormatException("cEMI-Längenfeld passt nicht");
        tpdu = tpdu[..(len + 1)];
        bool group = (ctrl2 & 0x80) != 0;
        byte tpci = tpdu[0];

        TpduKind kind;
        int seq = (tpci >> 2) & 0x0F;
        bool control = (tpci & 0x80) != 0;
        bool numbered = (tpci & 0x40) != 0;
        if (control)
        {
            kind = (tpci & 0x03) switch
            {
                0 when !numbered => TpduKind.Connect,
                1 when !numbered => TpduKind.Disconnect,
                2 => TpduKind.Ack,
                3 => TpduKind.Nak,
                _ => throw new FormatException($"Unbekanntes TPCI {tpci:X2}"),
            };
        }
        else
        {
            kind = numbered ? TpduKind.DataConnected : (group ? TpduKind.DataGroup : TpduKind.DataIndividual);
        }

        Apci? service = null;
        byte[] data = [];
        bool shortData = false;
        if (!control && tpdu.Length >= 2)
        {
            int apci = ((tpci & 0x03) << 8) | tpdu[1];
            int hi4 = apci & 0x3C0;
            // Services whose low 6 bits carry data
            if (hi4 is 0x000 or 0x040 or 0x080)
            {
                service = (Apci)hi4;
                if (tpdu.Length == 2)
                {
                    data = [(byte)(apci & 0x3F)];
                    shortData = true;
                }
                else
                {
                    data = tpdu[2..].ToArray();
                }
            }
            else if (hi4 is 0x180 or 0x1C0 or 0x200 or 0x240 or 0x280 or 0x300 or 0x340)
            {
                // ADC, memory and descriptor services use the low 6 bits as channel/count/type
                service = (Apci)hi4;
                data = [(byte)(apci & 0x3F), .. tpdu[2..].ToArray()];
                shortData = true;
            }
            else
            {
                service = (Apci)apci;
                data = tpdu[2..].ToArray();
            }
        }

        return new CemiFrame
        {
            MessageCode = code,
            Repeat = (ctrl1 & 0x20) == 0,
            Priority = (Priority)((ctrl1 >> 2) & 0x03),
            AckRequested = (ctrl1 & 0x02) != 0,
            ConfirmError = (ctrl1 & 0x01) != 0,
            HopCount = (ctrl2 >> 4) & 0x07,
            Source = src,
            Destination = dst,
            IsGroupDestination = group,
            Kind = kind,
            Sequence = seq,
            Service = service,
            Data = data,
            ShortData = shortData,
        };
    }

    public GroupValue? GroupValue =>
        Service is Apci.GroupValueWrite or Apci.GroupValueResponse ? new GroupValue(Data, ShortData) : null;

    public override string ToString()
    {
        string dst = IsGroupDestination ? DestinationGroup.ToString() : DestinationIndividual.ToString();
        string svc = Service?.ToString() ?? Kind.ToString();
        string data = Data.Length == 0 ? "" : " " + (ShortData ? $"${Data[0]:X2}" : string.Join(" ", Data.Select(x => x.ToString("X2"))));
        return $"{MessageCode} {Source} -> {dst} {svc}{data}";
    }
}
