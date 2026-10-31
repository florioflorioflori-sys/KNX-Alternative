namespace KnxAlternative.Core.KnxIp;

public enum ConnectionState { Disconnected, Connecting, Connected }

/// <summary>A link to the KNX bus that can send and receive cEMI frames.</summary>
public interface IKnxConnection : IAsyncDisposable
{
    ConnectionState State { get; }
    /// <summary>Own individual address on the bus (assigned by the tunnel server).</summary>
    IndividualAddress LocalAddress { get; }
    string Description { get; }

    /// <summary>Every frame seen on the bus (indications) and confirmations of own frames.</summary>
    event EventHandler<CemiFrame>? FrameReceived;
    event EventHandler<ConnectionState>? StateChanged;

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();

    /// <summary>Sends a frame. Returns true when the bus confirmed it (L_Data.con without error).</summary>
    Task<bool> SendAsync(CemiFrame frame, CancellationToken ct = default);
}
