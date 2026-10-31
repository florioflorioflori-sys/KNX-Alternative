using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ICSharpCode.SharpZipLib.Zip;

namespace KnxAlternative.Core.Project;

/// <summary>
/// Reads ETS project exports (.knxproj, ETS 5 and ETS 6). Password-protected projects
/// are supported when the password is given.
/// </summary>
public static class KnxprojImporter
{
    public static KnxProject Import(string path, string? password = null)
    {
        using var fs = File.OpenRead(path);
        return Import(fs, password);
    }

    public static KnxProject Import(Stream stream, string? password = null)
    {
        using var outer = new ZipFile(stream) { IsStreamOwner = false };
        var files = new Dictionary<string, Func<Stream>>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipEntry e in outer)
            if (e.IsFile)
            {
                var entry = e;
                files[entry.Name] = () => outer.GetInputStream(entry);
            }

        var projectXmlName = files.Keys.FirstOrDefault(k => IsProjectFile(k, "0.xml"));
        XDocument projectDoc;
        XDocument? infoDoc = null;
        if (projectXmlName != null)
        {
            projectDoc = Load(files[projectXmlName]);
            var infoName = files.Keys.FirstOrDefault(k => IsProjectFile(k, "project.xml"));
            if (infoName != null) infoDoc = Load(files[infoName]);
        }
        else
        {
            var innerName = files.Keys.FirstOrDefault(k => k.StartsWith("P-", StringComparison.OrdinalIgnoreCase)
                                                           && k.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("Keine Projektdaten in der Datei gefunden. Ist es ein ETS-Projektexport (.knxproj)?");
            var ms = new MemoryStream();
            using (var s = files[innerName]()) s.CopyTo(ms);
            (projectDoc, infoDoc) = ReadInner(ms, password);
        }

        var manufacturers = ReadManufacturerNames(files);
        var hardware = ReadHardware(files);
        var appCache = new Dictionary<string, ApplicationData?>();
        ApplicationData? App(string id)
        {
            if (!appCache.TryGetValue(id, out var a))
                appCache[id] = a = ReadApplication(files, id);
            return a;
        }

        return BuildProject(projectDoc, infoDoc, manufacturers, hardware, App);
    }

    static bool IsProjectFile(string entry, string fileName)
    {
        var parts = entry.Split('/');
        return parts.Length == 2 && parts[0].StartsWith("P-", StringComparison.OrdinalIgnoreCase) && parts[1].Equals(fileName, StringComparison.OrdinalIgnoreCase);
    }

    static (XDocument project, XDocument? info) ReadInner(MemoryStream ms, string? password)
    {
        var candidates = new List<string?> { null };
        if (password != null)
        {
            candidates.Add(password);
            candidates.Add(Ets6ZipPassword(password));
        }
        Exception? last = null;
        foreach (var pw in candidates)
        {
            ms.Position = 0;
            try
            {
                using var zip = new ZipFile(ms) { IsStreamOwner = false, Password = pw };
                XDocument? project = null, info = null;
                foreach (ZipEntry e in zip)
                {
                    if (!e.IsFile) continue;
                    var name = Path.GetFileName(e.Name);
                    if (name.Equals("0.xml", StringComparison.OrdinalIgnoreCase)) project = Load(() => zip.GetInputStream(e));
                    else if (name.Equals("project.xml", StringComparison.OrdinalIgnoreCase)) info = Load(() => zip.GetInputStream(e));
                }
                if (project != null) return (project, info);
            }
            catch (Exception ex) when (ex is ZipException or XmlException or InvalidDataException or IOException)
            {
                last = ex;
            }
        }
        throw new InvalidDataException(password == null
            ? "Das Projekt ist passwortgeschützt. Bitte Projektpasswort angeben."
            : "Projektpasswort falsch oder Projekt nicht lesbar.", last);
    }

