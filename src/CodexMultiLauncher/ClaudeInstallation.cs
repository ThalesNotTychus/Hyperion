namespace CodexMultiLauncher;

/// <summary>Claude MSIX identity comes from the current user's registered publisher family.</summary>
public static class ClaudeInstallation
{
    public const string PackageFamilyName = "Claude_pzs8sxrjxfjjc";
    public const string AppUserModelId = PackageFamilyName + "!Claude";

    public static CodexPackage SelectPackage(IEnumerable<string> fullNames) => fullNames
        .Select(TryParsePackage).OfType<CodexPackage>()
        .OrderByDescending(package => package.Version).ThenBy(package => package.FullName, StringComparer.Ordinal)
        .FirstOrDefault() ?? throw new InvalidOperationException("未找到官方 Claude x64 桌面版，请从 claude.com/download 安装或更新。");

    public static CodexPackage? TryParsePackage(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return null;
        var parts = fullName.Split('_');
        if (parts.Length != 5 || parts[0] != "Claude" || !parts[2].Equals("x64", StringComparison.OrdinalIgnoreCase)
            || parts[3].Length != 0 || parts[4] != "pzs8sxrjxfjjc"
            || parts[1].Split('.').Length != 4 || parts[1].Split('.').Any(part => !ushort.TryParse(part, out _))
            || !Version.TryParse(parts[1], out var version)) return null;
        return new(fullName, version);
    }

    public static CodexPackage GetCurrentPackage() => SelectPackage(PackageDiscovery.GetCurrentUserPackageFullNames(PackageFamilyName));

    public static string GetExecutablePath()
    {
        var package = GetCurrentPackage();
        var executable = Path.GetFullPath(Path.Combine(PackageDiscovery.GetPackageInstallPath(package.FullName), "app", "Claude.exe"));
        if (!File.Exists(executable)) throw new InvalidOperationException("官方 Claude 包中没有 app\\Claude.exe，请修复安装。");
        return executable;
    }

    public static void ValidateManagedPaths(WorkspaceDataPaths paths)
    {
        if (paths.Client != ClientKind.Claude || paths.ClaudeHome is null || paths.CodexHome is not null || paths.RuntimeUserHome is not null || paths.AntigravityHome is not null)
            throw new InvalidOperationException("Claude 隔离目录配置无效。");
        if (!PathIdentity.TryCanonicalize(paths.ElectronUserData, out var electron)
            || !PathIdentity.TryCanonicalize(paths.ClaudeHome, out var home))
            throw new InvalidOperationException("Claude 数据目录必须是绝对路径。");
        var root = Path.GetDirectoryName(electron)!;
        ProfilePaths.ValidateProfileId(Path.GetFileName(root));
        if (!PathIdentity.Equals(electron, Path.Combine(root, "electron-user-data"))
            || !PathIdentity.Equals(home, Path.Combine(root, "claude-home")))
            throw new InvalidOperationException("Claude 数据目录必须属于同一个受管账号。");
        ProfilePathSafety.ThrowIfReparsePath(electron);
        ProfilePathSafety.ThrowIfReparsePath(home);
    }

    public static IReadOnlyList<string> GetOriginalDataDirectories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new[]
        {
            Path.Combine(local, "Claude-Data", PackageFamilyName.ToLowerInvariant()),
            Path.Combine(local, "Packages", PackageFamilyName, "LocalCache", "Roaming", "Claude"),
            Path.Combine(roaming, "Claude"),
        };
    }
}
