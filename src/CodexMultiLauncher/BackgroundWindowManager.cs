using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CodexMultiLauncher;

public sealed record StealthWindowState(
    int VisibleWindowCount,
    int HiddenWindowCount,
    bool IsStealthActive);

/// <summary>
/// Hides official Codex, Claude, and Antigravity Electron windows into silent background daemons
/// (removing them from both desktop and taskbar via SW_HIDE) while keeping their backend processes alive,
/// and supports temporary window reveal when interactive first-time login is needed.
/// </summary>
public sealed class BackgroundWindowManager
{
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;

    private readonly ConcurrentDictionary<IntPtr, (uint RootPid, string? WorkspaceId)> _hiddenWindows = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _loginRevealAllowlist = new(StringComparer.Ordinal);
    private readonly uint _launcherPid = unchecked((uint)Environment.ProcessId);

    public int TotalHiddenCount
    {
        get
        {
            PruneDeadHandles();
            return _hiddenWindows.Count;
        }
    }

    public void AllowTemporaryLoginWindow(string workspaceId, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return;
        _loginRevealAllowlist[workspaceId] = DateTimeOffset.UtcNow + (duration ?? TimeSpan.FromMinutes(8));
    }

    public void RevokeTemporaryLoginWindow(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return;
        _loginRevealAllowlist.TryRemove(workspaceId, out _);
    }

    public bool IsTemporarilyRevealedForLogin(string? workspaceId) =>
        !string.IsNullOrWhiteSpace(workspaceId)
        && _loginRevealAllowlist.TryGetValue(workspaceId, out var until)
        && until > DateTimeOffset.UtcNow;

    public StealthWindowState GetWorkspaceWindowState(string? workspaceId, IEnumerable<uint> memberPids)
    {
        PruneDeadHandles();
        var pidSet = memberPids.Where(p => p != 0 && p != _launcherPid).ToHashSet();
        var visible = pidSet.Count == 0 ? 0 : EnumerateTopLevelWindows(pidSet, requireVisible: true).Count;
        var hidden = _hiddenWindows.Count(pair =>
            (!string.IsNullOrWhiteSpace(workspaceId) && string.Equals(pair.Value.WorkspaceId, workspaceId, StringComparison.Ordinal))
            || pidSet.Contains(pair.Value.RootPid));
        return new StealthWindowState(visible, hidden, hidden > 0 && visible == 0);
    }

    public int HideInstanceWindows(uint rootPid, string? workspaceId, IEnumerable<uint> memberPids)
    {
        var pidSet = memberPids.Append(rootPid).Where(p => p != 0 && p != _launcherPid).ToHashSet();
        if (pidSet.Count == 0) return 0;
        var handles = EnumerateTopLevelWindows(pidSet, requireVisible: true);
        var count = 0;
        foreach (var hwnd in handles)
        {
            if (ShowWindow(hwnd, SwHide))
            {
                _hiddenWindows[hwnd] = (rootPid, workspaceId);
                count++;
            }
        }
        return count;
    }

    public int RevealInstanceWindows(uint rootPid, string? workspaceId, IEnumerable<uint> memberPids)
    {
        PruneDeadHandles();
        var pidSet = memberPids.Append(rootPid).Where(p => p != 0 && p != _launcherPid).ToHashSet();
        var matchingHidden = _hiddenWindows
            .Where(pair => pair.Value.RootPid == rootPid
                || (!string.IsNullOrWhiteSpace(workspaceId) && string.Equals(pair.Value.WorkspaceId, workspaceId, StringComparison.Ordinal))
                || pidSet.Contains(pair.Value.RootPid))
            .Select(pair => pair.Key)
            .ToArray();

        var revealed = 0;
        foreach (var hwnd in matchingHidden)
        {
            _hiddenWindows.TryRemove(hwnd, out _);
            if (IsWindow(hwnd))
            {
                ShowWindow(hwnd, SwShow);
                ShowWindow(hwnd, SwRestore);
                SetForegroundWindow(hwnd);
                revealed++;
            }
        }

        // Also bring any already-visible window of that instance to foreground
        foreach (var hwnd in EnumerateTopLevelWindows(pidSet, requireVisible: true))
        {
            ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
            revealed++;
        }

        return revealed;
    }

    public int EnforceStealthOnInstances(IEnumerable<TrackedCodexInstance> instances, string? excludeWorkspaceId = null)
    {
        var hiddenNow = 0;
        foreach (var instance in instances)
        {
            if (instance.IsStopped) continue;
            var wsId = instance.ProfileId;
            if (!string.IsNullOrWhiteSpace(excludeWorkspaceId) && string.Equals(wsId, excludeWorkspaceId, StringComparison.Ordinal))
                continue;
            if (IsTemporarilyRevealedForLogin(wsId))
                continue;
            hiddenNow += HideInstanceWindows(instance.ProcessId, wsId, instance.MemberProcessIds);
        }
        return hiddenNow;
    }

    public int RevealAllHiddenWindows()
    {
        PruneDeadHandles();
        var count = 0;
        foreach (var pair in _hiddenWindows.ToArray())
        {
            if (_hiddenWindows.TryRemove(pair.Key, out _) && IsWindow(pair.Key))
            {
                ShowWindow(pair.Key, SwShow);
                ShowWindow(pair.Key, SwRestore);
                count++;
            }
        }
        return count;
    }

    private void PruneDeadHandles()
    {
        foreach (var hwnd in _hiddenWindows.Keys.ToArray())
        {
            if (!IsWindow(hwnd))
                _hiddenWindows.TryRemove(hwnd, out _);
        }
    }

    private static List<IntPtr> EnumerateTopLevelWindows(HashSet<uint> targetPids, bool requireVisible)
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (requireVisible && !IsWindowVisible(hwnd)) return true;
            if (GetWindowThreadProcessId(hwnd, out var pid) != 0 && targetPids.Contains(pid))
            {
                // Skip tiny 0x0 helper/IME windows when counting visible main windows
                if (!requireVisible || GetWindowTextLength(hwnd) > 0)
                    result.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
