using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace KnxAlternative.Core;

/// <summary>Encoded group value. Short values (up to 6 bit) travel inside the APCI octet.</summary>
public sealed record GroupValue(byte[] Data, bool IsShort)
{
    public static GroupValue Short(int bits) => new([(byte)(bits & 0x3F)], true);
    public static GroupValue Bytes(params byte[] data) => new(data, false);
    public override string ToString() => IsShort ? $"$ {Data[0]:X2}" : string.Join(" ", Data.Select(b => b.ToString("X2")));
}

/// <summary>Datapoint type main/sub number, e.g. 9.001. Parses "9.001", "DPT-9", "DPST-9-1".</summary>
public readonly record struct DptId(int Main, int Sub)
{
    public static bool TryParse(string? text, out DptId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        // ETS may store several types separated by space; use the first.
        t = t.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        string[] parts;
        if (t.StartsWith("DPST-", StringComparison.OrdinalIgnoreCase)) parts = t[5..].Split('-');
        else if (t.StartsWith("DPT-", StringComparison.OrdinalIgnoreCase)) parts = t[4..].Split('-');
        else parts = t.Split('.');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var main)) return false;
        int sub = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out sub)) return false;
        id = new DptId(main, sub);
        return true;
    }

    public override string ToString() => Sub == 0 ? $"{Main}.xxx" : $"{Main}.{Sub:000}";
}

/// <summary>Encoding and decoding of the common KNX datapoint types.</summary>
public static class Dpt
{
    public static readonly IReadOnlyDictionary<DptId, string> Names = new Dictionary<DptId, string>
    {
        [new(1, 1)] = "Schalten", [new(1, 2)] = "Boolesch", [new(1, 3)] = "Freigeben", [new(1, 7)] = "Schritt",
        [new(1, 8)] = "Auf/Ab", [new(1, 9)] = "Öffnen/Schließen", [new(1, 10)] = "Start/Stopp", [new(1, 17)] = "Trigger",
        [new(1, 18)] = "Belegung", [new(1, 19)] = "Fenster/Tür", [new(1, 24)] = "Tag/Nacht",
        [new(2, 1)] = "Schalten mit Zwangsführung", [new(3, 7)] = "Dimmen", [new(3, 8)] = "Jalousie",
        [new(5, 1)] = "Prozent (0..100%)", [new(5, 3)] = "Winkel (°)", [new(5, 4)] = "Prozent (0..255%)", [new(5, 10)] = "Zähler 8 Bit",
        [new(6, 1)] = "Prozent (-128..127%)", [new(6, 10)] = "Zähler 8 Bit mit Vorzeichen",
        [new(7, 1)] = "Zähler 16 Bit", [new(7, 13)] = "Helligkeit (lux)", [new(8, 1)] = "Zähler 16 Bit mit Vorzeichen",
        [new(9, 1)] = "Temperatur (°C)", [new(9, 4)] = "Helligkeit (lux)", [new(9, 5)] = "Windgeschwindigkeit (m/s)",
        [new(9, 7)] = "Luftfeuchte (%)", [new(9, 8)] = "Luftqualität (ppm)", [new(9, 24)] = "Leistung (kW)",
        [new(10, 1)] = "Uhrzeit", [new(11, 1)] = "Datum", [new(12, 1)] = "Zähler 32 Bit", [new(13, 1)] = "Zähler 32 Bit mit Vorzeichen",
        [new(13, 10)] = "Wirkenergie (Wh)", [new(13, 13)] = "Wirkenergie (kWh)", [new(14, 56)] = "Leistung (W)",
        [new(14, 19)] = "Strom (A)", [new(14, 27)] = "Spannung (V)", [new(16, 0)] = "Text ASCII", [new(16, 1)] = "Text ISO-8859-1",
        [new(17, 1)] = "Szenennummer", [new(18, 1)] = "Szenensteuerung", [new(20, 102)] = "HVAC-Modus",
        [new(232, 600)] = "RGB-Farbe",
    };

    public static string Describe(DptId id) =>
        Names.TryGetValue(id, out var n) ? $"{id} {n}" : Names.TryGetValue(new DptId(id.Main, 0), out n) ? $"{id} {n}" : id.ToString();

    /// <summary>Size in bits of a datapoint main type; needed to know if a payload is short.</summary>
    public static int SizeInBits(int main) => main switch
    {
        1 => 1, 2 => 2, 3 => 4, 23 => 2,
        4 or 5 or 6 or 17 or 18 or 20 => 8,
        7 or 8 or 9 or 22 => 16,
        10 or 11 or 232 => 24,
        12 or 13 or 14 => 32,
        16 => 112,
        19 or 29 => 64,
        _ => 8,
    };