    /// <summary>ETS 6 derives the archive password from the project password.</summary>
    public static string Ets6ZipPassword(string password)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.Unicode.GetBytes(password),
            Encoding.ASCII.GetBytes("21.project.ets.knx.org"), 65536, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(key);
    }

    static XDocument Load(Func<Stream> open)
    {
        using var s = open();
        return XDocument.Load(s);
    }

    static IEnumerable<XElement> E(this XElement e, string local) => e.Elements().Where(x => x.Name.LocalName == local);
    static IEnumerable<XElement> D(this XContainer e, string local) => e.Descendants().Where(x => x.Name.LocalName == local);
    static string? A(this XElement e, string name) => e.Attribute(name)?.Value;

    static Dictionary<string, string> ReadManufacturerNames(Dictionary<string, Func<Stream>> files)
    {
        var result = new Dictionary<string, string>();
        var master = files.Keys.FirstOrDefault(k => k.Equals("knx_master.xml", StringComparison.OrdinalIgnoreCase));
        if (master == null) return result;
        try
        {
            var doc = Load(files[master]);
            foreach (var m in doc.D("Manufacturer"))
                if (m.A("Id") is { } id && m.A("Name") is { } name) result[id] = name;
        }
        catch (XmlException) { }
        return result;
    }

    sealed record ProductInfo(string Text, string? OrderNumber);
    sealed record HardwareData(Dictionary<string, ProductInfo> Products, Dictionary<string, string> Hw2Program);

    static HardwareData ReadHardware(Dictionary<string, Func<Stream>> files)
    {
        var products = new Dictionary<string, ProductInfo>();
        var h2p = new Dictionary<string, string>();
        foreach (var name in files.Keys.Where(k => k.EndsWith("/Hardware.xml", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var doc = Load(files[name]);
                foreach (var p in doc.D("Product"))
                    if (p.A("Id") is { } id) products[id] = new ProductInfo(p.A("Text") ?? id, p.A("OrderNumber"));
                foreach (var hp in doc.D("Hardware2Program"))
                {
                    var appRef = hp.E("ApplicationProgramRef").FirstOrDefault()?.A("RefId");
                    if (hp.A("Id") is { } id && appRef != null) h2p[id] = appRef;
                }
            }
            catch (XmlException) { }
        }
        return new HardwareData(products, h2p);
    }

    sealed record ComObjectDef(int Number, string? Name, string? Text, string? FunctionText, string? Dpt, string? Size, string Flags);
    sealed record ApplicationData(string Id, string Name, string? Version, Dictionary<string, ComObjectDef> ComObjects, Dictionary<string, ComObjectDef> ComObjectRefs);

    static ApplicationData? ReadApplication(Dictionary<string, Func<Stream>> files, string appId)
    {
        var manu = appId.Split('_')[0];
        var file = files.Keys.FirstOrDefault(k => k.StartsWith(manu + "/", StringComparison.OrdinalIgnoreCase)
                                                  && Path.GetFileNameWithoutExtension(k).StartsWith(appId, StringComparison.OrdinalIgnoreCase)
                                                  && k.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        if (file == null) return null;
        try
        {
            using var s = files[file]();
            using var r = XmlReader.Create(s, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore });
            string name = appId;
            string? version = null;
            var cos = new Dictionary<string, ComObjectDef>();
            var refs = new Dictionary<string, ComObjectDef>();
            var pendingRefs = new List<(string Id, string RefId, string? Name, string? Text, string? Func, string? Dpt, string? Size, string? Flags)>();
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element) continue;
                switch (r.LocalName)
                {
                    case "ApplicationProgram":
                        name = r.GetAttribute("Name") ?? appId;
                        var v = r.GetAttribute("ApplicationVersion");
                        if (int.TryParse(v, out var vi)) version = $"{vi >> 4}.{vi & 0x0F}";
                        break;
                    case "ComObject":
                        if (r.GetAttribute("Id") is { } cid)
                        {
                            int.TryParse(r.GetAttribute("Number"), out var num);
                            cos[cid] = new ComObjectDef(num, r.GetAttribute("Name"), r.GetAttribute("Text"), r.GetAttribute("FunctionText"),
                                r.GetAttribute("DatapointType"), r.GetAttribute("ObjectSize"), ReadFlags(r));
                        }
                        break;
                    case "ComObjectRef":
                        if (r.GetAttribute("Id") is { } rid && r.GetAttribute("RefId") is { } rref)
                            pendingRefs.Add((rid, rref, r.GetAttribute("Name"), r.GetAttribute("Text"), r.GetAttribute("FunctionText"),
                                r.GetAttribute("DatapointType"), r.GetAttribute("ObjectSize"), HasFlags(r) ? ReadFlags(r) : null));
                        break;
                }
            }
            foreach (var p in pendingRefs)
            {
                cos.TryGetValue(p.RefId, out var b);
                refs[p.Id] = new ComObjectDef(b?.Number ?? 0, p.Name ?? b?.Name, p.Text ?? b?.Text, p.Func ?? b?.FunctionText,
                    p.Dpt ?? b?.Dpt, p.Size ?? b?.Size, p.Flags ?? b?.Flags ?? "");
            }
            return new ApplicationData(appId, name, version, cos, refs);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    static readonly (string Attr, char Flag)[] FlagMap =
    [
        ("CommunicationFlag", 'K'), ("ReadFlag", 'L'), ("WriteFlag", 'S'), ("TransmitFlag", 'Ü'), ("UpdateFlag", 'A'), ("ReadOnInitFlag", 'I'),
    ];

    static bool HasFlags(XmlReader r) => FlagMap.Any(f => r.GetAttribute(f.Attr) != null);

    static string ReadFlags(XmlReader r) =>
        new(FlagMap.Where(f => r.GetAttribute(f.Attr) == "Enabled").Select(f => f.Flag).ToArray());

    static KnxProject BuildProject(XDocument doc, XDocument? info, Dictionary<string, string> manufacturers, HardwareData hw,
        Func<string, ApplicationData?> app)
    {
        var root = doc.Root ?? throw new InvalidDataException("Leere Projektdatei");
        var projectEl = root.D("Project").FirstOrDefault() ?? throw new InvalidDataException("Kein <Project> gefunden");
        var project = new KnxProject { ProjectId = projectEl.A("Id") };

        var pi = info?.Root?.D("ProjectInformation").FirstOrDefault();
        if (pi != null)
        {
            project.Name = pi.A("Name") ?? project.Name;
            project.GroupAddressStyle = pi.A("GroupAddressStyle") switch
            {
                "TwoLevel" => GroupAddressStyle.TwoLevel,
                "Free" => GroupAddressStyle.Free,
                _ => GroupAddressStyle.ThreeLevel,
            };
            if (DateTime.TryParse(pi.A("LastModified"), out var lm)) project.Modified = lm;
        }

        var installation = projectEl.D("Installation").FirstOrDefault() ?? throw new InvalidDataException("Keine Installation im Projekt");

        // Group addresses
        var gaById = new Dictionary<string, GroupAddressEntry>();
        var gaRoot = installation.E("GroupAddresses").FirstOrDefault()?.E("GroupRanges").FirstOrDefault();
        if (gaRoot != null)
            foreach (var gr in gaRoot.E("GroupRange"))
                project.GroupRanges.Add(ReadRange(gr, gaById));
        GroupAddressEntry? FindGa(string reference)
        {
            if (gaById.TryGetValue(reference, out var g)) return g;
            var suffix = "_" + reference;
            return gaById.FirstOrDefault(kv => kv.Key.EndsWith(suffix, StringComparison.Ordinal)).Value;
        }

        // Topology and devices
        var deviceById = new Dictionary<string, Device>();
        var topo = installation.E("Topology").FirstOrDefault();
        if (topo != null)
        {
            foreach (var areaEl in topo.E("Area"))
            {
                int.TryParse(areaEl.A("Address"), out var area);
                var a = new TopologyArea { Address = area, Name = areaEl.A("Name") ?? $"Bereich {area}" };
                project.Areas.Add(a);
                foreach (var lineEl in areaEl.E("Line"))
                {
                    int.TryParse(lineEl.A("Address"), out var line);
                    a.Lines.Add(new TopologyLine
                    {
                        Address = line,
                        Name = lineEl.A("Name") ?? $"Linie {area}.{line}",
                        Medium = MediumName(lineEl.A("MediumTypeRefId")),
                    });
                    // ETS6: Line/Segment/DeviceInstance, ETS5: Line/DeviceInstance
                    var devs = lineEl.E("DeviceInstance").Concat(lineEl.E("Segment").SelectMany(s => s.E("DeviceInstance")));
                    foreach (var dEl in devs)
                    {
                        var d = ReadDevice(dEl, area, line, manufacturers, hw, app, FindGa);
                        project.Devices.Add(d);
                        deviceById[d.Id] = d;
                    }
                }
            }
            // Devices not yet placed in a line
            foreach (var dEl in topo.E("UnassignedDevices").SelectMany(u => u.E("DeviceInstance")))
            {
                var d = ReadDevice(dEl, 0, 0, manufacturers, hw, app, FindGa);
                d.Address = null;
                project.Devices.Add(d);
                deviceById[d.Id] = d;
            }
        }

        // Building structure: ETS6 <Locations><Space>, ETS5 <Buildings><BuildingPart>
        var locations = installation.E("Locations").FirstOrDefault();
        if (locations != null)
            foreach (var s in locations.E("Space")) project.Buildings.Add(ReadSpace(s, "Space", project, deviceById, FindGa, null));
        var buildings = installation.E("Buildings").FirstOrDefault();
        if (buildings != null)
            foreach (var s in buildings.E("BuildingPart")) project.Buildings.Add(ReadSpace(s, "BuildingPart", project, deviceById, FindGa, null));

        return project;
    }

    static string MediumName(string? id) => id switch
    {
        "MT-0" => "TP",
        "MT-1" => "PL110",
        "MT-2" => "RF",
        "MT-5" => "IP",
        _ => "TP",
    };

    static GroupRange ReadRange(XElement el, Dictionary<string, GroupAddressEntry> gaById)
    {
        var r = new GroupRange
        {
            Name = el.A("Name") ?? "",
            Start = ushort.TryParse(el.A("RangeStart"), out var s) ? s : (ushort)0,
            End = ushort.TryParse(el.A("RangeEnd"), out var e) ? e : (ushort)0,
        };
        foreach (var c in el.E("GroupRange")) r.Children.Add(ReadRange(c, gaById));
        foreach (var g in el.E("GroupAddress"))
        {
            var ga = new GroupAddressEntry
            {
                Id = g.A("Id") ?? Guid.NewGuid().ToString("N"),
                Raw = ushort.TryParse(g.A("Address"), out var a) ? a : (ushort)0,
                Name = g.A("Name") ?? "",
                Description = g.A("Description"),
                DatapointType = g.A("DatapointType"),
            };
            r.Addresses.Add(ga);
            gaById[ga.Id] = ga;
        }
        return r;
    }

    static Device ReadDevice(XElement el, int area, int line, Dictionary<string, string> manufacturers, HardwareData hw,
        Func<string, ApplicationData?> app, Func<string, GroupAddressEntry?> findGa)
    {
        var productRef = el.A("ProductRefId");
        var h2p = el.A("Hardware2ProgramRefId");
        hw.Products.TryGetValue(productRef ?? "", out var product);
        string? appId = h2p != null && hw.Hw2Program.TryGetValue(h2p, out var ap) ? ap : null;
        var appData = appId != null ? app(appId) : null;
        var manuId = (productRef ?? h2p ?? "").Split('_')[0];

        var d = new Device
        {
            Id = el.A("Id") ?? Guid.NewGuid().ToString("N"),
            Name = el.A("Name") ?? "",
            Description = el.A("Description"),
            Area = area,
            Line = line,
            Manufacturer = manufacturers.TryGetValue(manuId, out var mn) ? mn : (manuId.Length > 0 ? manuId : null),
            ProductName = product?.Text,
            OrderNumber = product?.OrderNumber,
            ApplicationProgram = appData != null ? $"{appData.Name}{(appData.Version != null ? " " + appData.Version : "")}" : appId,
            ApplicationProgramId = appId,
            SerialNumber = DecodeSerial(el.A("SerialNumber")),
            AddressLoaded = el.A("IndividualAddressLoaded") == "true",
            ApplicationLoaded = el.A("ApplicationProgramLoaded") == "true",
            ParametersLoaded = el.A("ParametersLoaded") == "true",
            CommunicationLoaded = el.A("CommunicationPartLoaded") == "true",
        };
        if (int.TryParse(el.A("Address"), out var dev) && dev is >= 0 and <= 255)
            d.Address = IndividualAddress.From(area, line, dev).Raw;
        if (string.IsNullOrEmpty(d.Name)) d.Name = d.ProductName ?? "Gerät";

        foreach (var co in el.D("ComObjectInstanceRef"))
        {
            var refId = co.A("RefId") ?? "";
            ComObjectDef? def = null;
            if (appData != null)
            {
                if (!appData.ComObjectRefs.TryGetValue(refId, out def))
                    appData.ComObjectRefs.TryGetValue(appData.Id + "_" + refId, out def);
            }
            var obj = new ComObject
            {
                Number = def?.Number ?? ParseObjectNumber(refId),
                Name = co.A("Text") ?? def?.Text ?? def?.Name ?? refId,
                Function = co.A("FunctionText") ?? def?.FunctionText,
                DatapointType = co.A("DatapointType") ?? def?.Dpt,
                ObjectSize = def?.Size,
                Flags = def?.Flags ?? "",
            };
            var links = (co.A("Links") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            // ETS5: <Connectors><Send GroupAddressRefId/><Receive/></Connectors>
            links.AddRange(co.D("Send").Concat(co.D("Receive")).Select(x => x.A("GroupAddressRefId")).OfType<string>());
            foreach (var l in links)
                if (findGa(l) is { } ga && !obj.GroupAddresses.Contains(ga.Raw)) obj.GroupAddresses.Add(ga.Raw);
            if (obj.GroupAddresses.Count > 0 || co.A("IsActive") != "false") d.ComObjects.Add(obj);
        }
        d.ComObjects.Sort((a, b) => a.Number.CompareTo(b.Number));
        return d;
    }

    static int ParseObjectNumber(string refId)
    {
        // "O-12_R-1" → 12
        var i = refId.IndexOf("O-", StringComparison.Ordinal);
        if (i < 0) return 0;
        var digits = new string(refId[(i + 2)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    static string? DecodeSerial(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        try { return Convert.ToHexString(Convert.FromBase64String(s)); }
        catch (FormatException) { return s; }
    }

    static BuildingSpace ReadSpace(XElement el, string elementName, KnxProject project, Dictionary<string, Device> devices,
        Func<string, GroupAddressEntry?> findGa, string? roomName)
    {
        var space = new BuildingSpace { Name = el.A("Name") ?? "", Type = el.A("Type") ?? "Room" };
        var currentRoom = space.Type is "Room" or "Corridor" or "Stairway" ? space.Name : roomName;
        foreach (var r in el.E("DeviceInstanceRef"))
            if (r.A("RefId") is { } id && devices.ContainsKey(id)) space.DeviceIds.Add(id);
        foreach (var f in el.E("Function"))
        {
            var fn = new BuildingFunction { Name = f.A("Name") ?? "", Type = f.A("Type") };
            foreach (var gr in f.E("GroupAddressRef"))
            {
                if (gr.A("RefId") is { } rid && findGa(rid) is { } ga)
                {
                    fn.GroupAddressIds.Add(ga.Id);
                    ga.Room ??= currentRoom ?? space.Name;
                    ga.Function ??= fn.Name;
                }
            }
            space.Functions.Add(fn);
        }
        foreach (var c in el.E(elementName)) space.Children.Add(ReadSpace(c, elementName, project, devices, findGa, currentRoom));
        return space;
    }
}
