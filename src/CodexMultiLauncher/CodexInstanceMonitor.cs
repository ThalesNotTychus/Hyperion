using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexMultiLauncher;

public sealed class CodexInstanceMonitor : IDisposable
{
    public static readonly TimeSpan PendingLaunchAssociationWindow = TimeSpan.FromSeconds(30);
    private readonly ObservableCollection<TrackedCodexInstance> _instances = [];
    private readonly Dictionary<(uint, DateTimeOffset), TrackedCodexInstance> _byIdentity = [];
    private readonly ManagedInstanceStore _store;
    private readonly Dictionary<uint, PendingLaunch> _pendingLaunches = [];
    private List<ManagedInstanceAssociation> _associations;
    private bool _disposed;
    private readonly ClientKind _client;
    private string GetExecutablePath() => _client switch
    {
        ClientKind.Codex => OfficialCodexInstallation.GetCurrentChatGptExecutablePath(),
        ClientKind.Claude => ClaudeInstallation.GetExecutablePath(),
        ClientKind.Antigravity => AntigravityInstallation.GetExecutablePath(),
        _ => throw new InvalidOperationException("客户端类型无效。"),
    };

    public CodexInstanceMonitor(string projectRoot, ClientKind client = ClientKind.Codex)
    {
        _client = client;
        _store = new ManagedInstanceStore(projectRoot, client);
        _associations = _store.Load().ToList(); LastWarning = _store.LastWarning;
    }

    public ObservableCollection<TrackedCodexInstance> Instances => _instances;
    public string? LastWarning { get; private set; }
    public bool DiscoveryAvailable { get; private set; }

    public bool RegisterLaunched(CodexWorkspace workspace, uint processId, DateTimeOffset requestedAtUtc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(workspace);
        if (!workspace.IsLaunchable || workspace.Client != _client) return false;
        if (requestedAtUtc.Offset != TimeSpan.Zero) requestedAtUtc = requestedAtUtc.ToUniversalTime();
        _pendingLaunches[processId] = new PendingLaunch(workspace.Id, processId, requestedAtUtc);
        if (!TryReadOfficialIdentity(processId, GetExecutablePath(), out var identity)) return false;
        if (!IsEligibleLaunchRoot(identity, requestedAtUtc, DateTimeOffset.UtcNow)) { _pendingLaunches.Remove(processId); return false; }
        _pendingLaunches.Remove(processId);
        if (workspace.Kind == CodexWorkspaceKind.Managed) AddAssociation(workspace.Id, identity);
        return true;
    }