    public static GroupValue Encode(DptId id, string text)
    {
        var ci = CultureInfo.InvariantCulture;
        text = text.Trim();
        switch (id.Main)
        {
            case 1: return GroupValue.Short(ParseBool(text) ? 1 : 0);
            case 2:
            {
                var p = text.Split(',', ' ', ';').Where(s => s.Length > 0).ToArray();
                if (p.Length != 2) throw new FormatException("DPT 2 erwartet 'Steuerung,Wert', z. B. '1,0'");
                return GroupValue.Short((ParseBool(p[0]) ? 2 : 0) | (ParseBool(p[1]) ? 1 : 0));
            }
            case 3:
            {
                // "+3" = increase step code 3, "-1" = decrease step 1, "0" or "stop" = break
                if (text.Equals("stop", StringComparison.OrdinalIgnoreCase) || text == "0") return GroupValue.Short(0);
                int v = int.Parse(text, NumberStyles.AllowLeadingSign, ci);
                if (Math.Abs(v) > 7) throw new FormatException("DPT 3 Schrittweite muss -7..+7 sein");
                return GroupValue.Short((v > 0 ? 0x08 : 0) | Math.Abs(v));
            }
            case 5:
            {
                double v = ParseDouble(text);
                int raw = id.Sub switch
                {
                    1 => (int)Math.Round(v * 255.0 / 100.0),
                    3 => (int)Math.Round(v * 255.0 / 360.0),
                    _ => (int)Math.Round(v),
                };
                return GroupValue.Bytes((byte)Math.Clamp(raw, 0, 255));
            }
            case 6: return GroupValue.Bytes(unchecked((byte)(sbyte)Math.Clamp((int)Math.Round(ParseDouble(text)), -128, 127)));
            case 7: return Be16((ushort)Math.Clamp((int)Math.Round(ParseDouble(text)), 0, 65535));
            case 8: return Be16(unchecked((ushort)(short)Math.Clamp((int)Math.Round(ParseDouble(text)), -32768, 32767)));
            case 9: return Be16(EncodeFloat16(ParseDouble(text)));
            case 10:
            {
                var t = TimeSpan.Parse(text, ci);
                return GroupValue.Bytes((byte)t.Hours, (byte)t.Minutes, (byte)t.Seconds);
            }
            case 11:
            {
                var d = DateTime.ParseExact(text, ["yyyy-MM-dd", "dd.MM.yyyy"], ci, DateTimeStyles.None);
                return GroupValue.Bytes((byte)d.Day, (byte)d.Month, (byte)(d.Year % 100));
            }
            case 12: { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, uint.Parse(text, ci)); return GroupValue.Bytes(b); }
            case 13: { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, int.Parse(text, NumberStyles.AllowLeadingSign, ci)); return GroupValue.Bytes(b); }
            case 14: { var b = new byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, (float)ParseDouble(text)); return GroupValue.Bytes(b); }
            case 16:
            {
                var b = new byte[14];
                var enc = Encoding.Latin1.GetBytes(text);
                Array.Copy(enc, b, Math.Min(14, enc.Length));
                return GroupValue.Bytes(b);
            }
            case 17: return GroupValue.Bytes((byte)((int.Parse(text, ci) - 1) & 0x3F));
            case 18:
            {
                // "lernen 5" or "5"
                bool learn = text.StartsWith("lern", StringComparison.OrdinalIgnoreCase) || text.StartsWith("learn", StringComparison.OrdinalIgnoreCase);
                int n = int.Parse(new string(text.Where(char.IsDigit).ToArray()), ci);
                return GroupValue.Bytes((byte)((learn ? 0x80 : 0) | ((n - 1) & 0x3F)));
            }
            case 20: return GroupValue.Bytes(byte.Parse(text, ci));
            case 232:
            {
                var hex = text.TrimStart('#');
                if (hex.Length == 6)
                    return GroupValue.Bytes(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));
                var p = text.Split(',').Select(s => byte.Parse(s.Trim(), ci)).ToArray();
                return GroupValue.Bytes(p);
            }
            default:
                return ParseRaw(text);
        }
    }

    public static string Decode(DptId? id, GroupValue value)
    {
        var ci = CultureInfo.InvariantCulture;
        var d = value.Data;
        if (id is null) return value.ToString();
        var t = id.Value;
        try
        {
            switch (t.Main)
            {
                case 1:
                    bool on = (d[0] & 1) == 1;
                    return t.Sub switch
                    {
                        1 => on ? "Ein" : "Aus",
                        8 => on ? "Ab" : "Auf",
                        9 => on ? "Geschlossen" : "Offen",
                        10 => on ? "Start" : "Stopp",
                        _ => on ? "1" : "0",
                    };
                case 2: return $"Steuerung={(d[0] >> 1) & 1}, Wert={d[0] & 1}";
                case 3:
                    int step = d[0] & 0x07;
                    return step == 0 ? "Stopp" : ((d[0] & 0x08) != 0 ? $"+{step}" : $"-{step}");
                case 5:
                    return t.Sub switch
                    {
                        1 => (d[0] * 100.0 / 255.0).ToString("0.#", ci) + " %",
                        3 => (d[0] * 360.0 / 255.0).ToString("0.#", ci) + " °",
                        _ => d[0].ToString(ci),
                    };
                case 6: return ((sbyte)d[0]).ToString(ci);
                case 7: return BinaryPrimitives.ReadUInt16BigEndian(d).ToString(ci);
                case 8: return BinaryPrimitives.ReadInt16BigEndian(d).ToString(ci);
                case 9:
                    var f = DecodeFloat16(BinaryPrimitives.ReadUInt16BigEndian(d));
                    return t.Sub == 1 ? f.ToString("0.##", ci) + " °C" : f.ToString("0.##", ci);
                case 10: return $"{d[0] & 0x1F:00}:{d[1] & 0x3F:00}:{d[2] & 0x3F:00}";
                case 11:
                    int yy = d[2] & 0x7F;
                    return $"{d[0] & 0x1F:00}.{d[1] & 0x0F:00}.{(yy >= 90 ? 1900 + yy : 2000 + yy)}";
                case 12: return BinaryPrimitives.ReadUInt32BigEndian(d).ToString(ci);
                case 13: return BinaryPrimitives.ReadInt32BigEndian(d).ToString(ci);
                case 14: return BinaryPrimitives.ReadSingleBigEndian(d).ToString("0.###", ci);
                case 16: return Encoding.Latin1.GetString(d).TrimEnd('\0');
                case 17: return $"Szene {(d[0] & 0x3F) + 1}";
                case 18: return $"{((d[0] & 0x80) != 0 ? "Lernen" : "Abrufen")} Szene {(d[0] & 0x3F) + 1}";
                case 20 when t.Sub == 102:
                    return d[0] switch { 0 => "Auto", 1 => "Komfort", 2 => "Standby", 3 => "Nacht", 4 => "Frost/Hitzeschutz", _ => d[0].ToString(ci) };
                case 232: return $"#{d[0]:X2}{d[1]:X2}{d[2]:X2}";
            }
        }
        catch (Exception) when (d.Length < 4)
        {
            // Payload shorter than the type expects: fall back to raw.
        }
        return value.ToString();
    }

    public static ushort EncodeFloat16(double value)
    {
        double v = value * 100.0;
        int e = 0;
        while (v < -2048.0 || v > 2047.0)
        {
            v /= 2.0;
            e++;
        }
        if (e > 15) throw new OverflowException("Wert außerhalb von DPT 9");
        int m = (int)Math.Round(v, MidpointRounding.AwayFromZero);
        if (m > 2047) { m >>= 1; e++; }
        if (m < -2048) { m /= 2; e++; }
        return (ushort)((m < 0 ? 0x8000 : 0) | (e << 11) | (m & 0x07FF));
    }

    public static double DecodeFloat16(ushort raw)
    {
        int m = raw & 0x07FF;
        if ((raw & 0x8000) != 0) m -= 2048;
        int e = (raw >> 11) & 0x0F;
        return 0.01 * m * (1 << e);
    }

    static GroupValue Be16(ushort v) => GroupValue.Bytes((byte)(v >> 8), (byte)v);

    static bool ParseBool(string s) => s.ToLowerInvariant() switch
    {
        "1" or "ein" or "an" or "on" or "true" or "ja" or "ab" or "zu" => true,
        "0" or "aus" or "off" or "false" or "nein" or "auf" or "offen" => false,
        _ => throw new FormatException($"'{s}' ist kein boolescher Wert (ein/aus, 1/0)"),
    };

    static double ParseDouble(string s) =>
        double.Parse(s.Replace(',', '.').TrimEnd('%', '°', 'C', ' '), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Raw hex such as "01" or "0C 33". A single value below 0x40 written as "$xx" is sent short.</summary>
    public static GroupValue ParseRaw(string text)
    {
        if (text.StartsWith('$')) return GroupValue.Short(Convert.ToByte(text[1..], 16));
        var bytes = text.Split(new[] { ' ', ',', '-' }, StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();
        return GroupValue.Bytes(bytes);
    }
}
