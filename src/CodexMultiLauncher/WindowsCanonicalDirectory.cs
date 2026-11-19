using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexMultiLauncher;

/// <summary>Match Rust's Windows canonical path, even for a future directory.</summary>
internal static class WindowsCanonicalDirectory
{
    internal static string Resolve(string directory)
    {
        var current = Path.GetFullPath(directory);
        ProfilePathSafety.ThrowIfReparsePath(current);
        var missing = new Stack<string>();
        while (!Directory.Exists(current))
        {
            if (File.Exists(current)) throw new InvalidOperationException("运行时插件市场路径包含文件，无法准备目录。");
            var parent = Path.GetDirectoryName(current)
                ?? throw new DirectoryNotFoundException("无法验证运行时目录的现有祖先。");
            missing.Push(Path.GetFileName(current));
            current = parent;
        }
        // Metadata-only access. A directory handle gives its actual filesystem
        // casing and expands 8.3 aliases, unlike Path.GetFullPath().
        using var handle = CreateFile(current, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法取得运行时目录的规范路径。");
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取运行时目录的规范路径。");
        var canonical = buffer.ToString();
        while (missing.TryPop(out var segment)) canonical = Path.Combine(canonical, segment);
        ProfilePathSafety.ThrowIfReparsePath(canonical);
        return canonical;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
