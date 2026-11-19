using System.Runtime.InteropServices;

namespace CodexMultiLauncher;

public static class PackagedAppResume
{
    public const string ResumeArgument = "--resume-packaged-app";
    private const uint ThreadSuspendResume = 0x0002;
    private const uint ThreadQueryLimitedInformation = 0x0800;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static bool IsResumeInvocation(string[] arguments) =>
        arguments.Length > 0 && string.Equals(arguments[0], ResumeArgument, StringComparison.Ordinal);

    public static bool Resume(string[] arguments)
    {
        if (!TryParse(arguments, out var processId, out var threadId)) return false;

        var thread = OpenThread(ThreadSuspendResume | ThreadQueryLimitedInformation, false, threadId);
        if (thread == IntPtr.Zero || thread == InvalidHandleValue)
        {
            return false;
        }

        try
        {
            return GetProcessIdOfThread(thread) == processId && ResumeThread(thread) != uint.MaxValue;
        }
        finally
        {
            CloseHandle(thread);
        }
    }

    public static bool TryParse(string[] arguments, out uint processId, out uint threadId)
    {
        processId = 0;
        threadId = 0;
        return arguments.Length == 5
            && string.Equals(arguments[0], ResumeArgument, StringComparison.Ordinal)
            && string.Equals(arguments[1], "-p", StringComparison.Ordinal)
            && uint.TryParse(arguments[2], out processId) && processId > 0
            && string.Equals(arguments[3], "-tid", StringComparison.Ordinal)
            && uint.TryParse(arguments[4], out threadId) && threadId > 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessIdOfThread(IntPtr thread);
}
