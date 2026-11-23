using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace CodexMultiLauncher;

/// <summary>Finds the installed Antigravity desktop app without executing shell aliases or importing accounts.</summary>
public static class AntigravityInstallation
{
    public static string GetOriginalDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Antigravity");

    public static string GetExecutablePath()
    {
        foreach (var candidate in GetInstalledCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(candidate) || !Path.GetFileName(candidate).Equals("Antigravity.exe", StringComparison.OrdinalIgnoreCase)) continue;
                ProfilePathSafety.ThrowIfReparsePath(candidate);
                var info = FileVersionInfo.GetVersionInfo(candidate);
                if (!IsDesktopProduct(info.ProductName, info.CompanyName)) continue;
                var directory = Path.GetDirectoryName(candidate)!;
                if (!File.Exists(Path.Combine(directory, "resources", "app.asar"))
                    || !File.Exists(Path.Combine(directory, "resources", "bin", "language_server.exe"))) continue;
                return Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        }
        throw new InvalidOperationException("未找到 Antigravity Windows 桌面版，请从 antigravity.google/download 安装。当前不支持单独的 CLI 或 IDE 安装版。");
    }

    public static bool IsDesktopProduct(string? product, string? company) =>
        string.Equals(product, "Antigravity", StringComparison.Ordinal)
        && company is "Google" or "Google LLC" or "Google Inc.";

    private static IEnumerable<string> GetInstalledCandidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "antigravity", "Antigravity.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Antigravity", "Antigravity.exe");
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var path in ReadRegisteredCandidates(hive, view)) yield return path;
        }
    }

    private static IReadOnlyList<string> ReadRegisteredCandidates(RegistryHive hive, RegistryView view)
    {
        var result = new List<string>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) return result;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                if (entry?.GetValue("DisplayName") is not string display || !display.StartsWith("Antigravity ", StringComparison.Ordinal)
                    || entry.GetValue("Publisher") is not string publisher || publisher is not ("Google" or "Google LLC" or "Google Inc.")) continue;
                if (entry.GetValue("InstallLocation") is string location && Path.IsPathFullyQualified(location))
                    result.Add(Path.Combine(location, "Antigravity.exe"));
                if (entry.GetValue("DisplayIcon") is string icon && TryParseDisplayIcon(icon, out var executable)) result.Add(executable);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return result;
    }

    public static bool TryParseDisplayIcon(string value, out string executable)
    {
        executable = string.Empty;
        var text = value.Trim();
        var comma = text.LastIndexOf(',');
        if (comma >= 0 && int.TryParse(text[(comma + 1)..], out _)) text = text[..comma].Trim();
        text = text.Trim('"');
        if (!PathIdentity.TryCanonicalize(text, out var path)
            || !Path.GetFileName(path).Equals("Antigravity.exe", StringComparison.OrdinalIgnoreCase)) return false;
        executable = path; return true;
    }

    public static void ValidateManagedPaths(WorkspaceDataPaths paths)
    {
        if (paths.Client != ClientKind.Antigravity || paths.AntigravityHome is null || paths.CodexHome is not null
            || paths.ClaudeHome is not null || paths.RuntimeUserHome is not null
            || !PathIdentity.TryCanonicalize(paths.ElectronUserData, out var electron)
            || !PathIdentity.TryCanonicalize(paths.AntigravityHome, out var home))
            throw new InvalidOperationException("Antigravity 隔离目录配置无效。");
        var root = Path.GetDirectoryName(electron)!;
        ProfilePaths.ValidateProfileId(Path.GetFileName(root));
        if (!PathIdentity.Equals(electron, Path.Combine(root, "electron-user-data"))
            || !PathIdentity.Equals(home, Path.Combine(root, "antigravity-home")))
            throw new InvalidOperationException("Antigravity 数据目录必须属于同一个受管账号。");
        ProfilePathSafety.ThrowIfReparsePath(electron);
        ProfilePathSafety.ThrowIfReparsePath(home);
        ProfilePathSafety.ThrowIfReparsePath(Path.Combine(home, ".gemini"));
    }

    public static IReadOnlyDictionary<string, string> CreateEnvironment(WorkspaceDataPaths paths)
    {
        if (paths.Client != ClientKind.Antigravity) throw new InvalidOperationException("客户端类型无效。");
        if (paths.AntigravityHome is null)
        {
            if (paths.CodexHome is not null || paths.ClaudeHome is not null || paths.RuntimeUserHome is not null
                || !PathIdentity.Equals(paths.ElectronUserData, GetOriginalDataDirectory()))
                throw new InvalidOperationException("只有已验证的原始 Antigravity 目录可以不设置隔离主目录。");
            return new Dictionary<string, string>();
        }
        ValidateManagedPaths(paths);
        var home = Path.GetFullPath(paths.AntigravityHome);
        var appDataRoaming = Path.Combine(home, "AppData", "Roaming");
        var appDataLocal = Path.Combine(home, "AppData", "Local");
        var xdgConfig = Path.Combine(home, ".config");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["USERPROFILE"] = home,
            ["HOME"] = home,
            ["APPDATA"] = appDataRoaming,
            ["LOCALAPPDATA"] = appDataLocal,
            ["XDG_CONFIG_HOME"] = xdgConfig,
            ["ANTIGRAVITY_GEMINI_DIR"] = Path.Combine(home, ".gemini"),
            ["ANTIGRAVITY_APP_DATA_DIR"] = "antigravity",
            ["ANTIGRAVITY_CREDENTIAL_SCOPE"] = "codexmultilauncher-" + Path.GetFileName(Path.GetDirectoryName(home)),
            // Forces `composite_token_storage` in both `language_server.exe` and `agy.exe` to store OAuth tokens
            // in isolated per-profile files under `.gemini` instead of overwriting the global Windows `wincred` slot.
            ["WSL_INTEROP"] = "1",
        };
    }

    public static ProcessStartInfo CreateStartInfo(string executable, WorkspaceDataPaths paths)
    {
        var environment = CreateEnvironment(paths);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        start.ArgumentList.Add("--user-data-dir=" + Path.GetFullPath(paths.ElectronUserData));
        start.Environment.Remove("ELECTRON_RUN_AS_NODE");
        start.Environment.Remove("ELECTRON_OZONE_PLATFORM_HINT");
        start.Environment.Remove("WSL_INTEROP");
        foreach (var (key, value) in environment) start.Environment[key] = value;
        return start;
    }

    public static uint Launch(WorkspaceDataPaths paths)
    {
        WindowsSecurity.ThrowIfHighIntegrity();
        var executable = GetExecutablePath();
        var start = CreateStartInfo(executable, paths);
        ProfilePathSafety.ThrowIfReparsePath(paths.ElectronUserData);
        if (paths.AntigravityHome is not null)
        {
            RequireIsolationSupport(executable);
            Directory.CreateDirectory(Path.Combine(paths.AntigravityHome, ".gemini", "antigravity"));
            Directory.CreateDirectory(Path.Combine(paths.AntigravityHome, "AppData", "Roaming"));
            Directory.CreateDirectory(Path.Combine(paths.AntigravityHome, "AppData", "Local"));
            Directory.CreateDirectory(Path.Combine(paths.AntigravityHome, ".config"));
        }
        Directory.CreateDirectory(paths.ElectronUserData);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Antigravity 未能启动。");
        return checked((uint)process.Id);
    }

    public static void RequireIsolationSupport(string executable)
    {
        // These native backend switches isolate Gemini state and the OS credential target.
        // Detect capabilities in the installed binary instead of guessing a minimum version.
        var backend = Path.Combine(Path.GetDirectoryName(executable)!, "resources", "bin", "language_server.exe");
        ProfilePathSafety.ThrowIfReparsePath(backend);
        using var stream = File.OpenRead(backend);
        if (!ContainsIsolationCapabilities(stream))
            throw new InvalidOperationException("此 Antigravity 版本缺少独立账号目录或凭据作用域支持，请更新桌面版后再启动受管账号。");
    }

    public static bool ContainsIsolationCapabilities(Stream stream)
    {
        var required = new HashSet<string>(["ANTIGRAVITY_GEMINI_DIR", "ANTIGRAVITY_CREDENTIAL_SCOPE"], StringComparer.Ordinal);
        var buffer = new byte[64 * 1024];
        var tail = string.Empty;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            var text = tail + Encoding.ASCII.GetString(buffer, 0, read);
            required.RemoveWhere(key => text.Contains(key, StringComparison.Ordinal));
            if (required.Count == 0) return true;
            tail = text[^Math.Min(text.Length, 32)..];
        }
        return false;
    }
}
