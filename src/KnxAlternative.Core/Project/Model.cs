using System.Text.Json.Serialization;

namespace KnxAlternative.Core.Project;

/// <summary>An installation: building, topology, group addresses and devices.</summary>
public sealed class KnxProject
{
    public string Name { get; set; } = "Neues Projekt";
    public string? ProjectId { get; set; }
    public GroupAddressStyle GroupAddressStyle { get; set; } = GroupAddressStyle.ThreeLevel;
    public DateTime Modified { get; set; } = DateTime.Now;
    public List<TopologyArea> Areas { get; set; } = new();
    public List<GroupRange> GroupRanges { get; set; } = new();
    public List<BuildingSpace> Buildings { get; set; } = new();
    public List<Device> Devices { get; set; } = new();

    public IEnumerable<GroupAddressEntry> AllGroupAddresses() => GroupRanges.SelectMany(r => r.AllAddresses());

    public GroupAddressEntry? FindGroupAddress(ushort raw) => AllGroupAddresses().FirstOrDefault(g => g.Raw == raw);

    public Device? FindDevice(string id) => Devices.FirstOrDefault(d => d.Id == id);

    public string FormatGroupAddress(ushort raw) => new GroupAddress(raw).ToString(GroupAddressStyle);

    /// <summary>Ensures every device's line exists in the topology.</summary>
    public TopologyLine GetOrCreateLine(int area, int line)
    {
        var a = Areas.FirstOrDefault(x => x.Address == area);
        if (a == null)
        {
            a = new TopologyArea { Address = area, Name = $"Bereich {area}" };
            Areas.Add(a);
            Areas.Sort((x, y) => x.Address.CompareTo(y.Address));
        }
        var l = a.Lines.FirstOrDefault(x => x.Address == line);
        if (l == null)
        {
            l = new TopologyLine { Address = line, Name = $"Linie {area}.{line}" };
            a.Lines.Add(l);
            a.Lines.Sort((x, y) => x.Address.CompareTo(y.Address));
        }
        return l;
    }
}

public sealed class TopologyArea
{
    public int Address { get; set; }
    public string Name { get; set; } = "";
    public List<TopologyLine> Lines { get; set; } = new();
}

public sealed class TopologyLine
{
    public int Address { get; set; }
    public string Name { get; set; } = "";
    public string Medium { get; set; } = "TP";
}

public sealed class GroupRange
{
    public string Name { get; set; } = "";
    public ushort Start { get; set; }
    public ushort End { get; set; }
    public List<GroupRange> Children { get; set; } = new();
    public List<GroupAddressEntry> Addresses { get; set; } = new();

    public IEnumerable<GroupAddressEntry> AllAddresses() => Addresses.Concat(Children.SelectMany(c => c.AllAddresses()));
}

public sealed class GroupAddressEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ushort Raw { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? DatapointType { get; set; }
    public string? Room { get; set; }
    public string? Function { get; set; }

    [JsonIgnore] public GroupAddress Address => new(Raw);
    [JsonIgnore] public DptId? Dpt => DptId.TryParse(DatapointType, out var d) ? d : null;
}

public sealed class BuildingSpace
{
    public string Name { get; set; } = "";
    /// <summary>Building, Floor, Room, Stairway, Corridor, DistributionBoard, BuildingPart …</summary>
    public string Type { get; set; } = "Room";
    public List<BuildingSpace> Children { get; set; } = new();
    public List<string> DeviceIds { get; set; } = new();
    public List<BuildingFunction> Functions { get; set; } = new();
}

public sealed class BuildingFunction
{
    public string Name { get; set; } = "";
    public string? Type { get; set; }
    public List<string> GroupAddressIds { get; set; } = new();
}

public sealed class Device
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Physical address, null while not yet assigned.</summary>
    public ushort? Address { get; set; }
    public int Area { get; set; }
    public int Line { get; set; }
    public string? Manufacturer { get; set; }
    public string? ProductName { get; set; }
    public string? OrderNumber { get; set; }
    public string? ApplicationProgram { get; set; }
    public string? ApplicationProgramId { get; set; }
    public string? SerialNumber { get; set; }
    public bool AddressLoaded { get; set; }
    public bool ApplicationLoaded { get; set; }
    public bool ParametersLoaded { get; set; }
    public bool CommunicationLoaded { get; set; }
    public List<ComObject> ComObjects { get; set; } = new();

    [JsonIgnore] public IndividualAddress? IndividualAddress => Address is { } a ? new IndividualAddress(a) : null;
    [JsonIgnore] public string AddressText => IndividualAddress?.ToString() ?? $"{Area}.{Line}.-";
}

public sealed class ComObject
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string? Function { get; set; }
    public string? DatapointType { get; set; }
    public string? ObjectSize { get; set; }
    /// <summary>C R W T U I flags as text, e.g. "KLÜ" style is shown by the UI.</summary>
    public string Flags { get; set; } = "";
    /// <summary>Linked group addresses (raw); the first one is the sending address.</summary>
    public List<ushort> GroupAddresses { get; set; } = new();
}