    public async Task RefreshAllAsync(WorkspaceCatalog catalog)
    {
        if (_disposed) return;
        try
        {
            var captured = await Task.Run(() =>
            {
                var executable = GetExecutablePath();
                var snapshots = ProcessSnapshot.Read(executable, out var unreadable);
                var trees = OfficialProcessTree.GroupRoots(snapshots.Select(snapshot => (snapshot.Identity, snapshot.ParentProcessId, snapshot.IsOfficialExecutable)));
                var observations = trees.Select(tree => new CapturedInstance(tree.Key, tree.Value,
                    ObserveElectronDirectory(tree.Value), TrackedCodexInstance.Measure(tree.Value))).ToArray();
                return (Executable: executable, Instances: observations, Unreadable: unreadable);
            });
            if (_disposed) return;
            var executable = captured.Executable;
            DiscoveryAvailable = true;
            LastWarning = _store.LastWarning;
            var known = new HashSet<(uint, DateTimeOffset)>(); var now = DateTimeOffset.UtcNow;
            foreach (var stale in _pendingLaunches.Where(pair => now - pair.Value.RequestedAtUtc > PendingLaunchAssociationWindow).Select(pair => pair.Key).ToArray()) _pendingLaunches.Remove(stale);
            var validAssociations = new List<ManagedInstanceAssociation>(); var associationsChanged = false;
            foreach (var capture in captured.Instances)
            {
                var identity = capture.Identity; var members = capture.Members;
                known.Add((identity.ProcessId, identity.StartedAtUtc));
                var observed = capture.Observation;
                var association = _associations.FirstOrDefault(item => item.RootProcessId == identity.ProcessId && item.RootStartedAtUtc == identity.StartedAtUtc);
                CodexWorkspace? workspace = null;
                if (observed.Kind == ElectronDirectoryObservationKind.Single)
                {
                    workspace = catalog.ResolveObservedElectronDirectory(observed.Directory!, _client);
                    _pendingLaunches.Remove(identity.ProcessId);
                    if (ResolveObservedAssociation(workspace, association) == ManagedAssociationResolution.Remove)
                    {
                        _associations.Remove(association!); association = null; associationsChanged = true;
                    }
                }
                else if (observed.Kind == ElectronDirectoryObservationKind.Unavailable)
                {
                    if (association is null && _pendingLaunches.TryGetValue(identity.ProcessId, out var pending) && IsEligibleLaunchRoot(identity, pending.RequestedAtUtc, now))
                    {
                        _pendingLaunches.Remove(identity.ProcessId); AddAssociation(pending.WorkspaceId, identity); association = _associations.LastOrDefault(); associationsChanged = true;
                    }
                    else if (_pendingLaunches.ContainsKey(identity.ProcessId)) _pendingLaunches.Remove(identity.ProcessId);
                    workspace = association is not null ? catalog.FindById(association.ProfileId) : null;
                    if (workspace?.Client != _client) workspace = null;
                }
                else
                {
                    if (association is not null) { _associations.Remove(association); association = null; associationsChanged = true; }
                    _pendingLaunches.Remove(identity.ProcessId);
                }
                if (association is not null && workspace?.Kind == CodexWorkspaceKind.Managed) validAssociations.Add(association);
                var source = workspace?.Kind switch { CodexWorkspaceKind.Managed => CodexInstanceSource.Managed, CodexWorkspaceKind.Original => CodexInstanceSource.Original, _ => CodexInstanceSource.External };
                if (!_byIdentity.TryGetValue((identity.ProcessId, identity.StartedAtUtc), out var instance))
                {
                    instance = new TrackedCodexInstance(identity, workspace, source); _byIdentity.Add((identity.ProcessId, identity.StartedAtUtc), instance); _instances.Add(instance);
                }
                else instance.SetWorkspace(workspace, source);
                instance.ApplyMeasurement(members, executable, capture.Measurement);
            }
            foreach (var instance in _instances.Where(instance => !known.Contains((instance.ProcessId, instance.StartedAtUtc))).ToArray())
            {
                if (captured.Unreadable.Contains(instance.ProcessId))
                {
                    instance.MarkUnavailable();
                    validAssociations.AddRange(_associations.Where(a => a.RootProcessId == instance.ProcessId && a.RootStartedAtUtc == instance.StartedAtUtc));
                }
                else instance.MarkMissing();
            }
            if (associationsChanged || !_associations.SequenceEqual(validAssociations)) { _associations = validAssociations; SaveAssociationsSafely(); }
        }
        catch (Exception exception)
        {
            if (_disposed) return;
            DiscoveryAvailable = false;
            LastWarning = $"{_client} 客户端发现失败：{exception.Message}";
            foreach (var instance in _instances.Where(instance => !instance.IsStopped)) instance.MarkUnavailable();
        }
    }

    public bool Focus(TrackedCodexInstance instance) => !_disposed && Owns(instance) && instance.TryFocus();
    public bool MinimizeAll(IEnumerable<TrackedCodexInstance>? instances = null) => !_disposed && (instances ?? _instances).Where(instance => !instance.IsStopped).Aggregate(false, (result, instance) => instance.TryMinimize() || result);
    public bool TileAll(IEnumerable<TrackedCodexInstance>? instances = null) => !_disposed && NativeWindowFocus.TryTile((instances ?? _instances).Where(instance => !instance.IsStopped).SelectMany(instance => instance.GetVerifiedTopLevelWindows()));
    public bool Close(TrackedCodexInstance instance) => !_disposed && Owns(instance) && instance.TryClose();
    public bool Kill(TrackedCodexInstance instance) => !_disposed && Owns(instance) && instance.TryKill();

