using System.Text.RegularExpressions;

namespace CodexMultiLauncher;

public sealed class ProfilePaths
{
    private static readonly Regex ProfileIdPattern = new(
        "^profile-[0-9a-f]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public ProfilePaths(string projectRoot, string profileId)
    {
        ProjectRoot = Path.GetFullPath(projectRoot ?? throw new ArgumentNullException(nameof(projectRoot)));
        ProfileId = ValidateProfileId(profileId);
        ProfilesRoot = ProfilePathSafety.GetContainedFullPath(ProjectRoot, "data", "profiles");
        ProfileDirectory = ProfilePathSafety.GetContainedFullPath(ProfilesRoot, ProfileId);
        CodexHome = ProfilePathSafety.GetContainedFullPath(ProfilesRoot, ProfileId, "codex-home");
        ClaudeHome = ProfilePathSafety.GetContainedFullPath(ProfilesRoot, ProfileId, "claude-home");
        AntigravityHome = ProfilePathSafety.GetContainedFullPath(ProfilesRoot, ProfileId, "antigravity-home");
        ElectronUserData = ProfilePathSafety.GetContainedFullPath(ProfilesRoot, ProfileId, "electron-user-data");
        RuntimeUserHome = ProfilePathSafety.GetContainedFullPath(ProfilesRoot, ProfileId, "runtime-user-home");
        PrimaryRuntimeInstallRoot = ProfilePathSafety.GetContainedFullPath(RuntimeUserHome, ".cache", "codex-runtimes");
    }

    public string ProjectRoot { get; }
    public string ProfileId { get; }
    public string ProfilesRoot { get; }
    public string ProfileDirectory { get; }
    public string CodexHome { get; }
    public string ClaudeHome { get; }
    public string AntigravityHome { get; }
    public string ElectronUserData { get; }
    public string RuntimeUserHome { get; }
    public string PrimaryRuntimeInstallRoot { get; }

    public static string CreateProfileId() => $"profile-{Guid.NewGuid():N}";

    public static string ValidateProfileId(string? profileId)
    {
        if (profileId is null || !ProfileIdPattern.IsMatch(profileId))
        {
            throw new InvalidOperationException("配置文件中的账号 ID 无效。账号 ID 必须由本程序生成。");
        }

        return profileId;
    }
}
