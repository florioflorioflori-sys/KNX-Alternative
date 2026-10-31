using System.IO.Compression;
using System.Text;
using KnxAlternative.Core;
using KnxAlternative.Core.KnxIp;
using KnxAlternative.Core.Management;
using KnxAlternative.Core.Project;
using Xunit;

namespace KnxAlternative.Tests;

public class TunnelingTests
{
    [Fact]
    public async Task Connects_and_gets_tunnel_address()
    {
        await using var server = new FakeKnxIpServer();
        await using var conn = new TunnelingConnection(server.Endpoint);
        await conn.ConnectAsync();
        Assert.Equal(ConnectionState.Connected, conn.State);
        Assert.Equal("1.1.250", conn.LocalAddress.ToString());
    }

    [Fact]
    public async Task Group_write_and_read()
    {
        await using var server = new FakeKnxIpServer();
        await using var conn = new TunnelingConnection(server.Endpoint);
        await conn.ConnectAsync();
        var group = new GroupClient(conn);
        var ga = GroupAddress.Parse("1/2/3");
        Assert.True(await group.WriteAsync(ga, new DptId(9, 1), "21.5"));
        var v = await group.ReadAsync(ga);
        Assert.NotNull(v);
        Assert.Equal("21.5 °C", Dpt.Decode(new DptId(9, 1), v!));
    }

    [Fact]
    public async Task Programs_individual_address()
    {
        await using var server = new FakeKnxIpServer();
        await using var conn = new TunnelingConnection(server.Endpoint);
        await conn.ConnectAsync();
        var mgmt = new DeviceManagement(conn);

        var inProg = await mgmt.FindDevicesInProgrammingModeAsync(TimeSpan.FromMilliseconds(300));
        Assert.Equal(new[] { IndividualAddress.Parse("15.15.255") }, inProg);

        var log = new List<string>();
        await mgmt.ProgramIndividualAddressAsync(IndividualAddress.Parse("1.1.5"), new SyncProgress(log.Add), TimeSpan.FromSeconds(5));
        Assert.Equal("1.1.5", server.DeviceAddress.ToString());
        Assert.Equal(1, server.RestartCount);
        Assert.False(server.DeviceInProgrammingMode);
        Assert.Contains(log, l => l.Contains("erfolgreich"));
    }

    [Fact]
    public async Task Reads_device_info()
    {
        await using var server = new FakeKnxIpServer();
        await using var conn = new TunnelingConnection(server.Endpoint);
        await conn.ConnectAsync();
        var info = await new DeviceManagement(conn).ReadDeviceInfoAsync(IndividualAddress.Parse("15.15.255"));
        Assert.Equal(0x07B0, info.MaskVersion);
        Assert.Equal("System B", info.MaskDescription);
        Assert.Equal("008312345678", info.SerialNumber);
    }

    [Fact]
    public async Task Missing_device_is_reported_as_free()
    {
        await using var server = new FakeKnxIpServer();
        await using var conn = new TunnelingConnection(server.Endpoint);
        await conn.ConnectAsync();
        Assert.False(await new DeviceManagement(conn).IsAddressOccupiedAsync(IndividualAddress.Parse("1.1.9")));
    }

    sealed class SyncProgress(Action<string> a) : IProgress<string>
    {
        public void Report(string value) => a(value);
    }
}

public class ProjectTests
{
    const string Ns = "http://knx.org/xml/project/23";

