namespace CodexMultiLauncher;

public sealed class ProfileService
{
    private const string ConfigFileName = "config.toml";
    private const string ConfigContents = WorkspaceSandboxService.NewProfileConfig;
    private readonly ProfileStore _store;

    public ProfileService(string projectRoot)
    {
        _store = new ProfileStore(projectRoot);
    }

    public IReadOnlyList<Profile> LoadOrCreateDefaults()
    {
        var profiles = _store.Load().ToList();
        if (profiles.Count == 0 && !File.Exists(_store.ProfilesFile))
        {
            profiles.Add(new Profile(ProfilePaths.CreateProfileId(), "账号 1"));
            profiles.Add(new Profile(ProfilePaths.CreateProfileId(), "账号 2"));
            foreach (var profile in profiles) EnsureProfileStorage(profile);
            _store.Save(profiles);
        }
        else
        {
            foreach (var profile in profiles) EnsureProfileStorage(profile);
        }

        return profiles;
    }

    public Profile CreateProfile(string displayName, ClientKind client = ClientKind.Codex)
    {
        if (!Enum.IsDefined(client)) throw new InvalidOperationException("客户端类型无效。");
        var profile = new Profile(ProfilePaths.CreateProfileId(), ProfileStore.ValidateDisplayName(displayName), client);
        var profiles = _store.Load().ToList();
        EnsureProfileStorage(profile);
        profiles.Add(profile);
        _store.Save(profiles);
        return profile;
    }

    public Profile RenameProfile(string profileId, string displayName)
    {
        ProfilePaths.ValidateProfileId(profileId);
        var normalized = ProfileStore.ValidateDisplayName(displayName);
        var profiles = _store.Load().ToList();
        var index = profiles.FindIndex(item => string.Equals(item.Id, profileId, StringComparison.Ordinal));
        if (index < 0) throw new InvalidOperationException("找不到要重命名的账号配置。");
        var updated = profiles[index] with { DisplayName = normalized };
        profiles[index] = updated;
        _store.Save(profiles);
        return updated;
    }

    public void DeleteProfile(string profileId, bool deleteStorageDirectory = false)
    {
        ProfilePaths.ValidateProfileId(profileId);
        var profiles = _store.Load().ToList();
        var target = profiles.FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("找不到要删除的账号配置。");
        profiles.Remove(target);
        _store.Save(profiles);
        if (!deleteStorageDirectory) return;
        var paths = GetPaths(target);
        ProfilePathSafety.ThrowIfReparsePath(paths.ProfileDirectory);
        if (Directory.Exists(paths.ProfileDirectory))
            Directory.Delete(paths.ProfileDirectory, recursive: true);
    }

    public ProfilePaths GetPaths(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ProfilePaths(_store.ProjectRoot, profile.Id);
    }

    public void EnsureProfileStorage(Profile profile)
    {
        var paths = GetPaths(profile);
        if (profile.Client == ClientKind.Antigravity)
        {
            ProfilePathSafety.ThrowIfReparsePath(paths.ElectronUserData);
            ProfilePathSafety.ThrowIfReparsePath(paths.AntigravityHome);
            ProfilePathSafety.ThrowIfReparsePath(Path.Combine(paths.AntigravityHome, ".gemini"));
            Directory.CreateDirectory(paths.ElectronUserData);
            Directory.CreateDirectory(Path.Combine(paths.AntigravityHome, ".gemini"));
            return;
        }
        if (profile.Client == ClientKind.Claude)
        {
            ProfilePathSafety.ThrowIfReparsePath(paths.ElectronUserData);
            ProfilePathSafety.ThrowIfReparsePath(paths.ClaudeHome);
            Directory.CreateDirectory(paths.ElectronUserData);
            Directory.CreateDirectory(paths.ClaudeHome);
            return;
        }
        ProfilePathSafety.ThrowIfReparsePath(paths.CodexHome);
        ProfilePathSafety.ThrowIfReparsePath(paths.ElectronUserData);
        ProfilePathSafety.ThrowIfReparsePath(ProfilePathSafety.GetContainedFullPath(paths.PrimaryRuntimeInstallRoot, "codex-primary-runtime"));
        Directory.CreateDirectory(paths.CodexHome);
        Directory.CreateDirectory(paths.ElectronUserData);
        Directory.CreateDirectory(paths.PrimaryRuntimeInstallRoot);

        var configFile = ProfilePathSafety.GetContainedFullPath(paths.ProfilesRoot, paths.ProfileId, "codex-home", ConfigFileName);
        ProfilePathSafety.ThrowIfReparsePath(configFile);
        if (!File.Exists(configFile))
        {
            using var stream = new FileStream(configFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(ConfigContents);
        }
    }
}
