using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexMultiLauncher;

public sealed class PackagedCodexLauncher
{
    public const string AppUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";
    public const string DebugMutexName = "Local\\CodexMultiLauncher.PackageDebugSettings.v1";
    public static readonly TimeSpan DebugMutexTimeout = TimeSpan.FromSeconds(15);

    public uint Launch(WorkspaceDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Client == ClientKind.Antigravity)
            return AntigravityInstallation.Launch(paths);
        if (paths.Client == ClientKind.Claude)
            return LaunchClaude(paths);
        ProfilePathSafety.ThrowIfReparsePath(paths.RequireCodexHome());
        ProfilePathSafety.ThrowIfReparsePath(paths.ElectronUserData);
        var runtimeUserHome = RuntimeHomeIsolation.Validate(paths);
        WindowsSecurity.ThrowIfHighIntegrity();
        if (runtimeUserHome is not null)
            Directory.CreateDirectory(ProfilePathSafety.GetContainedFullPath(runtimeUserHome, ".cache", "codex-runtimes"));

        var package = PackageFullNameSelector.SelectOrThrow(PackageDiscovery.GetCurrentUserPackageFullNames());
        using var mutex = new Mutex(false, DebugMutexName);
        var mutexAcquired = false;
        try
        {
            try
            {
                mutexAcquired = mutex.WaitOne(DebugMutexTimeout);
            }
            catch (AbandonedMutexException)
            {
                mutexAcquired = true;
            }

            if (!mutexAcquired)
            {
                throw new InvalidOperationException("另一个 Codex 启动操作仍在配置官方客户端。请稍后重试。");
            }

            return ActivateWithTemporaryDebugEnvironment(package.FullName, AppUserModelId, paths);
        }
        finally
        {
            if (mutexAcquired) mutex.ReleaseMutex();
        }
    }

    private static uint LaunchClaude(WorkspaceDataPaths paths)
    {
        WindowsSecurity.ThrowIfHighIntegrity();
        ProfilePathSafety.ThrowIfReparsePath(paths.ElectronUserData);
        if (paths.ClaudeHome is not null) ClaudeInstallation.ValidateManagedPaths(paths);
        Directory.CreateDirectory(paths.ElectronUserData);
        if (paths.ClaudeHome is not null)
        {
            Directory.CreateDirectory(paths.ClaudeHome);
        }
        var package = ClaudeInstallation.GetCurrentPackage();
        using var mutex = new Mutex(false, "Local\\CodexMultiLauncher.ClaudePackageDebugSettings.v1");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(DebugMutexTimeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("另一个 Claude 启动操作仍在进行，请稍后重试。");
            return ActivateWithTemporaryDebugEnvironment(package.FullName, ClaudeInstallation.AppUserModelId, paths);
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private static uint ActivateWithTemporaryDebugEnvironment(string packageFullName, string appUserModelId, WorkspaceDataPaths paths)
    {
        IPackageDebugSettings? debugSettings = null;
        object? debugSettingsObject = null;
        Exception? operationFailure = null;
        Exception? cleanupFailure = null;
        var debuggingEnabled = false;
        uint processId = 0;

        try
        {
            debugSettingsObject = new PackageDebugSettings();
            debugSettings = (IPackageDebugSettings)debugSettingsObject;
            using var environment = PackageEnvironmentBlock.Create(paths);
            ThrowForHResult(
                debugSettings.EnableDebugging(packageFullName, WindowsCommandLine.CreateDebuggerCommandLine(CurrentExecutablePath()), environment.Pointer),
                "无法为官方客户端 配置隔离启动环境");
            debuggingEnabled = true;

            object? activationManagerObject = null;
            try
            {
                activationManagerObject = new ApplicationActivationManager();
                var activationManager = (IApplicationActivationManager)activationManagerObject;
                ThrowForHResult(
                    activationManager.ActivateApplication(appUserModelId, WindowsCommandLine.CreateElectronArguments(paths.ElectronUserData), ActivateOptions.None, out processId),
                    "无法激活官方客户端 Windows 客户端");
            }
            finally
            {
                if (activationManagerObject is not null) Marshal.FinalReleaseComObject(activationManagerObject);
            }
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        finally
        {
            if (debuggingEnabled && debugSettings is not null)
            {
                try
                {
                    ThrowForHResult(debugSettings.DisableDebugging(packageFullName), "无法清除官方客户端 的临时启动环境");
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }

            if (debugSettingsObject is not null)
            {
                try
                {
                    Marshal.FinalReleaseComObject(debugSettingsObject);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }
        }

        if (operationFailure is not null && cleanupFailure is not null)
        {
            throw new InvalidOperationException($"{operationFailure.Message}；此外，清理失败：{cleanupFailure.Message}", operationFailure);
        }
        if (operationFailure is not null) throw operationFailure;
        if (cleanupFailure is not null)
        {
            throw new InvalidOperationException($"官方客户端 已处理启动请求，但临时启动环境清理失败：{cleanupFailure.Message}", cleanupFailure);
        }
        if (processId == 0) throw new InvalidOperationException("官方客户端 激活没有返回有效的进程 ID。");
        return processId;
    }

    private static string CurrentExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("无法确定启动器自身的可执行文件路径，不能安全启动官方客户端。");
        }
        return path;
    }

    private static void ThrowForHResult(int hresult, string context)
    {
        if (hresult < 0) throw new COMException($"{context}（HRESULT 0x{hresult:X8}）。", hresult);
    }
}

public sealed class PackageEnvironmentBlock : IDisposable
{
    private PackageEnvironmentBlock(IntPtr pointer) => Pointer = pointer;

    public IntPtr Pointer { get; private set; }

    public static PackageEnvironmentBlock Create(WorkspaceDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var text = BuildText(CreateValues(paths));
        var pointer = Marshal.AllocHGlobal(checked(text.Length * sizeof(char)));
        Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
        return new PackageEnvironmentBlock(pointer);
    }

    public static IReadOnlyDictionary<string, string> CreateValues(WorkspaceDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Client == ClientKind.Antigravity) return AntigravityInstallation.CreateEnvironment(paths);
        if (paths.Client == ClientKind.Claude)
        {
            var claudeValues = new Dictionary<string, string>(StringComparer.Ordinal);
            if (paths.ClaudeHome is not null)
            {
                ClaudeInstallation.ValidateManagedPaths(paths);
                claudeValues["CLAUDE_CONFIG_DIR"] = paths.ClaudeHome;
            }
            return claudeValues;
        }
        var codexHome = paths.RequireCodexHome();
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CODEX_ELECTRON_USER_DATA_PATH"] = paths.ElectronUserData,
            ["CODEX_HOME"] = codexHome,
            ["CODEX_SQLITE_HOME"] = codexHome,
        };
        var runtimeUserHome = RuntimeHomeIsolation.Validate(paths);
        if (runtimeUserHome is not null) values["USERPROFILE"] = runtimeUserHome;
        return values;
    }

    public static string BuildText(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var entries = environment
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Key.Contains('\0') || pair.Key.Contains('='))
                    throw new InvalidOperationException("环境变量名称无效。");
                if (pair.Value is null || pair.Value.Contains('\0')) throw new InvalidOperationException("环境变量值无效。");
                return $"{pair.Key}={pair.Value}";
            });
        return string.Join('\0', entries) + "\0\0";
    }

    public void Dispose()
    {
        if (Pointer == IntPtr.Zero) return;
        Marshal.FreeHGlobal(Pointer);
        Pointer = IntPtr.Zero;
    }
}

