using System.Net;
using System.Net.Sockets;

namespace KnxAlternative.Core.KnxIp;

/// <summary>KNXnet/IP routing (multicast 224.0.23.12). Needs a KNX IP router on the network.</summary>
public sealed class RoutingConnection : IKnxConnection
{
    readonly IPAddress? _localIp;
    readonly IPEndPoint _group;
    UdpClient? _udp;
    CancellationTokenSource? _cts;
    Task? _receiveTask;
    DateTime _lastSend = DateTime.MinValue;

    public RoutingConnection(IndividualAddress localAddress, IPAddress? localIp = null, IPAddress? multicast = null)
    {
        LocalAddress = localAddress;
        _localIp = localIp;
        _group = new IPEndPoint(multicast ?? KnxIpConstants.MulticastAddress, KnxIpConstants.Port);
    }

    public ConnectionState State { get; private set; }
    public IndividualAddress LocalAddress { get; }
    public string Description => $"Routing {_group.Address} als {LocalAddress}";
    public event EventHandler<CemiFrame>? FrameReceived;
    public event EventHandler<ConnectionState>? StateChanged;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, KnxIpConstants.Port));
        if (_localIp != null) udp.JoinMulticastGroup(_group.Address, _localIp);
        else udp.JoinMulticastGroup(_group.Address);
        udp.MulticastLoopback = false;
        _udp = udp;
        _cts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
        State = ConnectionState.Connected;
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        try { if (_receiveTask != null) await _receiveTask; } catch { }
        State = ConnectionState.Disconnected;
        StateChanged?.Invoke(this, State);
    }

    public async Task<bool> SendAsync(CemiFrame frame, CancellationToken ct = default)
    {
        if (_udp == null) throw new KnxException("Nicht verbunden");
        // Routers may drop bursts; keep at most ~50 frames/s.
        var wait = _lastSend.AddMilliseconds(20) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        var cemi = new CemiFrame
        {
            MessageCode = CemiMessageCode.LDataInd,
            Priority = frame.Priority,
            HopCount = frame.HopCount,
            Source = LocalAddress,
            Destination = frame.Destination,
            IsGroupDestination = frame.IsGroupDestination,
            Kind = frame.Kind,
            Sequence = frame.Sequence,
            Service = frame.Service,
            Data = frame.Data,
            ShortData = frame.ShortData,
        };
        await _udp.SendAsync(KnxIpFrame.Build(ServiceType.RoutingIndication, cemi.ToBytes()), _group, ct);
        _lastSend = DateTime.UtcNow;
        // No confirmation in routing: report own frame so monitors show it.
        FrameReceived?.Invoke(this, cemi);
        return true;
    }

    async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp != null)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            if (!KnxIpFrame.TryParse(r.Buffer, out var st, out var body)) continue;
            if (st != ServiceType.RoutingIndication) continue;
            try
            {
                var f = CemiFrame.Parse(body.Span);
                if (f.Source == LocalAddress) continue;
                FrameReceived?.Invoke(this, f);
            }
            catch (FormatException) { }
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
