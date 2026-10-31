using System.ComponentModel;
using System.Runtime.CompilerServices;
using KnxAlternative.Core;
using KnxAlternative.Core.Project;

namespace KnxAlternative.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class GaRow : Observable
{
    readonly KnxProject _project;
    string? _lastValue;

    public GaRow(GroupAddressEntry entry, KnxProject project)
    {
        Entry = entry;
        _project = project;
    }

    public GroupAddressEntry Entry { get; }
    public string AddressText => _project.FormatGroupAddress(Entry.Raw);
    public string DptText => Entry.Dpt is { } d ? Dpt.Describe(d) : "";
    public int LinkCount => _project.Devices.SelectMany(d => d.ComObjects).Count(c => c.GroupAddresses.Contains(Entry.Raw));

    public string? LastValue
    {
        get => _lastValue;
        set { _lastValue = value; Raise(); }
    }

    public void Refresh()
    {
        Raise(nameof(DptText));
        Raise(nameof(LinkCount));
    }
}

public sealed class ComObjectRow
{
    public ComObjectRow(ComObject c, KnxProject p)
    {
        Source = c;
        Links = string.Join(", ", c.GroupAddresses.Select(g => p.FormatGroupAddress(g)));
    }

    public ComObject Source { get; }
    public int Number => Source.Number;
    public string Name => Source.Name;
    public string? Function => Source.Function;
    public string? ObjectSize => Source.ObjectSize;
    public string? DatapointType => Source.DatapointType;
    public string Flags => Source.Flags;
    public string Links { get; }
}

public sealed record MonitorRow(
    DateTime Time,
    string Source,
    string? SourceName,
    string Destination,
    string? DestinationName,
    string Service,
    string Value,
    string Raw);
