using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodexMultiLauncher;

/// <summary>Bounded native process query; intentionally does not shell out to WMI.</summary>
internal static class ProcessCommandLineReader
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int MaxCommandLineBytes = 64 * 1024;

    public static bool TryRead(uint processId, out string? commandLine)
    {
        commandLine = null;
        if (processId is 0 or > int.MaxValue) return false;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return false;
        try
        {
            var size = 1024;
            while (size <= MaxCommandLineBytes)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    var status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, size, out var required);
                    if (status == 0)
                    {
                        var unicode = Marshal.PtrToStructure<UnicodeString>(buffer);
                        if (!IsValidReturnedString(buffer, size, unicode)) return false;
                        if (unicode.Length == 0) { commandLine = string.Empty; return true; }
                        commandLine = Marshal.PtrToStringUni(unicode.Buffer, unicode.Length / sizeof(char));
                        return commandLine is not null;
                    }
                    if (status != StatusInfoLengthMismatch || required <= size || required > MaxCommandLineBytes) return false;
                    size = checked((int)required);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return false;
        }
        finally { CloseHandle(process); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    private static bool IsValidReturnedString(IntPtr allocation, int allocationLength, UnicodeString value)
    {
        if (allocation == IntPtr.Zero || allocationLength <= 0 || value.Buffer == IntPtr.Zero || value.Length % sizeof(char) != 0 || value.Length > value.MaximumLength) return false;
        try
        {
            var allocationStart = unchecked((nuint)allocation.ToInt64());
            var allocationEnd = checked(allocationStart + checked((nuint)allocationLength));
            var stringStart = unchecked((nuint)value.Buffer.ToInt64());
            var stringEnd = checked(stringStart + (nuint)value.Length);
            return stringStart >= allocationStart && stringEnd <= allocationEnd;
        }
        catch (OverflowException) { return false; }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, IntPtr processInformation, int processInformationLength, out uint returnLength);
}
