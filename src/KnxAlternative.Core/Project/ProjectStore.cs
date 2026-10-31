using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnxAlternative.Core.Project;

/// <summary>Native project file (.knxalt, JSON) and group address CSV export/import.</summary>
public static class ProjectStore
{
    public const string Extension = ".knxalt";

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(KnxProject project, string path)
    {
        project.Modified = DateTime.Now;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(project, Options), Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    public static KnxProject Load(string path) =>
        JsonSerializer.Deserialize<KnxProject>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException("Projektdatei ist leer");

    /// <summary>Opens .knxalt or imports .knxproj based on the extension.</summary>
    public static KnxProject Open(string path, string? password = null) =>
        Path.GetExtension(path).Equals(".knxproj", StringComparison.OrdinalIgnoreCase)
            ? KnxprojImporter.Import(path, password)
            : Load(path);

    /// <summary>Copies the project into an archive folder with a timestamp (automatic backup like ETS 6.4).</summary>
    public static string Backup(KnxProject project, string archiveFolder)
    {
        Directory.CreateDirectory(archiveFolder);
        var safe = string.Concat(project.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var file = Path.Combine(archiveFolder, $"{safe}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{Extension}");
        Save(project, file);
        return file;
    }

    /// <summary>Group address CSV in the ETS export layout (3/1 format with ranges), separator ';'.</summary>
    public static string ExportGroupAddressesCsv(KnxProject project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\"Main\";\"Middle\";\"Sub\";\"Address\";\"Name\";\"Central\";\"Unfiltered\";\"Description\";\"DatapointType\";\"Security\"");
        foreach (var main in project.GroupRanges)
        {
            sb.AppendLine($"{Q(main.Name)};\"\";\"\";{Q(RangeText(main, project.GroupAddressStyle))};\"\";\"\";\"\";\"\";\"\";\"Auto\"");
            foreach (var mid in main.Children)
            {
                sb.AppendLine($"\"\";{Q(mid.Name)};\"\";{Q(RangeText(mid, project.GroupAddressStyle))};\"\";\"\";\"\";\"\";\"\";\"Auto\"");
                foreach (var g in mid.Addresses) sb.AppendLine(GaLine(g, project));
            }
            foreach (var g in main.Addresses) sb.AppendLine(GaLine(g, project));
        }
        return sb.ToString();

        static string RangeText(GroupRange r, GroupAddressStyle style)
        {
            var g = new GroupAddress(r.Start);
            return style == GroupAddressStyle.ThreeLevel && r.End - r.Start < 256 ? $"{g.Main}/{g.Middle}/-" : $"{g.Main}/-/-";
        }

        static string GaLine(GroupAddressEntry g, KnxProject p) =>
            $"\"\";\"\";{Q(g.Name)};{Q(p.FormatGroupAddress(g.Raw))};\"\";\"\";\"\";{Q(g.Description ?? "")};{Q(g.DatapointType ?? "")};\"Auto\"";
    }

    /// <summary>Reads a CSV with at least the columns Address and Name (ETS export or a simple list).</summary>
    public static int ImportGroupAddressesCsv(KnxProject project, string csv)
    {
        var lines = csv.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return 0;
        char sep = lines[0].Count(c => c == ';') >= lines[0].Count(c => c == ',') ? ';' : ',';
        var header = SplitCsv(lines[0], sep).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int iAddr = header.FindIndex(h => h is "address" or "adresse");
        int iName = header.FindIndex(h => h is "name" or "sub");
        int iDesc = header.FindIndex(h => h is "description" or "beschreibung");
        int iDpt = header.FindIndex(h => h is "datapointtype" or "dpt" or "datenpunkttyp");
        if (iAddr < 0) throw new InvalidDataException("Spalte 'Address' fehlt");
        int added = 0;
        foreach (var line in lines.Skip(1))
        {
            var f = SplitCsv(line, sep);
            if (iAddr >= f.Count || !GroupAddress.TryParse(f[iAddr], out var ga)) continue; // range rows like 1/-/-
            string name = iName >= 0 && iName < f.Count ? f[iName] : "";
            var entry = new GroupAddressEntry
            {
                Raw = ga.Raw,
                Name = name,
                Description = iDesc >= 0 && iDesc < f.Count && f[iDesc].Length > 0 ? f[iDesc] : null,
                DatapointType = iDpt >= 0 && iDpt < f.Count && f[iDpt].Length > 0 ? f[iDpt] : null,
            };
            if (AddGroupAddress(project, entry)) added++;
        }
        return added;
    }

    /// <summary>Adds a group address to the matching main/middle range, creating ranges as needed.</summary>
    public static bool AddGroupAddress(KnxProject project, GroupAddressEntry entry)
    {
        if (project.AllGroupAddresses().Any(g => g.Raw == entry.Raw)) return false;
        var a = entry.Address;
        ushort mainStart = (ushort)(a.Main << 11);
        var main = project.GroupRanges.FirstOrDefault(r => r.Start == mainStart);
        if (main == null)
        {
            main = new GroupRange { Name = $"Hauptgruppe {a.Main}", Start = mainStart, End = (ushort)(mainStart + 2047) };
            project.GroupRanges.Add(main);
            project.GroupRanges.Sort((x, y) => x.Start.CompareTo(y.Start));
        }
        var target = main;
        if (project.GroupAddressStyle == GroupAddressStyle.ThreeLevel)
        {
            ushort midStart = (ushort)(mainStart | (a.Middle << 8));
            var mid = main.Children.FirstOrDefault(r => r.Start == midStart);
            if (mid == null)
            {
                mid = new GroupRange { Name = $"Mittelgruppe {a.Main}/{a.Middle}", Start = midStart, End = (ushort)(midStart + 255) };
                main.Children.Add(mid);
                main.Children.Sort((x, y) => x.Start.CompareTo(y.Start));
            }
            target = mid;
        }
        target.Addresses.Add(entry);
        target.Addresses.Sort((x, y) => x.Raw.CompareTo(y.Raw));
        return true;
    }

    public static bool RemoveGroupAddress(KnxProject project, GroupAddressEntry entry)
    {
        bool Remove(GroupRange r) => r.Addresses.Remove(entry) || r.Children.Any(Remove);
        bool removed = project.GroupRanges.Any(Remove);
        if (removed)
            foreach (var co in project.Devices.SelectMany(d => d.ComObjects)) co.GroupAddresses.Remove(entry.Raw);
        return removed;
    }

    static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    static List<string> SplitCsv(string line, char sep)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == sep) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }
}
