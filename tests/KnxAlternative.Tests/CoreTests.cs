using KnxAlternative.Core;
using Xunit;

namespace KnxAlternative.Tests;

public class AddressTests
{
    [Theory]
    [InlineData("1.1.1", 0x1101)]
    [InlineData("15.15.255", 0xFFFF)]
    [InlineData("0.0.0", 0x0000)]
    public void IndividualAddress_roundtrip(string text, int raw)
    {
        var a = IndividualAddress.Parse(text);
        Assert.Equal(raw, a.Raw);
        Assert.Equal(text, a.ToString());
    }

    [Theory]
    [InlineData("1.16.1")]
    [InlineData("1.1")]
    [InlineData("a.b.c")]
    public void IndividualAddress_rejects_invalid(string text) => Assert.False(IndividualAddress.TryParse(text, out _));

    [Theory]
    [InlineData("1/0/1", 0x0801)]
    [InlineData("31/7/255", 0xFFFF)]
    [InlineData("1/257", 0x0901)]
    [InlineData("2049", 0x0801)]
    public void GroupAddress_parse(string text, int raw) => Assert.Equal(raw, GroupAddress.Parse(text).Raw);

    [Fact]
    public void GroupAddress_formats()
    {
        var g = new GroupAddress(0x0901);
        Assert.Equal("1/1/1", g.ToString());
        Assert.Equal("1/257", g.ToString(GroupAddressStyle.TwoLevel));
        Assert.Equal("2305", g.ToString(GroupAddressStyle.Free));
    }
}

public class DptTests
{
    [Theory]
    [InlineData(21.5, 0x0C33)]
    [InlineData(-1.0, 0x879C)]
    [InlineData(0.0, 0x0000)]
    public void Float16_encode(double v, int raw) => Assert.Equal(raw, Dpt.EncodeFloat16(v));

    [Theory]
    [InlineData(21.5)]
    [InlineData(-30)]
    [InlineData(1000)]
    [InlineData(670760)]
    public void Float16_roundtrip(double v) =>
        Assert.InRange(Dpt.DecodeFloat16(Dpt.EncodeFloat16(v)), v - Math.Abs(v) * 0.01 - 0.01, v + Math.Abs(v) * 0.01 + 0.01);

    [Fact]
    public void Switch_is_short()
    {
        var v = Dpt.Encode(new DptId(1, 1), "ein");
        Assert.True(v.IsShort);
        Assert.Equal(1, v.Data[0]);
        Assert.Equal("Ein", Dpt.Decode(new DptId(1, 1), v));
    }

    [Fact]
    public void Percent_scaling()
    {
        var v = Dpt.Encode(new DptId(5, 1), "100");
        Assert.Equal(255, v.Data[0]);
        Assert.Equal("50.2 %", Dpt.Decode(new DptId(5, 1), GroupValue.Bytes(128)));
    }

    [Theory]
    [InlineData("DPST-9-1", 9, 1)]
    [InlineData("DPT-1", 1, 0)]
    [InlineData("5.001", 5, 1)]
    [InlineData("DPST-1-1 DPST-1-8", 1, 1)]
    public void DptId_parse(string text, int main, int sub)
    {
        Assert.True(DptId.TryParse(text, out var id));
        Assert.Equal(new DptId(main, sub), id);
    }
}

public class CemiTests
{
    [Fact]
    public void GroupWrite_switch_matches_reference_bytes()
    {
        var f = CemiFrame.GroupWrite(GroupAddress.Parse("1/0/1"), GroupValue.Short(1));
        // L_Data.req, no add info, ctrl BC E0, src 0.0.0, dst 1/0/1, len 1, TPCI 00, APCI 81
        Assert.Equal(Convert.FromHexString("1100BCE000000801010081"), f.ToBytes());
    }

    [Fact]
    public void GroupWrite_float_roundtrip()
    {
        var f = CemiFrame.GroupWrite(GroupAddress.Parse("3/2/1"), Dpt.Encode(new DptId(9, 1), "21.5"));
        var p = CemiFrame.Parse(f.ToBytes());
        Assert.Equal(Apci.GroupValueWrite, p.Service);
        Assert.False(p.ShortData);
        Assert.Equal(new byte[] { 0x0C, 0x33 }, p.Data);
        Assert.Equal("3/2/1", p.DestinationGroup.ToString());
    }

    [Fact]
    public void IndividualAddressWrite_broadcast()
    {
        var f = CemiFrame.Broadcast(Apci.IndividualAddressWrite, 0x11, 0x05);
        var b = f.ToBytes();
        Assert.Equal(Convert.FromHexString("1100B0E0000000000300C01105"), b);
        var p = CemiFrame.Parse(b);
        Assert.Equal(Apci.IndividualAddressWrite, p.Service);
        Assert.Equal(new byte[] { 0x11, 0x05 }, p.Data);
    }

    [Fact]
    public void Transport_control_frames()
    {
        var connect = new CemiFrame { Destination = 0x1105, Kind = TpduKind.Connect, Priority = Priority.System };
        Assert.Equal(TpduKind.Connect, CemiFrame.Parse(connect.ToBytes()).Kind);
        var ack = new CemiFrame { Destination = 0x1105, Kind = TpduKind.Ack, Sequence = 5 };
        var pa = CemiFrame.Parse(ack.ToBytes());
        Assert.Equal(TpduKind.Ack, pa.Kind);
        Assert.Equal(5, pa.Sequence);
    }

    [Fact]
    public void DeviceDescriptorResponse_parse()
    {
        var f = new CemiFrame
        {
            MessageCode = CemiMessageCode.LDataInd, Destination = 0x11FA, Kind = TpduKind.DataConnected, Sequence = 0,
            Service = Apci.DeviceDescriptorResponse, Data = [0x00, 0x07, 0xB0], ShortData = true,
        };
        var p = CemiFrame.Parse(f.ToBytes());
        Assert.Equal(Apci.DeviceDescriptorResponse, p.Service);
        Assert.Equal(new byte[] { 0x00, 0x07, 0xB0 }, p.Data);
    }
}
