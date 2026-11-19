namespace CodexMultiLauncher;

/// <summary>
/// Windows archive runtimes use Node's os.homedir(), independently of CODEX_HOME.
/// Override it only in the managed desktop child's package environment. CLI queries
/// and the launcher's own environment continue to use the actual Windows profile.
/// </summary>
public static class RuntimeHomeIsolation
{
    public static string? Validate(WorkspaceDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        // Original workspaces deliberately keep the official default home.
        if (paths.RuntimeUserHome is null) return null;
        if (!PathIdentity.TryCanonicalize(paths.RequireCodexHome(), out var codexHome)
            || !PathIdentity.TryCanonicalize(paths.ElectronUserData, out var electronUserData)
            || !PathIdentity.TryCanonicalize(paths.RuntimeUserHome, out var runtimeUserHome)
            || !string.Equals(Path.GetFileName(codexHome), "codex-home", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("运行时隔离只接受受管账号的绝对数据路径。");

        var profileDirectory = Path.GetDirectoryName(codexHome)!;
        var expectedElectron = ProfilePathSafety.GetContainedFullPath(profileDirectory, "electron-user-data");
        var expectedHome = ProfilePathSafety.GetContainedFullPath(profileDirectory, "runtime-user-home");
        if (!PathIdentity.Equals(electronUserData, expectedElectron) || !PathIdentity.Equals(runtimeUserHome, expectedHome))
            throw new InvalidOperationException("运行时隔离目录必须属于同一受管账号，不能共享或指向外部目录。");

        ProfilePathSafety.ThrowIfReparsePath(codexHome);
        ProfilePathSafety.ThrowIfReparsePath(electronUserData);
        ProfilePathSafety.ThrowIfReparsePath(ProfilePathSafety.GetContainedFullPath(runtimeUserHome,
            ".cache", "codex-runtimes", "codex-primary-runtime"));
        return runtimeUserHome;
    }
}
