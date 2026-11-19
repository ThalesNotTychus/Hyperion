using System.Text.Json;

namespace CodexMultiLauncher;

public sealed record ManagedInstanceAssociation(string ProfileId, uint RootProcessId, DateTimeOffset RootStartedAtUtc);

/// <summary>Small, fail-closed persistence boundary for launcher-owned client identities.</summary>
public sealed class ManagedInstanceStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ManagedInstanceStore(string projectRoot, ClientKind client = ClientKind.Codex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var file = client switch
        {
            ClientKind.Codex => "managed-instances.json",
            ClientKind.Claude => "managed-claude-instances.json",
            ClientKind.Antigravity => "managed-antigravity-instances.json",
            _ => throw new InvalidOperationException("客户端类型无效。"),
        };
        _path = Path.Combine(Path.GetFullPath(projectRoot), "data", file);
    }

    public string? LastWarning { get; private set; }

    public IReadOnlyList<ManagedInstanceAssociation> Load()
    {
        try
        {
            if (!File.Exists(_path)) { LastWarning = null; return []; }
            var items = JsonSerializer.Deserialize<List<ManagedInstanceAssociation>>(File.ReadAllText(_path), JsonOptions);
            LastWarning = null;
            return items?
                .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.ProfileId) && item.RootProcessId > 0 && item.RootStartedAtUtc != default)
                .DistinctBy(item => (item.RootProcessId, item.RootStartedAtUtc))
                .ToArray() ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            LastWarning = $"实例归属文件不可用，已按未恢复归属继续：{exception.Message}";
            return [];
        }
    }

    public void Save(IEnumerable<ManagedInstanceAssociation> associations)
    {
        ArgumentNullException.ThrowIfNull(associations);
        var items = associations
            .Where(item => !string.IsNullOrWhiteSpace(item.ProfileId) && item.RootProcessId > 0 && item.RootStartedAtUtc != default)
            .DistinctBy(item => (item.RootProcessId, item.RootStartedAtUtc))
            .ToArray();
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(items, JsonOptions));
            if (File.Exists(_path)) File.Replace(temporary, _path, destinationBackupFileName: null);
            else File.Move(temporary, _path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
