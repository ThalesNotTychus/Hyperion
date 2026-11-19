using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodexMultiLauncher;

/// <summary>Resolves the one current-user Store package that this launcher is allowed to control.</summary>
public static class OfficialCodexInstallation
{
    public static string GetCurrentChatGptExecutablePath()
    {
        var package = PackageFullNameSelector.SelectOrThrow(PackageDiscovery.GetCurrentUserPackageFullNames());
        var packagePath = PackageDiscovery.GetPackageInstallPath(package.FullName);
        var executable = Path.GetFullPath(Path.Combine(packagePath, "app", "ChatGPT.exe"));
        if (!File.Exists(executable))
            throw new InvalidOperationException("当前注册的官方 Codex 包没有 app\\ChatGPT.exe，不能安全发现或控制客户端。");
        return executable;
    }

    public static bool IsExactOfficialExecutable(string? candidate, string? registeredExecutable)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(registeredExecutable)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(registeredExecutable), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

public readonly record struct ProcessIdentity(uint ProcessId, DateTimeOffset StartedAtUtc)
{
    public bool Matches(uint processId, DateTimeOffset startedAtUtc) => ProcessId == processId && StartedAtUtc == startedAtUtc;
}

public static class OfficialProcessTree
{
    public static IReadOnlyDictionary<ProcessIdentity, IReadOnlyList<ProcessIdentity>> GroupRoots(
        IEnumerable<(ProcessIdentity Identity, uint ParentProcessId, bool IsOfficialExecutable)> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var all = processes.ToArray();
        var byPid = all.GroupBy(process => process.Identity.ProcessId).ToDictionary(group => group.Key, group => group.Last());
        var children = all.GroupBy(process => process.ParentProcessId).ToDictionary(group => group.Key, group => group.ToArray());
        var roots = all.Where(process => process.IsOfficialExecutable && (!byPid.TryGetValue(process.ParentProcessId, out var parent) || !parent.IsOfficialExecutable || parent.Identity.StartedAtUtc > process.Identity.StartedAtUtc));
        var result = new Dictionary<ProcessIdentity, IReadOnlyList<ProcessIdentity>>();
        foreach (var root in roots)
        {
            var members = new List<ProcessIdentity>();
            var pending = new Queue<ProcessIdentity>();
            var seen = new HashSet<uint>();
            pending.Enqueue(root.Identity);
            while (pending.Count > 0)
            {
                var process = pending.Dequeue();
                if (!seen.Add(process.ProcessId)) continue;
                members.Add(process);
                // The official client lifecycle ends at the first non-official child.
                // Never cross that boundary and later absorb an unrelated official process.
                if (children.TryGetValue(process.ProcessId, out var childProcesses))
                    foreach (var child in childProcesses.Where(child => child.IsOfficialExecutable && child.Identity.StartedAtUtc >= process.StartedAtUtc)) pending.Enqueue(child.Identity);
            }
            result[root.Identity] = members;
        }
        return result;
    }
}
