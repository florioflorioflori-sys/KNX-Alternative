using System.Net;
using System.Text;
using KnxAlternative.Core;
using KnxAlternative.Core.KnxIp;
using KnxAlternative.Core.Management;
using KnxAlternative.Core.Project;

Console.OutputEncoding = Encoding.UTF8;

var opts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
var pos = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--"))
    {
        var key = args[i][2..];
        string? val = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
        opts[key] = val;
    }
    else pos.Add(args[i]);
}

if (pos.Count == 0 || opts.ContainsKey("help"))
{
    PrintHelp();
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    switch (pos[0].ToLowerInvariant())
    {
        case "discover":
        {
            Console.WriteLine("Suche KNX-IP-Schnittstellen (3 s) …");
            var devs = await KnxIpDiscovery.SearchAsync(TimeSpan.FromSeconds(3), cts.Token);
            if (devs.Count == 0) Console.WriteLine("Keine gefunden. Tipp: Firewall für UDP 3671 freigeben.");
            foreach (var d in devs) Console.WriteLine($"  {d}");
            return 0;
        }
        case "project":
        {
            Need(2, "project <datei.knxproj|.knxalt>");
            var p = ProjectStore.Open(pos[1], opts.GetValueOrDefault("password"));
            Console.WriteLine($"Projekt: {p.Name}");
            Console.WriteLine($"Geräte: {p.Devices.Count}, Gruppenadressen: {p.AllGroupAddresses().Count()}");
            foreach (var d in p.Devices.OrderBy(d => d.Address ?? ushort.MaxValue))
                Console.WriteLine($"  {d.AddressText,-10} {d.Name} | {d.Manufacturer} {d.OrderNumber} | {d.ApplicationProgram}");
            if (opts.TryGetValue("save", out var save) && save != null) { ProjectStore.Save(p, save); Console.WriteLine($"Gespeichert: {save}"); }
            if (opts.TryGetValue("csv", out var csv) && csv != null)
            {
                File.WriteAllText(csv, ProjectStore.ExportGroupAddressesCsv(p), new UTF8Encoding(true));
                Console.WriteLine($"Gruppenadressen exportiert: {csv}");
            }
            return 0;
        }
    }

    await using var bus = CreateConnection();
    bus.StateChanged += (_, s) => { if (s == ConnectionState.Disconnected) Console.Error.WriteLine("Verbindung getrennt."); };
    await bus.ConnectAsync(cts.Token);
    Console.Error.WriteLine($"Verbunden: {bus.Description}, eigene Adresse {bus.LocalAddress}");
    var mgmt = new DeviceManagement(bus);
    var group = new GroupClient(bus);

    switch (pos[0].ToLowerInvariant())
    {
        case "monitor":
        {
            KnxProject? project = opts.TryGetValue("project", out var pf) && pf != null ? ProjectStore.Open(pf, opts.GetValueOrDefault("password")) : null;
            Console.WriteLine("Gruppenmonitor läuft, Strg+C beendet.");
            bus.FrameReceived += (_, f) =>
            {
                if (f.MessageCode == CemiMessageCode.LDataCon && !opts.ContainsKey("own")) return;
                string dst = f.IsGroupDestination ? (project?.FormatGroupAddress(f.Destination) ?? f.DestinationGroup.ToString()) : f.DestinationIndividual.ToString();
                var ga = f.IsGroupDestination ? project?.FindGroupAddress(f.Destination) : null;
                string value = f.GroupValue is { } gv ? Dpt.Decode(ga?.Dpt, gv) : string.Join(" ", f.Data.Select(b => b.ToString("X2")));
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {f.Source,-10} → {dst,-9} {f.Service?.ToString() ?? f.Kind.ToString(),-22} {value,-12} {ga?.Name}");
            };
            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
            break;
        }
        case "write":
        {
            Need(4, "write <gruppenadresse> <dpt> <wert>   z. B. write 1/0/1 1.001 ein");
            var ga = GroupAddress.Parse(pos[1]);
            var value = DptId.TryParse(pos[2], out var dpt) ? Dpt.Encode(dpt, pos[3]) : Dpt.ParseRaw(pos[3]);
            bool ok = await group.WriteAsync(ga, value, cts.Token);
            Console.WriteLine(ok ? $"Gesendet: {ga} = {value}" : "Senden fehlgeschlagen (keine Bestätigung).");
            return ok ? 0 : 2;
        }
        case "read":
        {
            Need(2, "read <gruppenadresse> [dpt]");
            var ga = GroupAddress.Parse(pos[1]);
            DptId? dpt = pos.Count > 2 && DptId.TryParse(pos[2], out var d) ? d : null;
            var v = await group.ReadAsync(ga, ct: cts.Token);
            Console.WriteLine(v == null ? "Keine Antwort." : $"{ga} = {Dpt.Decode(dpt, v)} ({v})");
            return v == null ? 2 : 0;
        }
        case "progmode":
        {
            var list = await mgmt.FindDevicesInProgrammingModeAsync(ct: cts.Token);
            Console.WriteLine(list.Count == 0 ? "Kein Gerät im Programmiermodus." : "Im Programmiermodus: " + string.Join(", ", list));
            return 0;
        }
        case "set-address":
        {
            Need(2, "set-address <neue physikalische adresse>   z. B. set-address 1.1.5");
            var ia = IndividualAddress.Parse(pos[1]);
            if (opts.TryGetValue("serial", out var serial) && serial != null)
            {
                await mgmt.ProgramIndividualAddressBySerialAsync(Convert.FromHexString(serial), ia, cts.Token);
                Console.WriteLine($"Adresse {ia} über Seriennummer gesetzt.");
            }
            else
            {
                await mgmt.ProgramIndividualAddressAsync(ia, new Progress<string>(Console.WriteLine), ct: cts.Token);
                await Task.Delay(100);
            }
            return 0;
        }
        case "info":
        {
            Need(2, "info <physikalische adresse>");
            var info = await mgmt.ReadDeviceInfoAsync(IndividualAddress.Parse(pos[1]), cts.Token);
            Console.WriteLine($"Adresse:       {info.Address}");
            Console.WriteLine($"Maskenversion: {info.MaskVersion:X4} ({info.MaskDescription})");
            if (info.ManufacturerId is { } m) Console.WriteLine($"Hersteller-ID: {m} (0x{m:X4})");
            if (info.SerialNumber != null) Console.WriteLine($"Seriennummer:  {info.SerialNumber}");
            if (info.OrderInfo != null) Console.WriteLine($"Bestellinfo:   {info.OrderInfo}");
            if (info.HardwareType != null) Console.WriteLine($"Hardwaretyp:   {info.HardwareType}");
            if (info.ProgrammingMode != null) Console.WriteLine($"Prog.-Modus:   {(info.ProgrammingMode.Value ? "an" : "aus")}");
            return 0;
        }
        case "restart":
            Need(2, "restart <physikalische adresse>");
            await mgmt.RestartAsync(IndividualAddress.Parse(pos[1]), cts.Token);
            Console.WriteLine("Neustart gesendet.");
            return 0;
        case "scan":
        {
            Need(2, "scan <bereich.linie>   z. B. scan 1.1");
            var p = pos[1].Split('.');
            int area = int.Parse(p[0]), line = int.Parse(p[1]);
            Console.WriteLine($"Scanne Linie {area}.{line} (1..255), das dauert einige Minuten …");
            var found = await mgmt.ScanLineAsync(area, line, new Progress<(int, IndividualAddress?)>(x =>
            {
                if (x.Item2 is { } a) Console.WriteLine($"  gefunden: {a}");
            }), ct: cts.Token);
            Console.WriteLine($"{found.Count} Geräte gefunden.");
            return 0;
        }
        default:
            Console.Error.WriteLine($"Unbekannter Befehl: {pos[0]}");
            PrintHelp();
            return 1;
    }
    return 0;
}
catch (Exception ex) when (ex is KnxException or FormatException or InvalidDataException or IOException or ArgumentException)
{
    Console.Error.WriteLine($"Fehler: {ex.Message}");
    return 2;
}
catch (OperationCanceledException)
{
    return 130;
}

