using System.Runtime.InteropServices;

namespace CodexMultiLauncher;

internal static class NativeWindowFocus
{
    private const int ShowRestore = 9;
    private const int ShowMinimized = 6;
    private const uint WmClose = 0x0010;

    public static bool TryFocus(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return false;
        if (IsIconic(windowHandle)) ShowWindowAsync(windowHandle, ShowRestore);
        return SetForegroundWindow(windowHandle);
    }

    public static bool TryMinimize(IntPtr windowHandle) => windowHandle != IntPtr.Zero && ShowWindowAsync(windowHandle, ShowMinimized);
    public static bool TryClose(IntPtr windowHandle) => windowHandle != IntPtr.Zero && PostMessage(windowHandle, WmClose, IntPtr.Zero, IntPtr.Zero);

    public static bool TryTile(IEnumerable<IntPtr> windows)
    {
        var handles = windows.Where(handle => handle != IntPtr.Zero).Distinct().ToArray();
        if (handles.Length == 0) return false;
        var monitor = MonitorFromWindow(handles[0], 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
        var area = info.Work;
        var columns = (int)Math.Ceiling(Math.Sqrt(handles.Length));
        var rows = (int)Math.Ceiling(handles.Length / (double)columns);
        var width = Math.Max(1, (area.Right - area.Left) / columns);
        var height = Math.Max(1, (area.Bottom - area.Top) / rows);
        var positioned = true;
        foreach (var (handle, index) in handles.Select((handle, index) => (handle, index)))
        {
            ShowWindowAsync(handle, ShowRestore);
            positioned &= SetWindowPos(handle, IntPtr.Zero, (int)area.Left + index % columns * width, (int)area.Top + index / columns * height, width, height, 0x0054);
        }
        return positioned;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
