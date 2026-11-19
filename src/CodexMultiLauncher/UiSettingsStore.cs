using System.Text.Json;

namespace CodexMultiLauncher;

public sealed record UiSettings(
    string Theme = "obsidian",
    bool AutoFailoverEnabled = true,
    int FailoverThresholdPercent = 92,
    string DefaultSandboxMode = "workspace-write",
    string LastRepositoryPath = "",
    string LastPreviewUrl = "http://localhost:3000",
    bool StealthBackgroundMode = false,
    IReadOnlyList<string>? HiddenWorkspaceIds = null,
    string Language = "zh",
    IReadOnlyList<string>? RecentProjects = null)
{
    public static readonly IReadOnlySet<string> SupportedThemes = new HashSet<string>(StringComparer.Ordinal)
    {
        "obsidian",
        "matrix",
        "daylight",
        "aurora",
    };

    public UiSettings Normalize()
    {
        var theme = SupportedThemes.Contains(Theme ?? string.Empty) ? Theme! : "obsidian";
        var threshold = Math.Clamp(FailoverThresholdPercent, 50, 100);
        var mode = string.Equals(DefaultSandboxMode, "read-only", StringComparison.Ordinal) ? "read-only" : "workspace-write";
        var lang = string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        var hidden = (HiddenWorkspaceIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var projects = (RecentProjects ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p.Trim()))
            .Select(p => Path.GetFullPath(p.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new UiSettings(
            theme,
            AutoFailoverEnabled,
            threshold,
            mode,
            LastRepositoryPath?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(LastPreviewUrl) ? "http://localhost:3000" : LastPreviewUrl.Trim(),
            StealthBackgroundMode,
            hidden,
            lang,
            projects);
    }
}

public sealed class UiSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly object _gate = new();

    public UiSettingsStore(string projectRoot)
    {
        _file = ProfilePathSafety.GetContainedFullPath(projectRoot, "data", "ui-settings.json");
    }

    public UiSettings Load()
    {
        lock (_gate)
        {
            try
            {
                ProfilePathSafety.ThrowIfReparsePath(_file);
                if (!File.Exists(_file)) return new UiSettings().Normalize();
                var parsed = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(_file), Json);
                return (parsed ?? new UiSettings()).Normalize();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return new UiSettings().Normalize();
            }
        }
    }

    public UiSettings Save(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        lock (_gate)
        {
            ProfilePathSafety.ThrowIfReparsePath(_file);
            var directory = Path.GetDirectoryName(_file)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".ui-settings.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, Json));
                File.Move(temporary, _file, overwrite: true);
                return normalized;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
