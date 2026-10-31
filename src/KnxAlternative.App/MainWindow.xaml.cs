using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using KnxAlternative.Core;
using KnxAlternative.Core.KnxIp;
using KnxAlternative.Core.Management;
using KnxAlternative.Core.Project;
using Microsoft.Win32;

namespace KnxAlternative.App;

public partial class MainWindow : Window
{
    KnxProject _project = new();
    string? _projectPath;
    bool _dirty;
    IKnxConnection? _bus;

    readonly ObservableCollection<GaRow> _gaRows = new();
    readonly ObservableCollection<Device> _devices = new();
    readonly ObservableCollection<MonitorRow> _monitor = new();
    readonly ICollectionView _gaView;
    readonly ICollectionView _deviceView;
    Predicate<GaRow>? _gaTreeFilter;
    Predicate<Device>? _deviceTreeFilter;

    static readonly string ArchiveFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KnxAlternative", "Archiv");

    public MainWindow()
    {
        InitializeComponent();
        GaGrid.ItemsSource = _gaRows;
        DeviceGrid.ItemsSource = _devices;
        MonitorGrid.ItemsSource = _monitor;
        _gaView = CollectionViewSource.GetDefaultView(_gaRows);
        _gaView.Filter = o => o is GaRow r && (_gaTreeFilter?.Invoke(r) ?? true) && MatchesText(r);
        _deviceView = CollectionViewSource.GetDefaultView(_devices);
        _deviceView.Filter = o => o is Device d && (_deviceTreeFilter?.Invoke(d) ?? true);

        InputBindings.Add(new KeyBinding(new RelayCommand(() => New_Click(this, new RoutedEventArgs())), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Open_Click(this, new RoutedEventArgs())), Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Save_Click(this, new RoutedEventArgs())), Key.S, ModifierKeys.Control));

        LoadProject(new KnxProject(), null);
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) Loaded += (_, _) => OpenFile(args[1]);
    }

    // ---------------------------------------------------------------- project

    void LoadProject(KnxProject p, string? path)
    {
        _project = p;
        _projectPath = path;
        _dirty = false;
        _gaRows.Clear();
        foreach (var g in p.AllGroupAddresses().OrderBy(g => g.Raw)) _gaRows.Add(new GaRow(g, p));
        _devices.Clear();
        foreach (var d in p.Devices.OrderBy(d => d.Area).ThenBy(d => d.Line).ThenBy(d => d.Address ?? ushort.MaxValue)) _devices.Add(d);
        _gaTreeFilter = null;
        _deviceTreeFilter = null;
        BuildTrees();
        RefreshViews();
        UpdateTitle();
    }

    void MarkDirty()
    {
        _dirty = true;
        UpdateTitle();
    }

    void UpdateTitle()
    {
        Title = $"KNX Alternative – {_project.Name}{(_dirty ? " *" : "")}";
        ProjectText.Text = $"{_project.Devices.Count} Geräte · {_gaRows.Count} Gruppenadressen{(_projectPath != null ? " · " + _projectPath : "")}";
    }

    void RefreshViews()
    {
        _gaView.Refresh();
        _deviceView.Refresh();
    }

    void BuildTrees()
    {
        BuildingTree.Items.Clear();
        foreach (var b in _project.Buildings) BuildingTree.Items.Add(SpaceNode(b));
        if (_project.Buildings.Count == 0) BuildingTree.Items.Add(new TreeViewItem { Header = "(keine Gebäudestruktur)", IsEnabled = false });

        TopologyTree.Items.Clear();
        foreach (var d in _project.Devices.Where(d => d.Address != null)) _project.GetOrCreateLine(d.Area, d.Line);
        foreach (var a in _project.Areas)
        {
            var an = new TreeViewItem { Header = $"{a.Address} {a.Name}", Tag = a, IsExpanded = true };
            foreach (var l in a.Lines)
            {
                var (area, line) = (a.Address, l.Address);
                var ln = new TreeViewItem { Header = $"{area}.{line} {l.Name} ({l.Medium})", Tag = (area, line), IsExpanded = false };
                foreach (var d in _project.Devices.Where(d => d.Area == area && d.Line == line).OrderBy(d => d.Address ?? ushort.MaxValue))
                    ln.Items.Add(new TreeViewItem { Header = $"{d.AddressText} {d.Name}", Tag = d });
                an.Items.Add(ln);
            }
            TopologyTree.Items.Add(an);
        }

        GroupTree.Items.Clear();
        foreach (var r in _project.GroupRanges) GroupTree.Items.Add(RangeNode(r));
    }

    TreeViewItem SpaceNode(BuildingSpace s)
    {
        var n = new TreeViewItem { Header = $"{s.Name} ({SpaceType(s.Type)})", Tag = s, IsExpanded = s.Type is "Building" or "Floor" };
        foreach (var c in s.Children) n.Items.Add(SpaceNode(c));
        foreach (var id in s.DeviceIds)
            if (_project.FindDevice(id) is { } d) n.Items.Add(new TreeViewItem { Header = $"{d.AddressText} {d.Name}", Tag = d });
        foreach (var f in s.Functions) n.Items.Add(new TreeViewItem { Header = $"ƒ {f.Name}", Tag = f });
        return n;
    }

    static string SpaceType(string t) => t switch
    {
        "Building" => "Gebäude", "Floor" => "Etage", "Room" => "Raum", "Corridor" => "Flur", "Stairway" => "Treppenhaus",
        "DistributionBoard" => "Verteiler", "BuildingPart" => "Gebäudeteil", _ => t,
    };

    TreeViewItem RangeNode(GroupRange r)
    {
        var start = new GroupAddress(r.Start);
        string addr = _project.GroupAddressStyle == GroupAddressStyle.ThreeLevel && r.End - r.Start < 256 ? $"{start.Main}/{start.Middle}" : $"{start.Main}";
        var n = new TreeViewItem { Header = $"{addr} {r.Name}", Tag = r };
        foreach (var c in r.Children) n.Items.Add(RangeNode(c));
        return n;
    }

    void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem { Tag: var tag }) return;
        switch (tag)
        {
            case GroupRange r:
                _gaTreeFilter = row => row.Entry.Raw >= r.Start && row.Entry.Raw <= r.End;
                MainTabs.SelectedIndex = 0;
                break;
            case TopologyArea a:
                _deviceTreeFilter = d => d.Area == a.Address;
                MainTabs.SelectedIndex = 1;
                break;
            case ValueTuple<int, int> al:
                _deviceTreeFilter = d => d.Area == al.Item1 && d.Line == al.Item2;
                MainTabs.SelectedIndex = 1;
                break;
            case BuildingSpace s:
                var ids = new HashSet<string>(AllDeviceIds(s));
                _deviceTreeFilter = d => ids.Contains(d.Id);
                var gas = new HashSet<string>(AllFunctions(s).SelectMany(f => f.GroupAddressIds));
                _gaTreeFilter = gas.Count > 0 ? row => gas.Contains(row.Entry.Id) : null;
                MainTabs.SelectedIndex = 1;
                break;
            case BuildingFunction f:
                var fg = new HashSet<string>(f.GroupAddressIds);
                _gaTreeFilter = row => fg.Contains(row.Entry.Id);
                MainTabs.SelectedIndex = 0;
                break;
            case Device d:
                _deviceTreeFilter = null;
                _deviceView.Refresh();
                DeviceGrid.SelectedItem = d;
                DeviceGrid.ScrollIntoView(d);
                MainTabs.SelectedIndex = 1;
                return;
        }
        RefreshViews();
    }

    static IEnumerable<string> AllDeviceIds(BuildingSpace s) => s.DeviceIds.Concat(s.Children.SelectMany(AllDeviceIds));
    static IEnumerable<BuildingFunction> AllFunctions(BuildingSpace s) => s.Functions.Concat(s.Children.SelectMany(AllFunctions));

    bool MatchesText(GaRow r)
    {
        var t = GaFilterBox?.Text;
        if (string.IsNullOrWhiteSpace(t)) return true;
        return r.AddressText.Contains(t, StringComparison.OrdinalIgnoreCase)
               || r.Entry.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
               || (r.Entry.Room?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
               || (r.Entry.Function?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    void GaFilter_Changed(object sender, TextChangedEventArgs e) => _gaView.Refresh();

    void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        MarkDirty();
        if (e.Row.Item is GaRow r) Dispatcher.BeginInvoke(r.Refresh);
    }

    bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show(this, "Das Projekt wurde geändert. Speichern?", "KNX Alternative", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return SaveProject(false);
        return true;
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var name = InputDialog.Ask(this, "Neues Projekt", "Projektname:", "Mein Haus");
        if (name == null) return;
        LoadProject(new KnxProject { Name = name }, null);
        MarkDirty();
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = "Alle Projekte|*.knxalt;*.knxproj|KNX Alternative (*.knxalt)|*.knxalt|ETS-Projekt (*.knxproj)|*.knxproj" };
        if (dlg.ShowDialog(this) == true) OpenFile(dlg.FileName);
    }

    void ImportEts_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = "ETS-Projekt (*.knxproj)|*.knxproj" };
        if (dlg.ShowDialog(this) == true) OpenFile(dlg.FileName);
    }

    void OpenFile(string path, string? password = null)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var p = ProjectStore.Open(path, password);
            bool isEts = path.EndsWith(".knxproj", StringComparison.OrdinalIgnoreCase);
            LoadProject(p, isEts ? null : path);
            if (isEts) MarkDirty();
            Status($"Geöffnet: {p.Name} ({p.Devices.Count} Geräte, {_gaRows.Count} Gruppenadressen)");
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("Passwort") || ex.Message.Contains("passwort"))
        {
            Mouse.OverrideCursor = null;
            var pw = InputDialog.Ask(this, "Projektpasswort", ex.Message, password: true);
            if (pw != null) OpenFile(path, pw);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    void Save_Click(object sender, RoutedEventArgs e) => SaveProject(false);
    void SaveAs_Click(object sender, RoutedEventArgs e) => SaveProject(true);

    bool SaveProject(bool askPath)
    {
        if (askPath || _projectPath == null)
        {
            var dlg = new SaveFileDialog { Filter = "KNX Alternative (*.knxalt)|*.knxalt", FileName = _project.Name + ProjectStore.Extension };
            if (dlg.ShowDialog(this) != true) return false;
            _projectPath = dlg.FileName;
        }
        ProjectStore.Save(_project, _projectPath);
        _dirty = false;
        UpdateTitle();
        Status($"Gespeichert: {_projectPath}");
        return true;
    }

    void Backup_Click(object sender, RoutedEventArgs e)
    {
        var f = ProjectStore.Backup(_project, ArchiveFolder);
        Status($"Sicherung erstellt: {f}");
    }

    void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = _project.Name + " Gruppenadressen.csv" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, ProjectStore.ExportGroupAddressesCsv(_project), new UTF8Encoding(true));
        Status($"Exportiert: {dlg.FileName}");
    }

    void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv" };
        if (dlg.ShowDialog(this) != true) return;
        int n = ProjectStore.ImportGroupAddressesCsv(_project, File.ReadAllText(dlg.FileName));
        LoadProject(_project, _projectPath);
        MarkDirty();
        Status($"{n} Gruppenadressen importiert");
    }

    void Exit_Click(object sender, RoutedEventArgs e) => Close();

    async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        // Automatic archive copy when closing, like ETS 6.4
        if (_project.Devices.Count > 0 || _gaRows.Count > 0)
        {
            try { ProjectStore.Backup(_project, ArchiveFolder); } catch (IOException) { }
        }
        if (_bus != null) await _bus.DisposeAsync();
    }

    // ---------------------------------------------------------------- group addresses

    void AddGa_Click(object sender, RoutedEventArgs e)
    {
        var next = NextFreeGroupAddress();
        var v = InputDialog.AskMany(this, "Neue Gruppenadresse",
            ("Adresse (z. B. 1/0/1):", next), ("Name:", ""), ("Datenpunkttyp (z. B. 1.001, 9.001):", "1.001"));
        if (v == null) return;
        if (!GroupAddress.TryParse(v[0], out var ga)) { Error("Ungültige Gruppenadresse"); return; }
        var entry = new GroupAddressEntry { Raw = ga.Raw, Name = v[1], DatapointType = DptId.TryParse(v[2], out var d) ? $"DPST-{d.Main}-{d.Sub}" : null };
        if (!ProjectStore.AddGroupAddress(_project, entry)) { Error($"{ga} existiert bereits"); return; }
        _gaRows.Add(new GaRow(entry, _project));
        BuildTrees();
        MarkDirty();
    }

    string NextFreeGroupAddress()
    {
        var used = _project.AllGroupAddresses().Select(g => g.Raw).ToHashSet();
        ushort start = (ushort)((GaGrid.SelectedItem as GaRow)?.Entry.Raw ?? 0x0800);
        for (int r = start; r < 0xFFFF; r++)
            if (r != 0 && !used.Contains((ushort)r)) return _project.FormatGroupAddress((ushort)r);
        return "";
    }

    void DeleteGa_Click(object sender, RoutedEventArgs e)
    {
        var rows = GaGrid.SelectedItems.OfType<GaRow>().ToList();
        if (rows.Count == 0) return;
        if (MessageBox.Show(this, $"{rows.Count} Gruppenadresse(n) löschen? Verknüpfungen werden entfernt.", "Löschen",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        foreach (var r in rows)
        {
            ProjectStore.RemoveGroupAddress(_project, r.Entry);
            _gaRows.Remove(r);
        }
        BuildTrees();
        MarkDirty();
    }

    async void WriteGa_Click(object sender, RoutedEventArgs e)
    {
        if (GaGrid.SelectedItem is not GaRow r || !RequireBus()) return;
        try
        {
            var value = r.Entry.Dpt is { } d ? Dpt.Encode(d, GaValueBox.Text) : Dpt.ParseRaw(GaValueBox.Text);
            bool ok = await new GroupClient(_bus!).WriteAsync(r.Entry.Address, value);
            Status(ok ? $"{r.AddressText} = {GaValueBox.Text} gesendet" : "Keine Bestätigung vom Bus");
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or KnxException) { Error(ex.Message); }
    }

    async void ReadGa_Click(object sender, RoutedEventArgs e)
    {
        if (GaGrid.SelectedItem is not GaRow r || !RequireBus()) return;
        Status($"Lese {r.AddressText} …");
        var v = await new GroupClient(_bus!).ReadAsync(r.Entry.Address);
        Status(v == null ? $"{r.AddressText}: keine Antwort" : $"{r.AddressText} = {Dpt.Decode(r.Entry.Dpt, v)}");
    }

    // ---------------------------------------------------------------- devices

    void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ComObjectGrid.ItemsSource = DeviceGrid.SelectedItem is Device d ? d.ComObjects.Select(c => new ComObjectRow(c, _project)).ToList() : null;
    }

    void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        var v = InputDialog.AskMany(this, "Neues Gerät", ("Name:", ""), ("Physikalische Adresse (z. B. 1.1.5):", SuggestAddress()),
            ("Hersteller:", ""), ("Bestellnummer:", ""));
        if (v == null) return;
        if (!IndividualAddress.TryParse(v[1], out var ia)) { Error("Ungültige physikalische Adresse"); return; }
        if (_project.Devices.Any(d => d.Address == ia.Raw)) { Error($"{ia} ist im Projekt bereits vergeben"); return; }
        var dev = new Device { Name = v[0], Address = ia.Raw, Area = ia.Area, Line = ia.Line, Manufacturer = NullIfEmpty(v[2]), OrderNumber = NullIfEmpty(v[3]) };
        _project.Devices.Add(dev);
        _devices.Add(dev);
        BuildTrees();
        MarkDirty();
    }

    string SuggestAddress()
    {
        var (area, line) = DeviceGrid.SelectedItem is Device s ? (s.Area, s.Line) : (1, 1);
        var used = _project.Devices.Where(d => d.Address != null).Select(d => d.Address!.Value).ToHashSet();
        for (int i = 1; i < 256; i++)
        {
            var a = IndividualAddress.From(area, line, i);
            if (!used.Contains(a.Raw)) return a.ToString();
        }
        return $"{area}.{line}.";
    }

    void DeleteDevice_Click(object sender, RoutedEventArgs e)
    {
        var sel = DeviceGrid.SelectedItems.OfType<Device>().ToList();
        if (sel.Count == 0) return;
        if (MessageBox.Show(this, $"{sel.Count} Gerät(e) aus dem Projekt löschen?", "Löschen", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        foreach (var d in sel)
        {
            _project.Devices.Remove(d);
            _devices.Remove(d);
            RemoveDeviceRefs(_project.Buildings, d.Id);
        }
        BuildTrees();
        MarkDirty();
    }

    static void RemoveDeviceRefs(IEnumerable<BuildingSpace> spaces, string id)
    {
        foreach (var s in spaces)
        {
            s.DeviceIds.Remove(id);
            RemoveDeviceRefs(s.Children, id);
        }
    }

    void LinkGa_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceGrid.SelectedItem is not Device d) { Error("Bitte zuerst ein Gerät auswählen"); return; }
        var co = ComObjectGrid.SelectedItem as ComObjectRow;
        var v = InputDialog.AskMany(this, "Gruppenadresse verknüpfen",
            ("Objektnummer:", co?.Number.ToString() ?? "0"), ("Gruppenadresse(n), mit Komma getrennt:", ""));
        if (v == null || !int.TryParse(v[0], out var num)) return;
        var obj = d.ComObjects.FirstOrDefault(c => c.Number == num);
        if (obj == null)
        {
            obj = new ComObject { Number = num, Name = $"Objekt {num}" };
            d.ComObjects.Add(obj);
            d.ComObjects.Sort((a, b) => a.Number.CompareTo(b.Number));
        }
        foreach (var part in v[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!GroupAddress.TryParse(part, out var ga)) { Error($"Ungültig: {part}"); continue; }
            if (_project.FindGroupAddress(ga.Raw) == null)
            {
                var entry = new GroupAddressEntry { Raw = ga.Raw, Name = $"{d.Name} {obj.Name}" };
                ProjectStore.AddGroupAddress(_project, entry);
                _gaRows.Add(new GaRow(entry, _project));
            }
            if (!obj.GroupAddresses.Contains(ga.Raw)) obj.GroupAddresses.Add(ga.Raw);
        }
        DeviceGrid_SelectionChanged(this, null!);
        foreach (var r in _gaRows) r.Refresh();
        BuildTrees();
        MarkDirty();
        Status("Verknüpft. Hinweis: Das Übertragen der Verknüpfungen ins Gerät folgt in Phase 2.");
    }

    async void ProgramAddress_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireBus()) return;
        var dev = DeviceGrid.SelectedItem as Device;
        var text = InputDialog.Ask(this, "Physikalische Adresse programmieren",
            "Neue physikalische Adresse. Nach OK die Programmiertaste am Gerät drücken (2 Minuten Zeit).",
            dev?.IndividualAddress?.ToString() ?? SuggestAddress());
        if (text == null) return;
        if (!IndividualAddress.TryParse(text, out var ia)) { Error("Ungültige physikalische Adresse"); return; }
        await RunBusy(async () =>
        {
            await new DeviceManagement(_bus!).ProgramIndividualAddressAsync(ia, new Progress<string>(Status));
            if (dev != null)
            {
                dev.Address = ia.Raw;
                dev.Area = ia.Area;
                dev.Line = ia.Line;
                dev.AddressLoaded = true;
                _deviceView.Refresh();
                BuildTrees();
                MarkDirty();
            }
            MessageBox.Show(this, $"Physikalische Adresse {ia} wurde programmiert.", "Fertig", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    async void ProgramBySerial_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireBus()) return;
        var dev = DeviceGrid.SelectedItem as Device;
        var v = InputDialog.AskMany(this, "Programmieren über Seriennummer",
            ("Seriennummer (12 Hex-Zeichen, z. B. 00FA12345678):", dev?.SerialNumber ?? ""),
            ("Neue physikalische Adresse:", dev?.IndividualAddress?.ToString() ?? SuggestAddress()));
        if (v == null) return;
        if (!IndividualAddress.TryParse(v[1], out var ia)) { Error("Ungültige physikalische Adresse"); return; }
        byte[] serial;
        try { serial = Convert.FromHexString(v[0].Replace(":", "").Replace(" ", "")); }
        catch (FormatException) { Error("Ungültige Seriennummer"); return; }
        await RunBusy(async () =>
        {
            await new DeviceManagement(_bus!).ProgramIndividualAddressBySerialAsync(serial, ia);
            if (dev != null) { dev.Address = ia.Raw; dev.Area = ia.Area; dev.Line = ia.Line; dev.AddressLoaded = true; dev.SerialNumber = v[0]; MarkDirty(); BuildTrees(); }
            Status($"Adresse {ia} über Seriennummer gesetzt");
        });
    }

    async void DeviceInfo_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireBus()) return;
        var ia = AskTargetAddress("Geräteinfo lesen");
        if (ia == null) return;
        await RunBusy(async () =>
        {
            var info = await new DeviceManagement(_bus!).ReadDeviceInfoAsync(ia.Value);
            var sb = new StringBuilder();
            sb.AppendLine($"Adresse: {info.Address}");
            sb.AppendLine($"Maskenversion: {info.MaskVersion:X4} ({info.MaskDescription})");
            if (info.ManufacturerId is { } m) sb.AppendLine($"Hersteller-ID: {m} (0x{m:X4})");
            if (info.SerialNumber != null) sb.AppendLine($"Seriennummer: {info.SerialNumber}");
            if (info.OrderInfo != null) sb.AppendLine($"Bestellinfo: {info.OrderInfo}");
            if (info.HardwareType != null) sb.AppendLine($"Hardwaretyp: {info.HardwareType}");
            if (info.ProgrammingMode != null) sb.AppendLine($"Programmiermodus: {(info.ProgrammingMode.Value ? "an" : "aus")}");
            Log(sb.ToString());
            if (DeviceGrid.SelectedItem is Device d && d.Address == ia.Value.Raw && info.SerialNumber != null && d.SerialNumber == null)
            {
                d.SerialNumber = info.SerialNumber;
                _deviceView.Refresh();
                MarkDirty();
            }
            MessageBox.Show(this, sb.ToString(), "Geräteinfo", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireBus()) return;
        var ia = AskTargetAddress("Gerät neu starten");
        if (ia == null) return;
        await RunBusy(async () =>
        {
            await new DeviceManagement(_bus!).RestartAsync(ia.Value);
            Status($"Neustart an {ia} gesendet");
        });
    }

    IndividualAddress? AskTargetAddress(string title)
    {
        var def = (DeviceGrid.SelectedItem as Device)?.IndividualAddress?.ToString() ?? "1.1.1";
        var t = InputDialog.Ask(this, title, "Physikalische Adresse des Geräts:", def);
        if (t == null) return null;
        if (IndividualAddress.TryParse(t, out var ia)) return ia;
        Error("Ungültige physikalische Adresse");
        return null;
    }

    // ---------------------------------------------------------------- diagnostics

    async void FindProgMode_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireBus()) return;
        await RunBusy(async () =>
        {
            var list = await new DeviceManagement(_bus!).FindDevicesInProgrammingModeAsync();
            var msg = list.Count == 0 ? "Kein Gerät im Programmiermodus." : "Im Programmiermodus: " + string.Join(", ", list);
            Log(msg);
            MessageBox.Show(this, msg, "Programmiermodus", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    async void CheckAddress_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireBus()) return;
        var ia = AskTargetAddress("Physikalische Adresse prüfen");
        if (ia == null) return;
        await RunBusy(async () =>
        {
            bool used = await new DeviceManagement(_bus!).IsAddressOccupiedAsync(ia.Value);
            var msg = used ? $"Unter {ia} antwortet ein Gerät." : $"Unter {ia} antwortet kein Gerät (Adresse frei).";
            Log(msg);
            MessageBox.Show(this, msg, "Adresse prüfen", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    async void ScanLine_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireBus()) return;
        var t = InputDialog.Ask(this, "Linie scannen", "Linie (Bereich.Linie), z. B. 1.1. Dauert einige Minuten.", "1.1");
        if (t == null) return;
        var p = t.Split('.');
        if (p.Length != 2 || !int.TryParse(p[0], out var area) || !int.TryParse(p[1], out var line)) { Error("Format: Bereich.Linie"); return; }
        MainTabs.SelectedIndex = 3;
        await RunBusy(async () =>
        {
            Log($"Scanne Linie {area}.{line} …");
            var found = await new DeviceManagement(_bus!).ScanLineAsync(area, line, new Progress<(int Done, IndividualAddress? Found)>(x =>
            {
                Status($"Linienscan {x.Done}/255");
                if (x.Found is { } a)
                    Log($"  gefunden: {a}{(_project.Devices.FirstOrDefault(d => d.Address == a.Raw) is { } d ? " – " + d.Name : " – nicht im Projekt")}");
            }));
            Log($"Linienscan fertig: {found.Count} Geräte.");
        });
    }

    // ---------------------------------------------------------------- connection

    async void Discover_Click(object sender, RoutedEventArgs e)
    {
        Status("Suche Schnittstellen …");
        var list = await KnxIpDiscovery.SearchAsync(TimeSpan.FromSeconds(3));
        InterfaceBox.Items.Clear();
        foreach (var d in list) InterfaceBox.Items.Add(d);
        if (list.Count > 0) InterfaceBox.SelectedIndex = 0;
        Status(list.Count == 0 ? "Keine Schnittstelle gefunden. Firewall für UDP 3671 prüfen oder IP-Adresse eintippen." : $"{list.Count} Schnittstelle(n) gefunden");
    }

    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_bus != null)
        {
            await _bus.DisposeAsync();
            _bus = null;
            return;
        }
        try
        {
            IKnxConnection bus;
            if (RoutingBox.IsChecked == true)
            {
                bus = new RoutingConnection(IndividualAddress.Parse("15.15.250"));
            }
            else
            {
                IPEndPoint ep;
                if (InterfaceBox.SelectedItem is KnxIpDevice dev) ep = dev.ControlEndpoint;
                else
                {
                    var text = InterfaceBox.Text.Trim();
                    if (text.Length == 0) { Error("Bitte IP-Adresse der Schnittstelle eingeben oder auf Suchen klicken."); return; }
                    var parts = text.Split(':');
                    var addr = IPAddress.TryParse(parts[0], out var a) ? a : (await Dns.GetHostAddressesAsync(parts[0])).First();
                    ep = new IPEndPoint(addr, parts.Length > 1 ? int.Parse(parts[1]) : KnxIpConstants.Port);
                }
                bus = new TunnelingConnection(ep);
            }
            bus.StateChanged += (_, s) => Dispatcher.BeginInvoke(() => OnBusState(s));
            bus.FrameReceived += (_, f) => Dispatcher.BeginInvoke(() => OnFrame(f));
            _bus = bus;
            OnBusState(ConnectionState.Connecting);
            await bus.ConnectAsync();
            Status($"Verbunden: {bus.Description}, eigene Adresse {bus.LocalAddress}");
        }
        catch (Exception ex) when (ex is KnxException or System.Net.Sockets.SocketException or FormatException)
        {
            _bus = null;
            OnBusState(ConnectionState.Disconnected);
            Error(ex.Message);
        }
    }

    void OnBusState(ConnectionState s)
    {
        StateDot.Fill = s switch { ConnectionState.Connected => Brushes.ForestGreen, ConnectionState.Connecting => Brushes.Orange, _ => Brushes.Gray };
        StateText.Text = s switch
        {
            ConnectionState.Connected => $"Verbunden ({_bus?.LocalAddress})",
            ConnectionState.Connecting => "Verbinde …",
            _ => "Nicht verbunden",
        };
        ConnectButton.Content = s == ConnectionState.Disconnected ? "Verbinden" : "Trennen";
        if (s == ConnectionState.Disconnected) _bus = null;
    }

    void OnFrame(CemiFrame f)
    {
        if (f.MessageCode == CemiMessageCode.LDataCon && f.ConfirmError) return;
        bool isGroup = f.IsGroupDestination && f.Destination != 0 && f.Service is Apci.GroupValueRead or Apci.GroupValueWrite or Apci.GroupValueResponse;
        GaRow? row = isGroup ? _gaRows.FirstOrDefault(r => r.Entry.Raw == f.Destination) : null;
        string value = "";
        if (f.GroupValue is { } gv)
        {
            value = Dpt.Decode(row?.Entry.Dpt, gv);
            if (row != null) row.LastValue = value;
        }
        if (MonitorPause.IsChecked == true) return;
        if (!isGroup && MonitorAllBox.IsChecked != true) return;
        var src = _project.Devices.FirstOrDefault(d => d.Address == f.Source.Raw);
        _monitor.Add(new MonitorRow(
            DateTime.Now,
            f.Source.ToString(),
            src?.Name,
            f.IsGroupDestination ? _project.FormatGroupAddress(f.Destination) : f.DestinationIndividual.ToString(),
            row?.Entry.Name,
            f.Service?.ToString() ?? f.Kind.ToString(),
            value,
            string.Join(" ", f.ToBytes().Select(b => b.ToString("X2")))));
        if (_monitor.Count > 20000) _monitor.RemoveAt(0);
        if (MainTabs.SelectedIndex == 2 && _monitor.Count > 0) MonitorGrid.ScrollIntoView(_monitor[^1]);
    }

    void ClearMonitor_Click(object sender, RoutedEventArgs e) => _monitor.Clear();

    void SaveMonitor_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"Gruppenmonitor {DateTime.Now:yyyy-MM-dd HH-mm}.csv" };
        if (dlg.ShowDialog(this) != true) return;
        var sb = new StringBuilder("Zeit;Quelle;Quellgerät;Ziel;Zielname;Dienst;Wert;Rohdaten\r\n");
        foreach (var r in _monitor)
            sb.Append($"{r.Time:yyyy-MM-dd HH:mm:ss.fff};{r.Source};{r.SourceName};{r.Destination};{r.DestinationName};{r.Service};{r.Value};{r.Raw}\r\n");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        Status($"Aufzeichnung gespeichert: {dlg.FileName}");
    }

    // ---------------------------------------------------------------- helpers

    bool RequireBus()
    {
        if (_bus?.State == ConnectionState.Connected) return true;
        Error("Bitte zuerst mit einer KNX-Schnittstelle verbinden.");
        return false;
    }

    async Task RunBusy(Func<Task> action)
    {
        IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is KnxException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            Log("Fehler: " + ex.Message);
            Error(ex.Message);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
        }
    }

    void Status(string text)
    {
        StatusText.Text = text;
        Log(text);
    }

    void Log(string text) => LogBox.AppendText($"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}");

    void Error(string text) => MessageBox.Show(this, text, "KNX Alternative", MessageBoxButton.OK, MessageBoxImage.Warning);

    static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "KNX Alternative 0.1 (Phase 1)\n\nOffene Alternative zur ETS für Windows 10/11.\n" +
        "Enthalten: ETS-Projektimport, Gebäude, Topologie, Gruppenadressen, Gruppenmonitor,\n" +
        "physikalische Adresse programmieren, Geräteinfo, Linienscan, KNXnet/IP Tunneling und Routing.\n\n" +
        "Noch nicht enthalten: Applikations- und Parameterdownload, KNX Secure, USB.",
        "Über", MessageBoxButton.OK, MessageBoxImage.Information);
}

sealed class RelayCommand(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
