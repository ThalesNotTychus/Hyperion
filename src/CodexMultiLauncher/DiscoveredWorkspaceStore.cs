using System.Text.Json;

namespace CodexMultiLauncher;

/// <summary>Persists only the documented default path pair, never credentials or client files.</summary>
public sealed class DiscoveredWorkspaceStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DiscoveredWorkspaceStore(string projectRoot) => _path = Path.Combine(Path.GetFullPath(projectRoot), "data", "discovered-workspaces.json");
    public string? LastWarning { get; private set; }

    public bool HasOriginalWorkspace()
    {
        try
        {
            if (!File.Exists(_path)) return false;
            var item = JsonSerializer.Deserialize<OriginalWorkspaceRecord>(File.ReadAllText(_path), JsonOptions);
            return item is not null && OriginalWorkspacePaths.IsExactDefaultPair(item.ElectronUserData, item.CodexHome);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            LastWarning = $"原始工作区记录不可用：{exception.Message}";
            return false;
        }
    }

    public void SaveOriginalWorkspace()
    {
        var paths = OriginalWorkspacePaths.GetCurrentUserDefaultPair();
        var codexHome = paths.RequireCodexHome();
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new OriginalWorkspaceRecord(paths.ElectronUserData, codexHome), JsonOptions));
            File.Move(temporary, _path, overwrite: true);
            LastWarning = null;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record OriginalWorkspaceRecord(string ElectronUserData, string CodexHome);
}

public static class OriginalWorkspacePaths
{
    public static WorkspaceDataPaths GetCurrentUserDefaultPair()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(appData) || string.IsNullOrWhiteSpace(userProfile))
            throw new InvalidOperationException("无法确定当前 Windows 用户的默认 Codex 数据目录。");
        return new WorkspaceDataPaths(
            Canonicalize(Path.Combine(appData, "Codex", "web", "Codex")),
            Canonicalize(Path.Combine(userProfile, ".codex")));
    }

    public static bool IsExactDefaultPair(string? electronUserData, string? codexHome)
    {
        if (!PathIdentity.TryCanonicalize(electronUserData, out var electron) || !PathIdentity.TryCanonicalize(codexHome, out var home)) return false;
        var expected = GetCurrentUserDefaultPair();
        return PathIdentity.Equals(electron, expected.ElectronUserData) && PathIdentity.Equals(home, expected.RequireCodexHome());
    }

    private static string Canonicalize(string value)
    {
        if (!PathIdentity.TryCanonicalize(value, out var result)) throw new InvalidOperationException("默认 Codex 数据目录无效。");
        return result;
    }
}

public static class PathIdentity
{
    public static bool TryCanonicalize(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var trimmed = value.Trim();
            if (!Path.IsPathFullyQualified(trimmed)) return false;
            var full = Path.GetFullPath(trimmed);
            path = Path.TrimEndingDirectorySeparator(full);
            return path.Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static bool Equals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