    private void AddAssociation(string workspaceId, ProcessIdentity identity)
    {
        _associations.RemoveAll(item => item.RootProcessId == identity.ProcessId);
        _associations.Add(new ManagedInstanceAssociation(workspaceId, identity.ProcessId, identity.StartedAtUtc)); SaveAssociationsSafely();
    }
    private bool Owns(TrackedCodexInstance? instance) => instance is not null && _instances.Contains(instance);
    public static bool IsEligibleLaunchRoot(ProcessIdentity identity, DateTimeOffset requestedAtUtc, DateTimeOffset observedAtUtc) => identity.StartedAtUtc >= requestedAtUtc && observedAtUtc >= requestedAtUtc && observedAtUtc - requestedAtUtc <= PendingLaunchAssociationWindow;
    /// <summary>Observed canonical path is authoritative; PID metadata survives only for the same managed workspace.</summary>
    public static ManagedAssociationResolution ResolveObservedAssociation(CodexWorkspace observedWorkspace, ManagedInstanceAssociation? association) =>
        association is null ? ManagedAssociationResolution.None : observedWorkspace.Kind == CodexWorkspaceKind.Managed && string.Equals(observedWorkspace.Id, association.ProfileId, StringComparison.Ordinal) ? ManagedAssociationResolution.Keep : ManagedAssociationResolution.Remove;
    public static ElectronDirectoryObservation ObserveElectronDirectory(IEnumerable<ProcessIdentity> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return ObserveElectronCommandLines(members.Select(member => ProcessCommandLineReader.TryRead(member.ProcessId, out var commandLine) ? commandLine : null));
    }
    public static ElectronDirectoryObservation ObserveElectronCommandLines(IEnumerable<string?> commandLines)
    {
        ArgumentNullException.ThrowIfNull(commandLines);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var malformed = false;
        foreach (var commandLine in commandLines)
            foreach (var path in WindowsCommandLine.GetElectronUserDataDirectories(commandLine))
                if (path is null) malformed = true; else directories.Add(path);
        return malformed || directories.Count > 1 ? ElectronDirectoryObservation.Conflict : directories.Count == 1 ? ElectronDirectoryObservation.Single(directories.Single()) : ElectronDirectoryObservation.Unavailable;
    }
    private static bool TryReadOfficialIdentity(uint processId, string executable, out ProcessIdentity identity)
    {
        identity = default; if (processId is 0 or > int.MaxValue) return false;
        try { using var process = Process.GetProcessById(checked((int)processId)); if (process.HasExited || !OfficialCodexInstallation.IsExactOfficialExecutable(process.MainModule?.FileName, executable)) return false; identity = new(processId, new DateTimeOffset(process.StartTime.ToUniversalTime())); return true; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException) { return false; }
    }
    private void SaveAssociationsSafely()
    {
        try { _store.Save(_associations); LastWarning = null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) { LastWarning = $"实例归属未持久化：{exception.Message}"; }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; foreach (var instance in _instances) instance.Dispose(); }
    private sealed record PendingLaunch(string WorkspaceId, uint ProcessId, DateTimeOffset RequestedAtUtc);

    private sealed record CapturedInstance(ProcessIdentity Identity, IReadOnlyList<ProcessIdentity> Members, ElectronDirectoryObservation Observation, InstanceMeasurement Measurement);

    private sealed record ProcessSnapshot(ProcessIdentity Identity, uint ParentProcessId, bool IsOfficialExecutable)
    {
        public static IReadOnlyList<ProcessSnapshot> Read(string executable, out HashSet<uint> unreadable)
        {
            var results = new List<ProcessSnapshot>();
            unreadable = [];
            foreach (var entry in ToolhelpSnapshot.Read())
            {
                // A filename prefilter is only an optimization; exact package path remains authoritative.
                if (entry.ProcessId is 0 or > int.MaxValue || !string.Equals(entry.ExecutableName, Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase)) continue;
                try { using var process = Process.GetProcessById(checked((int)entry.ProcessId)); if (!process.HasExited) results.Add(new(new(entry.ProcessId, new DateTimeOffset(process.StartTime.ToUniversalTime())), entry.ParentProcessId, OfficialCodexInstallation.IsExactOfficialExecutable(process.MainModule?.FileName, executable))); }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException) { unreadable.Add(entry.ProcessId); }
            }
            return results;
        }
    }
    private static class ToolhelpSnapshot
    {
        private const uint ProcessSnapshotFlag = 0x2;
        public readonly record struct Entry(uint ProcessId, uint ParentProcessId, string ExecutableName);
        public static IReadOnlyList<Entry> Read()
        {
            var handle = CreateToolhelp32Snapshot(ProcessSnapshotFlag, 0); if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法获取进程快照。");
            try { var entries = new List<Entry>(); var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() }; if (!Process32First(handle, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取进程快照。"); do { entries.Add(new(entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile)); entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>(); } while (Process32Next(handle, ref entry)); var error = Marshal.GetLastWin32Error(); if (error != 18) throw new Win32Exception(error, "无法完整读取进程快照。"); return entries; }
            finally { CloseHandle(handle); }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct PROCESSENTRY32 { public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    }
}

public enum ElectronDirectoryObservationKind { Unavailable, Single, Conflict }
public enum ManagedAssociationResolution { None, Keep, Remove }
public sealed record ElectronDirectoryObservation(ElectronDirectoryObservationKind Kind, string? Directory)
{
    public static ElectronDirectoryObservation Unavailable { get; } = new(ElectronDirectoryObservationKind.Unavailable, null);
    public static ElectronDirectoryObservation Conflict { get; } = new(ElectronDirectoryObservationKind.Conflict, null);
    public static ElectronDirectoryObservation Single(string directory) => new(ElectronDirectoryObservationKind.Single, directory);
}