internal static class PackageDiscovery
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    public static IReadOnlyList<string> GetCurrentUserPackageFullNames(string packageFamilyName = PackageFullNameSelector.PackageFamilyName)
    {
        uint count = 0;
        uint characters = 0;
        var result = GetPackagesByPackageFamily(packageFamilyName, ref count, null, ref characters, IntPtr.Zero);
        if (result != ErrorSuccess && result != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(result, $"无法枚举当前用户安装的官方客户端 包（Windows 错误 {result}）。");
        }
        if (count == 0 || characters == 0)
        {
            throw new InvalidOperationException($"当前 Windows 用户没有安装客户端包 {packageFamilyName}，请先安装官方桌面版。");
        }

        var names = new IntPtr[count];
        var buffer = Marshal.AllocHGlobal(checked((int)characters * sizeof(char)));
        try
        {
            result = GetPackagesByPackageFamily(packageFamilyName, ref count, names, ref characters, buffer);
            if (result != ErrorSuccess)
            {
                throw new Win32Exception(result, $"无法读取官方客户端 包列表（Windows 错误 {result}）。");
            }

            var fullNames = new List<string>(checked((int)count));
            for (var index = 0; index < count; index++)
            {
                var fullName = Marshal.PtrToStringUni(names[index]);
                if (!string.IsNullOrWhiteSpace(fullName)) fullNames.Add(fullName);
            }
            return fullNames;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string GetPackageInstallPath(string packageFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFullName);
        uint length = 0;
        var result = GetPackagePathByFullName(packageFullName, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
            throw new Win32Exception(result, $"无法读取官方客户端 包的安装路径（Windows 错误 {result}）。");
        var buffer = new char[length];
        result = GetPackagePathByFullName(packageFullName, ref length, buffer);
        if (result != ErrorSuccess)
            throw new Win32Exception(result, $"无法读取官方客户端 包的安装路径（Windows 错误 {result}）。");
        return new string(buffer, 0, checked((int)length - 1));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetPackagesByPackageFamily(
        string packageFamilyName,
        ref uint count,
        [Out] IntPtr[]? packageFullNames,
        ref uint bufferLength,
        IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, [Out] char[]? path);
}

internal static class WindowsSecurity
{
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int ErrorInsufficientBuffer = 122;
    private const int SecurityMandatoryHighRid = 0x3000;

    public static void ThrowIfHighIntegrity()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法检查当前进程的完整性级别。");

        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
            var error = Marshal.GetLastWin32Error();
            if (size == 0 || error != ErrorInsufficientBuffer)
                throw new Win32Exception(error, "无法读取当前进程的完整性级别。");

            var buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前进程的完整性级别。");

                var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
                var count = GetSidSubAuthorityCount(label.Sid);
                if (count == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法解析当前进程的完整性级别。");
                var authorityCount = Marshal.ReadByte(count);
                if (authorityCount == 0) throw new InvalidOperationException("无法解析当前进程的完整性级别。");
                var authority = GetSidSubAuthority(label.Sid, authorityCount - 1);
                if (authority == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法解析当前进程的完整性级别。");
                if (Marshal.ReadInt32(authority) >= SecurityMandatoryHighRid)
                    throw new InvalidOperationException("此启动器不能以管理员或高完整性权限运行。请关闭管理员模式后，用普通 Windows 用户权限重新启动。");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, int subAuthority);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

[ComImport]
[Guid("F27C3930-8029-4AD1-94E3-3DBA417810C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPackageDebugSettings
{
    [PreserveSig]
    int EnableDebugging([MarshalAs(UnmanagedType.LPWStr)] string packageFullName, [MarshalAs(UnmanagedType.LPWStr)] string debuggerCommandLine, IntPtr environment);
    [PreserveSig]
    int DisableDebugging([MarshalAs(UnmanagedType.LPWStr)] string packageFullName);
}

[ComImport]
[Guid("B1AEC16F-2383-4852-B0E9-8F0B1DC66B4D")]
internal class PackageDebugSettings { }

[ComImport]
[Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, ActivateOptions options, out uint processId);
}

[ComImport]
[Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
internal class ApplicationActivationManager { }

internal enum ActivateOptions
{
    None = 0,
}
