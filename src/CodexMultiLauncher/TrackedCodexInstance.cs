using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CodexMultiLauncher;

public enum CodexInstanceState { Running, Unavailable, Stopped }
public enum CodexInstanceSource { Managed, Original, External }

public sealed class TrackedCodexInstance : INotifyPropertyChanged, IDisposable
{
    private readonly ProcessIdentity _identity;
    private IReadOnlyList<ProcessIdentity> _members = [];
    private CodexWorkspace? _workspace;
    private CodexInstanceSource _source;
    private CodexInstanceState _state = CodexInstanceState.Running;
    private TimeSpan _uptime, _lastProcessorTime;
    private long? _lastSampleTimestamp;
    private double _cpuPercent;
    private long _workingSetBytes;
    private bool _isCpuAvailable, _isWorkingSetAvailable, _hasMainWindow, _hasProcessorSample, _disposed;
    private string? _officialExecutable;

    internal TrackedCodexInstance(ProcessIdentity identity, CodexWorkspace? workspace, CodexInstanceSource source) { _identity = identity; _workspace = workspace; _source = source; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string? ProfileId => _workspace?.Id;
    public CodexWorkspace? Workspace => _workspace;
    public string ProfileName => _workspace?.DisplayName ?? "外部客户端";
    public uint ProcessId => _identity.ProcessId;
    public DateTimeOffset StartedAtUtc => _identity.StartedAtUtc;
    public CodexInstanceSource Source => _source;
    public string SourceText => Source switch { CodexInstanceSource.Managed => "受管账号", CodexInstanceSource.Original => "原始客户端", _ => "外部客户端" };
    public CodexInstanceState State => _state;
    public string StateText => State switch { CodexInstanceState.Running => "运行中", CodexInstanceState.Unavailable => "暂不可用", _ => "已退出" };
    public int ProcessCount => _members.Count;
    public IReadOnlyList<uint> MemberProcessIds => _members.Select(m => m.ProcessId).ToArray();
    public TimeSpan Uptime => _uptime;
    public string UptimeText => RuntimeFormatting.FormatDuration(Uptime);
    public double CpuPercent => _cpuPercent;
    public bool IsCpuAvailable => _isCpuAvailable;
    public string CpuText => IsCpuAvailable ? $"{CpuPercent:0.0}%" : RuntimeFormatting.UnavailableText;
    public long WorkingSetBytes => _workingSetBytes;
    public bool IsWorkingSetAvailable => _isWorkingSetAvailable;
    public string WorkingSetText => IsWorkingSetAvailable ? RuntimeFormatting.FormatBytes(WorkingSetBytes) : RuntimeFormatting.UnavailableText;
    public bool HasMainWindow => _hasMainWindow;
    public string WindowText => HasMainWindow ? "可用" : "等待窗口";
    public bool IsStopped => State == CodexInstanceState.Stopped;

    internal void SetWorkspace(CodexWorkspace? workspace, CodexInstanceSource source)
    {
        if (!Equals(_workspace, workspace)) { _workspace = workspace; Changed(nameof(ProfileId)); Changed(nameof(Workspace)); Changed(nameof(ProfileName)); }
        if (_source != source) { _source = source; Changed(nameof(Source)); Changed(nameof(SourceText)); }
    }
    internal void ApplyMeasurement(IReadOnlyList<ProcessIdentity> members, string executable, InstanceMeasurement measurement)
    {
        if (_disposed) return;
        var changed = !_members.SequenceEqual(members);
        _members = members; _officialExecutable = executable;
        if (changed) ResetCpuSample();
        Changed(nameof(ProcessCount));
        SetUptime(measurement.MeasuredAtUtc);
        SetWorkingSet(measurement.WorkingSetBytes, measurement.MetricsAvailable);
        SetWindow(measurement.HasWindow);
        UpdateCpu(measurement.ProcessorTime, measurement.Timestamp, measurement.MetricsAvailable);
        SetState(CodexInstanceState.Running);
    }
    internal static InstanceMeasurement Measure(IReadOnlyList<ProcessIdentity> members)
    {
        var processor = TimeSpan.Zero; var memory = 0L; var available = members.Count > 0; var hasWindow = false;
        foreach (var member in members)
        {
            try
            {
                if (!TryVerifyMember(member, out var process) || process is null) { available = false; continue; }
                using (process)
                {
                    processor += process.TotalProcessorTime; memory += process.WorkingSet64;
                    if (member == members[0]) hasWindow = process.MainWindowHandle != IntPtr.Zero;
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            { available = false; }
        }
        // Partial trees cannot be compared with complete trees or presented as complete totals.
        return new(processor, memory, available, hasWindow, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
    }
    internal void MarkUnavailable()
    {
        if (IsStopped) return;
        ResetCpuSample(); SetCpu(0, false); SetWorkingSet(0, false); SetWindow(false);
        SetState(CodexInstanceState.Unavailable);
    }
    internal void MarkMissing() => MarkStopped();
    private void ResetCpuSample() { _hasProcessorSample = false; _lastSampleTimestamp = null; }
    internal bool TryFocus() => TryGetVerifiedRootWindow(out var handle) && NativeWindowFocus.TryFocus(handle);
    internal bool TryMinimize() => TryGetVerifiedRootWindow(out var handle) && NativeWindowFocus.TryMinimize(handle);
    internal bool TryClose() => TryGetVerifiedRootWindow(out var handle) && NativeWindowFocus.TryClose(handle);
    internal IEnumerable<IntPtr> GetVerifiedTopLevelWindows()
    {
        if (TryGetVerifiedRootWindow(out var handle)) yield return handle;
    }
    internal bool TryKill()
    {
        if (!TryVerifyRoot(out var root) || root is null) { MarkStopped(); return false; }
        try { root.Kill(entireProcessTree: true); MarkStopped(); return true; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { MarkUnavailable(); return false; }
        finally { root?.Dispose(); }
    }
    private bool TryVerifyRoot(out Process? process)
    {
        process = null; if (_disposed || string.IsNullOrWhiteSpace(_officialExecutable)) return false;
        try
        {
            process = Process.GetProcessById(checked((int)ProcessId));
            if (process.HasExited || new DateTimeOffset(process.StartTime.ToUniversalTime()) != StartedAtUtc || !OfficialCodexInstallation.IsExactOfficialExecutable(process.MainModule?.FileName, _officialExecutable)) { process.Dispose(); process = null; return false; }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { process?.Dispose(); process = null; return false; }
    }
    private static bool TryVerifyMember(ProcessIdentity identity, out Process? process)
    {
        process = null;
        if (identity.ProcessId is 0 or > int.MaxValue) return false;
        try
        {
            process = Process.GetProcessById(checked((int)identity.ProcessId));
            if (process.HasExited || new DateTimeOffset(process.StartTime.ToUniversalTime()) != identity.StartedAtUtc) { process.Dispose(); process = null; return false; }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { process?.Dispose(); process = null; return false; }
    }
    private bool TryGetVerifiedRootWindow(out IntPtr handle)
    {
        handle = IntPtr.Zero;
        if (!TryVerifyRoot(out var root) || root is null) return false;
        try { handle = root.MainWindowHandle; return handle != IntPtr.Zero; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { MarkUnavailable(); return false; }
        finally { root?.Dispose(); }
    }
    private void MarkStopped() { ResetCpuSample(); SetUptime(DateTimeOffset.UtcNow); SetCpu(0, false); SetWorkingSet(0, false); SetWindow(false); SetState(CodexInstanceState.Stopped); }
    private void UpdateCpu(TimeSpan processor, long timestamp, bool available)
    {
        if (!available || !_hasProcessorSample) { _hasProcessorSample = available; _lastProcessorTime = processor; _lastSampleTimestamp = available ? timestamp : null; SetCpu(0, false); return; }
        var elapsed = timestamp - _lastSampleTimestamp!.Value; var delta = processor - _lastProcessorTime; _lastProcessorTime = processor; _lastSampleTimestamp = timestamp;
        var value = elapsed > 0 && delta >= TimeSpan.Zero ? delta.TotalSeconds / (elapsed / (double)Stopwatch.Frequency) / Environment.ProcessorCount * 100d : double.NaN;
        SetCpu(double.IsFinite(value) ? Math.Clamp(value, 0d, 100d) : 0, double.IsFinite(value));
    }
    private void SetUptime(DateTimeOffset now) { var value = now - StartedAtUtc; if (value < TimeSpan.Zero) value = TimeSpan.Zero; if (_uptime != value) { _uptime = value; Changed(nameof(Uptime)); Changed(nameof(UptimeText)); } }
    private void SetCpu(double value, bool available) { if (_cpuPercent != value) { _cpuPercent = value; Changed(nameof(CpuPercent)); Changed(nameof(CpuText)); } if (_isCpuAvailable != available) { _isCpuAvailable = available; Changed(nameof(IsCpuAvailable)); Changed(nameof(CpuText)); } }
    private void SetWorkingSet(long value, bool available) { if (_workingSetBytes != value) { _workingSetBytes = value; Changed(nameof(WorkingSetBytes)); Changed(nameof(WorkingSetText)); } if (_isWorkingSetAvailable != available) { _isWorkingSetAvailable = available; Changed(nameof(IsWorkingSetAvailable)); Changed(nameof(WorkingSetText)); } }
    private void SetWindow(bool value) { if (_hasMainWindow != value) { _hasMainWindow = value; Changed(nameof(HasMainWindow)); Changed(nameof(WindowText)); } }
    private void SetState(CodexInstanceState value) { if (_state != value) { _state = value; Changed(nameof(State)); Changed(nameof(StateText)); Changed(nameof(IsStopped)); } }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void Dispose() => _disposed = true;
}

internal sealed record InstanceMeasurement(TimeSpan ProcessorTime, long WorkingSetBytes, bool MetricsAvailable, bool HasWindow, long Timestamp, DateTimeOffset MeasuredAtUtc);
