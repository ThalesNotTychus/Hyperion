namespace CodexMultiLauncher;

/// <summary>UI-thread scheduler: one current request; same-workspace ticks never cancel it.</summary>
public sealed class WorkspaceRefreshGate : IDisposable
{
    private readonly TimeSpan _freshness;
    public WorkspaceRefreshGate(TimeSpan freshness)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(freshness, TimeSpan.Zero);
        _freshness = freshness;
    }
    private readonly Dictionary<string, DateTimeOffset> _completed = new(StringComparer.Ordinal);
    private RefreshLease? _current;
    private bool _disposed;
    public bool IsRunning => _current is not null;
    public RefreshLease? TryBegin(string workspaceId, DateTimeOffset now, bool force = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current?.WorkspaceId == workspaceId) return null;
        Cancel();
        if (!force && _completed.TryGetValue(workspaceId, out var completed) && now >= completed && now - completed < _freshness) return null;
        return _current = new RefreshLease(workspaceId);
    }
    public bool IsCurrent(RefreshLease lease) => !_disposed && ReferenceEquals(_current, lease) && !lease.Token.IsCancellationRequested;
    public void Complete(RefreshLease lease, DateTimeOffset now)
    {
        if (IsCurrent(lease)) { _completed[lease.WorkspaceId] = now; _current = null; }
        lease.Dispose();
    }
    public void Cancel() { _current?.Cancel(); _current = null; }
    public void Dispose() { if (_disposed) return; Cancel(); _disposed = true; }
}

public sealed class RefreshLease : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    public string WorkspaceId { get; }
    public CancellationToken Token { get; }
    internal RefreshLease(string workspaceId) { WorkspaceId = workspaceId; Token = _cancellation.Token; }
    internal void Cancel() => _cancellation.Cancel();
    public void Dispose() => _cancellation.Dispose();
}
