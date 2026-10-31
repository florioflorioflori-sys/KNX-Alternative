using System.Globalization;

namespace KnxAlternative.Core;

/// <summary>Physical (individual) address: area.line.device, 4/4/8 bit.</summary>
public readonly record struct IndividualAddress(ushort Raw) : IComparable<IndividualAddress>
{
    public int Area => Raw >> 12;
    public int Line => (Raw >> 8) & 0x0F;
    public int Device => Raw & 0xFF;

    public static IndividualAddress From(int area, int line, int device)
    {
        if (area is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(area), "Bereich muss 0..15 sein");
        if (line is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(line), "Linie muss 0..15 sein");
        if (device is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(device), "Teilnehmer muss 0..255 sein");
        return new IndividualAddress((ushort)((area << 12) | (line << 8) | device));
    }

    public static IndividualAddress Parse(string text)
    {
        if (!TryParse(text, out var a)) throw new FormatException($"Ungültige physikalische Adresse: '{text}'");
        return a;
    }

    public static bool TryParse(string? text, out IndividualAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a) || a > 15) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var l) || l > 15) return false;
        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d > 255) return false;
        address = From(a, l, d);
        return true;
    }

    public int CompareTo(IndividualAddress other) => Raw.CompareTo(other.Raw);
    public override string ToString() => $"{Area}.{Line}.{Device}";
}

public enum GroupAddressStyle { ThreeLevel, TwoLevel, Free }

/// <summary>Group address: main/middle/sub (5/3/8), main/sub (5/11) or free (16 bit).</summary>
public readonly record struct GroupAddress(ushort Raw) : IComparable<GroupAddress>
{
    public int Main => (Raw >> 11) & 0x1F;
    public int Middle => (Raw >> 8) & 0x07;
    public int Sub => Raw & 0xFF;
    public int Sub11 => Raw & 0x7FF;

    public static GroupAddress From(int main, int middle, int sub)
    {
        if (main is < 0 or > 31) throw new ArgumentOutOfRangeException(nameof(main), "Hauptgruppe muss 0..31 sein");
        if (middle is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(middle), "Mittelgruppe muss 0..7 sein");
        if (sub is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(sub), "Untergruppe muss 0..255 sein");
        return new GroupAddress((ushort)((main << 11) | (middle << 8) | sub));
    }

    public static GroupAddress Parse(string text)
    {
        if (!TryParse(text, out var a)) throw new FormatException($"Ungültige Gruppenadresse: '{text}'");
        return a;
    }

    public static bool TryParse(string? text, out GroupAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('/');
        var n = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return false;
        switch (parts.Length)
        {
            case 3 when n[0] <= 31 && n[1] <= 7 && n[2] <= 255:
                address = From(n[0], n[1], n[2]); return true;
            case 2 when n[0] <= 31 && n[1] <= 2047:
                address = new GroupAddress((ushort)((n[0] << 11) | n[1])); return true;
            case 1 when n[0] <= 65535:
                address = new GroupAddress((ushort)n[0]); return true;
            default: return false;
        }
    }

    public string ToString(GroupAddressStyle style) => style switch
    {
        GroupAddressStyle.TwoLevel => $"{Main}/{Sub11}",
        GroupAddressStyle.Free => Raw.ToString(CultureInfo.InvariantCulture),
        _ => $"{Main}/{Middle}/{Sub}",
    };

    public int CompareTo(GroupAddress other) => Raw.CompareTo(other.Raw);
    public override string ToString() => ToString(GroupAddressStyle.ThreeLevel);
}
