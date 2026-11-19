using System.Text.Json;

namespace CodexMultiLauncher;

public sealed class ProfileStore
{
    private const int DisplayNameMaximumLength = 80;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _profilesFile;

    public ProfileStore(string projectRoot)
    {
        ProjectRoot = Path.GetFullPath(projectRoot ?? throw new ArgumentNullException(nameof(projectRoot)));
        DataDirectory = ProfilePathSafety.GetContainedFullPath(ProjectRoot, "data");
        _profilesFile = ProfilePathSafety.GetContainedFullPath(DataDirectory, "profiles.json");
    }

    public string ProjectRoot { get; }
    public string DataDirectory { get; }
    public string ProfilesFile => _profilesFile;

    public IReadOnlyList<Profile> Load()
    {
        if (!File.Exists(_profilesFile)) return Array.Empty<Profile>();

        try
        {
            var profiles = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(_profilesFile), SerializerOptions)
                ?? throw new InvalidOperationException("profiles.json 为空或格式无效。");
            ValidateProfiles(profiles);
            return profiles;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("profiles.json 不是有效的账号配置 JSON。", exception);
        }
    }

    public void Save(IReadOnlyList<Profile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ValidateProfiles(profiles);
        Directory.CreateDirectory(DataDirectory);

        var temporary = Path.Combine(DataDirectory, $".profiles.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profiles, SerializerOptions));
            File.Move(temporary, _profilesFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string ValidateDisplayName(string? displayName)
    {
        var normalized = displayName?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("账号配置名称不能为空。");
        }
        if (normalized.Length > DisplayNameMaximumLength)
        {
            throw new InvalidOperationException($"账号配置名称不能超过 {DisplayNameMaximumLength} 个字符。");
        }
        return normalized;
    }

    private static void ValidateProfiles(IReadOnlyList<Profile> profiles)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (profile is null) throw new InvalidOperationException("账号配置中存在空条目。");
            ProfilePaths.ValidateProfileId(profile.Id);
            if (!Enum.IsDefined(profile.Client)) throw new InvalidOperationException("账号配置中的客户端类型无效。");
            ValidateDisplayName(profile.DisplayName);
            if (!ids.Add(profile.Id)) throw new InvalidOperationException("账号配置中存在重复的账号 ID。");
        }
    }
}