    static byte[] BuildKnxproj()
    {
        var project = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <KNX xmlns="{Ns}">
              <Project Id="P-0ABC">
                <Installations>
                  <Installation Name="">
                    <Topology>
                      <Area Id="P-0ABC-0_A-1" Name="Haus" Address="1">
                        <Line Id="P-0ABC-0_L-1" Name="EG" Address="1" MediumTypeRefId="MT-0">
                          <Segment Id="P-0ABC-0_S-1" Number="0" MediumTypeRefId="MT-0">
                            <DeviceInstance Id="P-0ABC-0_DI-1" Name="Aktor Küche" Address="5" ProductRefId="M-0083_H-1_P-1" Hardware2ProgramRefId="M-0083_H-1_HP-1" SerialNumber="AIMSNFZ4" IndividualAddressLoaded="true">
                              <ComObjectInstanceRefs>
                                <ComObjectInstanceRef RefId="O-0_R-1" Links="GA-1" />
                                <ComObjectInstanceRef RefId="O-1_R-2" Links="GA-2 GA-1" />
                              </ComObjectInstanceRefs>
                            </DeviceInstance>
                          </Segment>
                        </Line>
                      </Area>
                    </Topology>
                    <Locations>
                      <Space Id="P-0ABC-0_BP-1" Type="Building" Name="Haus">
                        <Space Id="P-0ABC-0_BP-2" Type="Floor" Name="EG">
                          <Space Id="P-0ABC-0_BP-3" Type="Room" Name="Küche">
                            <DeviceInstanceRef RefId="P-0ABC-0_DI-1" />
                            <Function Id="P-0ABC-0_F-1" Name="Deckenlicht" Type="FT-1">
                              <GroupAddressRef Id="P-0ABC-0_F-1_GR-1" RefId="P-0ABC-0_GA-1" Role="SwitchOnOff" />
                            </Function>
                          </Space>
                        </Space>
                      </Space>
                    </Locations>
                    <GroupAddresses>
                      <GroupRanges>
                        <GroupRange Id="P-0ABC-0_GR-1" Name="Licht" RangeStart="2048" RangeEnd="4095">
                          <GroupRange Id="P-0ABC-0_GR-2" Name="EG" RangeStart="2048" RangeEnd="2303">
                            <GroupAddress Id="P-0ABC-0_GA-1" Name="Küche Licht schalten" Address="2049" DatapointType="DPST-1-1" />
                            <GroupAddress Id="P-0ABC-0_GA-2" Name="Küche Licht Status" Address="2050" DatapointType="DPST-1-1" />
                          </GroupRange>
                        </GroupRange>
                      </GroupRanges>
                    </GroupAddresses>
                  </Installation>
                </Installations>
              </Project>
            </KNX>
            """;
        var info = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <KNX xmlns="{Ns}"><Project Id="P-0ABC"><ProjectInformation Name="Testhaus" GroupAddressStyle="ThreeLevel" /></Project></KNX>
            """;
        var master = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <KNX xmlns="{Ns}"><MasterData><Manufacturers><Manufacturer Id="M-0083" Name="MDT technologies" /></Manufacturers></MasterData></KNX>
            """;
        var hardware = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <KNX xmlns="{Ns}"><ManufacturerData><Manufacturer RefId="M-0083"><Hardware>
              <Hardware Id="M-0083_H-1"><Products><Product Id="M-0083_H-1_P-1" Text="Schaltaktor 8-fach" OrderNumber="AKS-0816.03" /></Products>
                <Hardware2Programs><Hardware2Program Id="M-0083_H-1_HP-1"><ApplicationProgramRef RefId="M-0083_A-0001-10-ABCD" /></Hardware2Program></Hardware2Programs>
              </Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
            """;
        var application = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <KNX xmlns="{Ns}"><ManufacturerData><Manufacturer RefId="M-0083"><ApplicationPrograms>
              <ApplicationProgram Id="M-0083_A-0001-10-ABCD" Name="Schaltaktor" ApplicationVersion="18"><Static><ComObjectTable>
                <ComObject Id="M-0083_A-0001-10-ABCD_O-0" Name="Kanal A" Text="Kanal A" FunctionText="Schalten" Number="0" ObjectSize="1 Bit" DatapointType="DPST-1-1" CommunicationFlag="Enabled" WriteFlag="Enabled" />
                <ComObject Id="M-0083_A-0001-10-ABCD_O-1" Name="Kanal A" Text="Kanal A" FunctionText="Status" Number="1" ObjectSize="1 Bit" CommunicationFlag="Enabled" ReadFlag="Enabled" TransmitFlag="Enabled" />
              </ComObjectTable><ComObjectRefs>
                <ComObjectRef Id="M-0083_A-0001-10-ABCD_O-0_R-1" RefId="M-0083_A-0001-10-ABCD_O-0" />
                <ComObjectRef Id="M-0083_A-0001-10-ABCD_O-1_R-2" RefId="M-0083_A-0001-10-ABCD_O-1" />
              </ComObjectRefs></Static></ApplicationProgram>
            </ApplicationPrograms></Manufacturer></ManufacturerData></KNX>
            """;

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Add("knx_master.xml", master);
            Add("P-0ABC/0.xml", project);
            Add("P-0ABC/project.xml", info);
            Add("M-0083/Hardware.xml", hardware);
            Add("M-0083/M-0083_A-0001-10-ABCD.xml", application);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Imports_ets6_project()
    {
        var p = KnxprojImporter.Import(new MemoryStream(BuildKnxproj()));
        Assert.Equal("Testhaus", p.Name);
        Assert.Single(p.Devices);
        var d = p.Devices[0];
        Assert.Equal("1.1.5", d.AddressText);
        Assert.Equal("MDT technologies", d.Manufacturer);
        Assert.Equal("Schaltaktor 8-fach", d.ProductName);
        Assert.Equal("AKS-0816.03", d.OrderNumber);
        Assert.Equal("Schaltaktor 1.2", d.ApplicationProgram);
        Assert.Equal("008312345678", d.SerialNumber);
        Assert.Equal(2, d.ComObjects.Count);
        Assert.Equal("Schalten", d.ComObjects[0].Function);
        Assert.Equal("KS", d.ComObjects[0].Flags);
        Assert.Equal(new ushort[] { 2050, 2049 }, d.ComObjects[1].GroupAddresses);

        var gas = p.AllGroupAddresses().ToList();
        Assert.Equal(2, gas.Count);
        Assert.Equal("1/0/1", gas[0].Address.ToString());
        Assert.Equal("Küche", gas[0].Room);
        Assert.Equal("Deckenlicht", gas[0].Function);

        Assert.Equal("Haus", p.Buildings[0].Name);
        Assert.Contains("P-0ABC-0_DI-1", p.Buildings[0].Children[0].Children[0].DeviceIds);
    }

    [Fact]
    public void Saves_and_loads_native_format_and_csv()
    {
        var p = KnxprojImporter.Import(new MemoryStream(BuildKnxproj()));
        var file = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}{ProjectStore.Extension}");
        try
        {
            ProjectStore.Save(p, file);
            var q = ProjectStore.Load(file);
            Assert.Equal(p.Devices[0].ComObjects[1].GroupAddresses, q.Devices[0].ComObjects[1].GroupAddresses);

            var csv = ProjectStore.ExportGroupAddressesCsv(q);
            var fresh = new KnxProject();
            Assert.Equal(2, ProjectStore.ImportGroupAddressesCsv(fresh, csv));
            Assert.Equal("Küche Licht Status", fresh.FindGroupAddress(2050)!.Name);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Ets6_password_derivation_is_stable()
    {
        var a = KnxprojImporter.Ets6ZipPassword("test");
        Assert.Equal(44, a.Length);
        Assert.Equal(a, KnxprojImporter.Ets6ZipPassword("test"));
    }
}