IKnxConnection CreateConnection()
{
    IPAddress? local = opts.TryGetValue("local-ip", out var l) && l != null ? IPAddress.Parse(l) : null;
    if (opts.ContainsKey("routing"))
    {
        var own = opts.TryGetValue("address", out var a) && a != null ? IndividualAddress.Parse(a) : IndividualAddress.Parse("15.15.250");
        return new RoutingConnection(own, local);
    }
    if (!opts.TryGetValue("ip", out var ip) || ip == null)
        throw new ArgumentException("Bitte --ip <adresse der IP-Schnittstelle> oder --routing angeben (Schnittstellen finden: knx discover).");
    var parts = ip.Split(':');
    var host = parts[0];
    int port = parts.Length > 1 ? int.Parse(parts[1]) : KnxIpConstants.Port;
    var addr = IPAddress.TryParse(host, out var parsed) ? parsed : Dns.GetHostAddresses(host).First();
    return new TunnelingConnection(new IPEndPoint(addr, port), local, natMode: !opts.ContainsKey("no-nat"));
}

void Need(int count, string usage)
{
    if (pos.Count < count) throw new ArgumentException($"Aufruf: knx {usage}");
}

static void PrintHelp()
{
    Console.WriteLine("""
        KNX Alternative – Kommandozeile

        Verbindung:  --ip <host[:port]>   IP-Schnittstelle (Tunneling)
                     --routing [--address 15.15.250]   IP-Router (Multicast)
                     --local-ip <ip>      Netzwerkkarte wählen

        Befehle:
          discover                         IP-Schnittstellen im Netz suchen
          monitor [--project datei]        Gruppenmonitor (mit Projekt: Namen und Werte)
          write <ga> <dpt> <wert>          Wert senden, z. B. write 1/0/1 1.001 ein
          read <ga> [dpt]                  Wert lesen
          progmode                         Geräte im Programmiermodus anzeigen
          set-address <pa> [--serial hex]  Physikalische Adresse programmieren
          info <pa>                        Geräteinformationen lesen
          restart <pa>                     Gerät neu starten
          scan <bereich.linie>             Linie nach Geräten durchsuchen
          project <datei> [--password pw] [--save x.knxalt] [--csv ga.csv]
                                           ETS-Projekt (.knxproj) einlesen und anzeigen
        """);
}
