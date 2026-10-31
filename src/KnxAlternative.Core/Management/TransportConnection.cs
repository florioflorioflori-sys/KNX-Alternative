using System.Threading.Channels;
using KnxAlternative.Core.KnxIp;

namespace KnxAlternative.Core.Management;

/// <summary>Connection-oriented transport layer link (T_Connect) to one device, for management services.</summary>
public sealed class TransportConnection : IAsyncDisposable
{
    readonly IKnxConnection _bus;
    readonly IndividualAddress _target;
    readonly Channel<CemiFrame> _incoming = Channel.CreateUnbounded<CemiFrame>();
    int _sendSeq;
    int _recvSeq;
    bool _open;

    public TransportConnection(IKnxConnection bus, IndividualAddress target)
    {
        _bus = bus;
        _target = target;
        _bus.FrameReceived += OnFrame;
    }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);
    public IndividualAddress Target => _target;

    void OnFrame(object? sender, CemiFrame f)
    {
        if (f.MessageCode == CemiMessageCode.LDataCon) return;
        if (f.IsGroupDestination || f.Source != _target) return;
        _incoming.Writer.TryWrite(f);
    }

    CemiFrame Make(TpduKind kind, int seq = 0, Apci? apci = null, byte[]? data = null, bool shortData = false) => new()
    {
        Destination = _target.Raw,
        IsGroupDestination = false,
        Priority = Priority.System,
        Kind = kind,
        Sequence = seq,
        Service = apci,
        Data = data ?? [],
        ShortData = shortData,
    };

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        while (_incoming.Reader.TryRead(out _)) { }
        bool ok = await _bus.SendAsync(Make(TpduKind.Connect), ct);
        if (!ok) throw new KnxException($"Gerät {_target} antwortet nicht (keine Bestätigung auf dem Bus)");
        _sendSeq = 0;
        _recvSeq = 0;
        _open = true;
    }

    /// <summary>Sends one data request and waits for the device's ACK and the matching response.</summary>
    public async Task<CemiFrame> RequestAsync(Apci service, byte[] data, Apci expectedResponse, bool shortData = false, CancellationToken ct = default)
    {
        await SendDataAsync(service, data, shortData, ct);
        return await ReceiveAsync(expectedResponse, ct);
    }

    public async Task SendDataAsync(Apci service, byte[] data, bool shortData = false, CancellationToken ct = default)
    {
        if (!_open) throw new InvalidOperationException("Transportverbindung nicht offen");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await _bus.SendAsync(Make(TpduKind.DataConnected, _sendSeq, service, data, shortData), ct);
            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                var f = await NextAsync(deadline - DateTime.UtcNow, ct);
                if (f == null) break;
                if (f.Kind == TpduKind.Ack && f.Sequence == _sendSeq)
                {
                    _sendSeq = (_sendSeq + 1) & 0x0F;
                    return;
                }
                if (f.Kind == TpduKind.Nak) break;
                if (f.Kind == TpduKind.Disconnect) throw new KnxException($"Gerät {_target} hat die Verbindung getrennt");
                if (f.Kind == TpduKind.DataConnected) Requeue(f);
            }
        }
        throw new KnxException($"Gerät {_target} bestätigt nicht");
    }

    readonly List<CemiFrame> _backlog = new();
    void Requeue(CemiFrame f) => _backlog.Add(f);

    public async Task<CemiFrame> ReceiveAsync(Apci expected, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            CemiFrame? f;
            if (_backlog.Count > 0) { f = _backlog[0]; _backlog.RemoveAt(0); }
            else f = await NextAsync(deadline - DateTime.UtcNow, ct);
            if (f == null) throw new KnxException($"Keine Antwort ({expected}) von {_target}");
            if (f.Kind == TpduKind.Disconnect) throw new KnxException($"Gerät {_target} hat die Verbindung getrennt");
            if (f.Kind != TpduKind.DataConnected) continue;
            // Acknowledge every data frame; repeated frames carry the previous sequence number.
            await _bus.SendAsync(Make(TpduKind.Ack, f.Sequence), ct);
            if (f.Sequence != _recvSeq) continue;
            _recvSeq = (_recvSeq + 1) & 0x0F;
            if (f.Service == expected) return f;
        }
    }

    async Task<CemiFrame?> NextAsync(TimeSpan wait, CancellationToken ct)
    {
        if (wait <= TimeSpan.Zero) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(wait);
        try
        {
            return await _incoming.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task DisconnectAsync()
    {
        if (!_open) return;
        _open = false;
        try { await _bus.SendAsync(Make(TpduKind.Disconnect)); } catch (KnxException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _bus.FrameReceived -= OnFrame;
    }
}
