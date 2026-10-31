using KnxAlternative.Core.KnxIp;

namespace KnxAlternative.Core.Management;

/// <summary>Group communication: write values and read them back.</summary>
public sealed class GroupClient
{
    readonly IKnxConnection _bus;
    public GroupClient(IKnxConnection bus) => _bus = bus;

    public Task<bool> WriteAsync(GroupAddress ga, GroupValue value, CancellationToken ct = default) =>
        _bus.SendAsync(CemiFrame.GroupWrite(ga, value), ct);

    public Task<bool> WriteAsync(GroupAddress ga, DptId dpt, string text, CancellationToken ct = default) =>
        WriteAsync(ga, Dpt.Encode(dpt, text), ct);

    /// <summary>Sends GroupValueRead and returns the first GroupValueResponse, or null on timeout.</summary>
    public async Task<GroupValue?> ReadAsync(GroupAddress ga, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<GroupValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, CemiFrame f)
        {
            if (f.IsGroupDestination && f.Destination == ga.Raw && f.Service == Apci.GroupValueResponse
                && f.MessageCode != CemiMessageCode.LDataCon)
                tcs.TrySetResult(new GroupValue(f.Data, f.ShortData));
        }
        _bus.FrameReceived += Handler;
        try
        {
            await _bus.SendAsync(CemiFrame.GroupRead(ga), ct);
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(3), ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _bus.FrameReceived -= Handler;
        }
    }
}
