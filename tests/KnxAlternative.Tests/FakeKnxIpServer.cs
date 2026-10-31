using System.Net;
using System.Net.Sockets;
using KnxAlternative.Core;
using KnxAlternative.Core.KnxIp;

namespace KnxAlternative.Tests;

/// <summary>
/// Minimal KNXnet/IP tunneling server on loopback with one simulated bus device,
/// so the client and management code can be tested without hardware.
/// </summary>
public sealed class FakeKnxIpServer : IAsyncDisposable
{
    readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    readonly CancellationTokenSource _cts = new();
    readonly Task _loop;
    IPEndPoint? _client;
    byte _sendSeq;
    readonly IndividualAddress _tunnelAddress = IndividualAddress.Parse("1.1.250");

    // Simulated device
    public IndividualAddress DeviceAddress { get; private set; } = IndividualAddress.Parse("15.15.255");
    public bool DeviceInProgrammingMode { get; set; } = true;
    public int RestartCount { get; private set; }
    public ushort MaskVersion { get; set; } = 0x07B0;
    public Dictionary<ushort, byte[]> GroupValues { get; } = new();
    public List<CemiFrame> ReceivedFrames { get; } = new();
    int _deviceRecvSeq, _deviceSendSeq;
    bool _deviceConnected;

    public FakeKnxIpServer() => _loop = Task.Run(Loop);

    public IPEndPoint Endpoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            if (!KnxIpFrame.TryParse(r.Buffer, out var st, out var bodyMem)) continue;
            var body = bodyMem.ToArray();
            switch (st)
            {
                case ServiceType.ConnectRequest:
                    _client = r.RemoteEndPoint;
                    await Send(ServiceType.ConnectResponse, [0x07, 0x00], KnxIpFrame.Hpai(Endpoint),
                        [0x04, 0x04, (byte)(_tunnelAddress.Raw >> 8), (byte)_tunnelAddress.Raw]);
                    break;
                case ServiceType.ConnectionStateRequest:
                    await Send(ServiceType.ConnectionStateResponse, [body[0], 0x00]);
                    break;
                case ServiceType.DisconnectRequest:
                    await Send(ServiceType.DisconnectResponse, [body[0], 0x00]);
                    break;
                case ServiceType.TunnelingRequest:
                    await Send(ServiceType.TunnelingAck, [0x04, body[1], body[2], 0x00]);
                    var req = CemiFrame.Parse(body.AsSpan(4));
                    lock (ReceivedFrames) ReceivedFrames.Add(req);
                    bool delivered = !(req is { IsGroupDestination: false } && req.DestinationIndividual != DeviceAddress);
                    await Indicate(Copy(req, CemiMessageCode.LDataCon, _tunnelAddress, confirmError: !delivered));
                    if (delivered) await Simulate(req);
                    break;
            }
        }
    }

    static CemiFrame Copy(CemiFrame f, CemiMessageCode code, IndividualAddress src, bool confirmError = false) => new()
    {
        MessageCode = code, Source = src, Destination = f.Destination, IsGroupDestination = f.IsGroupDestination,
        Priority = f.Priority, Kind = f.Kind, Sequence = f.Sequence, Service = f.Service, Data = f.Data, ShortData = f.ShortData,
        ConfirmError = confirmError,
    };

    async Task Simulate(CemiFrame f)
    {
        if (f.IsGroupDestination && f.Destination == 0)
        {
            if (f.Service == Apci.IndividualAddressRead && DeviceInProgrammingMode)
                await FromDevice(new CemiFrame { Destination = 0, IsGroupDestination = true, Kind = TpduKind.DataGroup, Service = Apci.IndividualAddressResponse });
            else if (f.Service == Apci.IndividualAddressWrite && DeviceInProgrammingMode)
                DeviceAddress = new IndividualAddress((ushort)((f.Data[0] << 8) | f.Data[1]));
            return;
        }
        if (f.IsGroupDestination)
        {
            if (f.Service == Apci.GroupValueWrite) GroupValues[f.Destination] = f.Data;
            else if (f.Service == Apci.GroupValueRead && GroupValues.TryGetValue(f.Destination, out var v))
                await FromDevice(new CemiFrame
                {
                    Destination = f.Destination, IsGroupDestination = true, Kind = TpduKind.DataGroup,
                    Service = Apci.GroupValueResponse, Data = v, ShortData = v.Length == 1 && v[0] < 0x40,
                });
            return;
        }

        // Point-to-point to the simulated device
        var tunnel = _tunnelAddress.Raw;
        switch (f.Kind)
        {
            case TpduKind.Connect:
                _deviceConnected = true; _deviceRecvSeq = 0; _deviceSendSeq = 0;
                break;
            case TpduKind.Disconnect:
                _deviceConnected = false;
                break;
            case TpduKind.DataConnected when _deviceConnected:
                await FromDevice(new CemiFrame { Destination = tunnel, Kind = TpduKind.Ack, Sequence = f.Sequence });
                if (f.Sequence != _deviceRecvSeq) break;
                _deviceRecvSeq = (_deviceRecvSeq + 1) & 0x0F;
                if (f.Service == Apci.DeviceDescriptorRead)
                    await FromDevice(new CemiFrame
                    {
                        Destination = tunnel, Kind = TpduKind.DataConnected, Sequence = _deviceSendSeq,
                        Service = Apci.DeviceDescriptorResponse, Data = [0x00, (byte)(MaskVersion >> 8), (byte)MaskVersion], ShortData = true,
                    });
                else if (f.Service == Apci.PropertyValueRead && f.Data[1] == 11)
                    await FromDevice(new CemiFrame
                    {
                        Destination = tunnel, Kind = TpduKind.DataConnected, Sequence = _deviceSendSeq,
                        Service = Apci.PropertyValueResponse, Data = [0x00, 11, 0x10, 0x01, 0x00, 0x83, 0x12, 0x34, 0x56, 0x78],
                    });
                else if (f.Service == Apci.PropertyValueRead)
                    await FromDevice(new CemiFrame
                    {
                        Destination = tunnel, Kind = TpduKind.DataConnected, Sequence = _deviceSendSeq,
                        Service = Apci.PropertyValueResponse, Data = [0x00, f.Data[1], 0x00, 0x01],
                    });
                else if (f.Service == Apci.Restart)
                {
                    RestartCount++;
                    DeviceInProgrammingMode = false;
                    _deviceConnected = false;
                    return;
                }
                else return;
                _deviceSendSeq = (_deviceSendSeq + 1) & 0x0F;
                break;
        }
    }

    Task FromDevice(CemiFrame f) => Indicate(new CemiFrame
    {
        MessageCode = CemiMessageCode.LDataInd, Source = DeviceAddress, Destination = f.Destination, IsGroupDestination = f.IsGroupDestination,
        Priority = Priority.System, Kind = f.Kind, Sequence = f.Sequence, Service = f.Service, Data = f.Data, ShortData = f.ShortData,
    });

    async Task Indicate(CemiFrame f)
    {
        var seq = _sendSeq++;
        await Send(ServiceType.TunnelingRequest, [0x04, 0x07, seq, 0x00], f.ToBytes());
    }

    Task Send(ushort st, params byte[][] parts) =>
        _client == null ? Task.CompletedTask : _udp.SendAsync(KnxIpFrame.Build(st, parts), _client).AsTask();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udp.Dispose();
        try { await _loop; } catch { }
    }
}
