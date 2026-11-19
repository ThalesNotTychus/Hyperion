using System.Collections.ObjectModel;
using System.Text.Json;

namespace CodexMultiLauncher;

public sealed class GroupChatStore
{
    private readonly string _file;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public GroupChatStore(string root) => _file = ProfilePathSafety.GetContainedFullPath(root, "data", "group-chat.json");
    public string FilePath => _file;

    public ObservableCollection<GroupChatRoom> Load()
    {
        lock (_gate)
        {
            ProfilePathSafety.ThrowIfReparsePath(_file);
            if (!File.Exists(_file)) return [];
            if (new FileInfo(_file).Length > 32 * 1024 * 1024) throw new InvalidOperationException("群聊文件超过 32 MiB，请先备份并整理记录。");
            try
            {
                var document = JsonSerializer.Deserialize<ChatDocument>(File.ReadAllText(_file), Json)
                    ?? throw new InvalidOperationException("群聊文件为空。");
                if (document.Version != 1) throw new InvalidOperationException("此版本不能读取该群聊文件。");
                Validate(document.Rooms);
                foreach (var message in document.Rooms.SelectMany(room => room.Messages))
                    if (message.State is ChatMessageState.Pending or ChatMessageState.Running) message.State = ChatMessageState.Interrupted;
                return document.Rooms;
            }
            catch (JsonException exception) { throw new InvalidOperationException("群聊记录格式无效，原文件已保留。", exception); }
        }
    }

    public void Save(ObservableCollection<GroupChatRoom> rooms)
    {
        lock (_gate)
        {
            Validate(rooms);
            ProfilePathSafety.ThrowIfReparsePath(_file);
            var directory = Path.GetDirectoryName(_file)!; Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".group-chat.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new ChatDocument(1, rooms), Json));
                if (new FileInfo(temporary).Length > 32 * 1024 * 1024) throw new InvalidOperationException("群聊记录已超过 32 MiB，未覆盖原文件。");
                File.Move(temporary, _file, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static void Validate(ObservableCollection<GroupChatRoom>? rooms)
    {
        if (rooms is null || rooms.Count > 200) throw new InvalidOperationException("群聊列表无效或超过 200 个群聊。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in rooms)
        {
            if (room is null || !Guid.TryParseExact(room.Id, "N", out _) || !ids.Add(room.Id)
                || string.IsNullOrWhiteSpace(room.Title) || room.Title.Length > 80
                || !Path.IsPathFullyQualified(room.WorkingDirectory) || room.Agents is null || room.Messages is null)
                throw new InvalidOperationException("群聊设置无效，原记录不会被覆盖。");
            GroupChatRouting.ValidateAgents(room.Agents);
            var messages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var message in room.Messages)
                if (message is null || !Guid.TryParseExact(message.Id, "N", out _) || !messages.Add(message.Id)
                    || !Enum.IsDefined(message.Role) || !Enum.IsDefined(message.State) || message.Text is null
                    || message.Text.Length > 1_000_000 || string.IsNullOrWhiteSpace(message.Sender))
                    throw new InvalidOperationException("群聊消息无效，原记录不会被覆盖。");
        }
    }

    private sealed record ChatDocument(int Version, ObservableCollection<GroupChatRoom> Rooms);
}
