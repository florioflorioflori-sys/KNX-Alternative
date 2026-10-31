using System.Net;
using System.Net.Sockets;

namespace KnxAlternative.Core.KnxIp;

/// <summary>KNXnet/IP tunneling (UDP, link layer) as used by IP interfaces and routers.</summary>
public sealed class TunnelingConnection : IKnxConnection
{
    readonly IPEndPoint _remote;
    readonly IPAddress? _localIp;
    readonly bool _natMode;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    UdpClient? _udp;
    CancellationTokenSource? _cts;
    Task? _receiveTask, _heartbeatTask;
    byte _channel;
    byte _sendSeq;
    byte _recvSeq;
    TaskCompletionSource<byte[]>? _connectTcs;
    TaskCompletionSource<byte>? _stateTcs;
    TaskCompletionSource<bool>? _ackTcs;
    TaskCompletionSource<bool>? _conTcs;
    byte _ackExpectedSeq;

    public TunnelingConnection(IPEndPoint remote, IPAddress? localIp = null, bool natMode = true)
    {
        _remote = remote;
        _localIp = localIp;
        _natMode = natMode;
    }

    public TunnelingConnection(string host, int port = KnxIpConstants.Port)
        : this(new IPEndPoint(Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork), port)) { }

    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ConfirmTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(60);

    public ConnectionState State { get; private set; }
    public IndividualAddress LocalAddress { get; private set; }
    public string Description => $"Tunneling {_remote}";

    public event EventHandler<CemiFrame>? FrameReceived;
    public event EventHandler<ConnectionState>? StateChanged;

    void SetState(ConnectionState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(this, s);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (State != ConnectionState.Disconnected) throw new InvalidOperationException("Bereits verbunden");
        SetState(ConnectionState.Connecting);
        try
        {
            _udp = new UdpClient(new IPEndPoint(_localIp ?? IPAddress.Any, 0));
            _cts = new CancellationTokenSource();
            _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));

            var hpai = _natMode ? KnxIpFrame.Hpai(null) : KnxIpFrame.Hpai(LocalEndpointFor());
            byte[] cri = [0x04, 0x04, 0x02, 0x00]; // tunnel connection, link layer
            _connectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await _udp.SendAsync(KnxIpFrame.Build(ServiceType.ConnectRequest, hpai, hpai, cri), _remote, ct);
            var resp = await _connectTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

            byte status = resp[1];
            if (status != 0) throw new KnxException($"Verbindung abgelehnt: {ConnectStatusText(status)}");
            _channel = resp[0];
            if (resp.Length >= 14) LocalAddress = new IndividualAddress((ushort)((resp[12] << 8) | resp[13]));
            _sendSeq = 0;
            _recvSeq = 0;
            SetState(ConnectionState.Connected);
            _heartbeatTask = Task.Run(() => HeartbeatLoop(_cts.Token));
        }
        catch (TimeoutException)
        {
            await CleanupAsync();
            throw new KnxException($"Keine Antwort von {_remote}. Ist die IP-Schnittstelle erreichbar?");
        }
        catch
        {
            await CleanupAsync();
            throw;
        }
    }

    IPEndPoint LocalEndpointFor()
    {
        var lep = (IPEndPoint)_udp!.Client.LocalEndPoint!;
        if (!lep.Address.Equals(IPAddress.Any)) return lep;
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect(_remote);
        return new IPEndPoint(((IPEndPoint)probe.LocalEndPoint!).Address, lep.Port);
    }

    public async Task DisconnectAsync()
    {
        if (_udp != null && State == ConnectionState.Connected)
        {
            try
            {
                var hpai = _natMode ? KnxIpFrame.Hpai(null) : KnxIpFrame.Hpai(LocalEndpointFor());
                await _udp.SendAsync(KnxIpFrame.Build(ServiceType.DisconnectRequest, [_channel, 0x00], hpai), _remote);
                await Task.Delay(100);
            }
            catch (SocketException) { }
        }
        await CleanupAsync();
    }

    async Task CleanupAsync()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        try
        {
            if (_receiveTask != null) await _receiveTask;
        }
        catch { }
        SetState(ConnectionState.Disconnected);
    }

    public async Task<bool> SendAsync(CemiFrame frame, CancellationToken ct = default)
    {
        if (State != ConnectionState.Connected || _udp == null) throw new KnxException("Nicht verbunden");
        await _sendLock.WaitAsync(ct);
        try
        {
            var cemi = new CemiFrame
            {
                MessageCode = CemiMessageCode.LDataReq,
                Priority = frame.Priority,
                HopCount = frame.HopCount,
                Source = new IndividualAddress(0),
                Destination = frame.Destination,
                IsGroupDestination = frame.IsGroupDestination,
                Kind = frame.Kind,
                Sequence = frame.Sequence,
                Service = frame.Service,
                Data = frame.Data,
                ShortData = frame.ShortData,
            }.ToBytes();

            _conTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bool acked = false;
            for (int attempt = 0; attempt < 2 && !acked; attempt++)
            {
                _ackExpectedSeq = _sendSeq;
                _ackTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var pkt = KnxIpFrame.Build(ServiceType.TunnelingRequest, [0x04, _channel, _sendSeq, 0x00], cemi);
                await _udp.SendAsync(pkt, _remote, ct);
                try
                {
                    acked = await _ackTcs.Task.WaitAsync(AckTimeout, ct);
                }
                catch (TimeoutException) { }
            }
            if (!acked)
            {
                _ = DisconnectAsync();
                throw new KnxException("Die Schnittstelle bestätigt keine Telegramme mehr, Verbindung getrennt.");
            }
            _sendSeq++;

            try
            {
                return await _conTcs.Task.WaitAsync(ConfirmTimeout, ct);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp != null)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; } // ICMP port unreachable etc.

            if (!KnxIpFrame.TryParse(r.Buffer, out var st, out var bodyMem)) continue;
            var body = bodyMem.ToArray();
            try
            {
                await HandleAsync(st, body);
            }
            catch (Exception)
            {
                // malformed frame: ignore
            }
        }
    }

    async Task HandleAsync(ushort st, byte[] body)
    {
        switch (st)
        {
            case ServiceType.ConnectResponse:
                _connectTcs?.TrySetResult(body);
                break;
            case ServiceType.ConnectionStateResponse:
                if (body.Length >= 2 && body[0] == _channel) _stateTcs?.TrySetResult(body[1]);
                break;
            case ServiceType.TunnelingAck:
                if (body.Length >= 4 && body[1] == _channel && body[2] == _ackExpectedSeq)
                    _ackTcs?.TrySetResult(body[3] == 0);
                break;
            case ServiceType.TunnelingRequest:
            {
                if (body.Length < 4 || body[1] != _channel) return;
                byte seq = body[2];
                if (seq == _recvSeq || seq == (byte)(_recvSeq - 1))
                    await _udp!.SendAsync(KnxIpFrame.Build(ServiceType.TunnelingAck, [0x04, _channel, seq, 0x00]), _remote);
                if (seq != _recvSeq) return; // duplicate or out of order
                _recvSeq++;
                var frame = CemiFrame.Parse(body.AsSpan(body[0]));
                if (frame.MessageCode == CemiMessageCode.LDataCon) _conTcs?.TrySetResult(!frame.ConfirmError);
                FrameReceived?.Invoke(this, frame);
                break;
            }
            case ServiceType.DisconnectRequest:
                if (body.Length >= 1 && body[0] == _channel)
                {
                    await _udp!.SendAsync(KnxIpFrame.Build(ServiceType.DisconnectResponse, [_channel, 0x00]), _remote);
                    _ = CleanupAsync();
                }
                break;
        }
    }

    async Task HeartbeatLoop(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(HeartbeatInterval, ct); }
            catch (OperationCanceledException) { return; }
            bool ok = false;
            for (int i = 0; i < 3 && !ok && !ct.IsCancellationRequested; i++)
            {
                try
                {
                    _stateTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    var hpai = _natMode ? KnxIpFrame.Hpai(null) : KnxIpFrame.Hpai(LocalEndpointFor());
                    await _udp!.SendAsync(KnxIpFrame.Build(ServiceType.ConnectionStateRequest, [_channel, 0x00], hpai), _remote, ct);
                    ok = await _stateTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct) == 0;
                }
                catch (TimeoutException) { }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
            }
            failures = ok ? 0 : failures + 1;
            if (!ok)
            {
                await CleanupAsync();
                return;
            }
        }
    }

    static string ConnectStatusText(byte status) => status switch
    {
        0x22 => "Verbindungstyp nicht unterstützt",
        0x23 => "Verbindungsoption nicht unterstützt",
        0x24 => "Keine freien Tunnel-Verbindungen (alle belegt)",
        0x29 => "Tunneling-Schicht nicht unterstützt",
        _ => $"Status 0x{status:X2}",
    };

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}

public sealed class KnxException(string message) : Exception(message);
