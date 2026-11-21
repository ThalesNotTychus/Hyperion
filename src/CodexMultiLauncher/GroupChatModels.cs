using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CodexMultiLauncher;

public abstract class ChatObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

[JsonConverter(typeof(JsonStringEnumConverter<ChatMessageRole>))]
public enum ChatMessageRole { User, Agent }

[JsonConverter(typeof(JsonStringEnumConverter<ChatMessageState>))]
public enum ChatMessageState { Pending, Running, Completed, Failed, Cancelled, Interrupted }

[JsonConverter(typeof(JsonStringEnumConverter<ChatSandboxMode>))]
public enum ChatSandboxMode { ReadOnly, WorkspaceWrite }

public sealed record ChatExecutionStep(
    string Id,
    string Kind,
    string Title,
    string Status,
    string? Target = null,
    string? Meta = null,
    string? Detail = null);

public sealed record GroupChatAgent(
    string Id,
    string Alias,
    string WorkspaceId,
    string? Model = null,
    string? ReasoningEffort = null,
    string? ServiceTier = null);

public sealed class GroupChatMessage : ChatObservable
{
    private string _text = string.Empty;
    private string _thinkingText = string.Empty;
    private string _activityStatus = string.Empty;
    private ChatMessageState _state = ChatMessageState.Completed;
    private string? _failoverNotice;
    private string? _modelSummary;
    private IReadOnlyList<ChatExecutionStep> _steps = [];

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ChatMessageRole Role { get; set; }
    public string Sender { get; set; } = string.Empty;
    public string? WorkspaceName { get; set; }
    public string? WorkspaceId { get; set; }
    public string? AgentId { get; set; }
    public string? ModelSummary { get => _modelSummary; set { _modelSummary = value; Changed(); } }
    public DateTimeOffset SentAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Text { get => _text; set { _text = value ?? string.Empty; Changed(); } }
    public string ThinkingText { get => _thinkingText; set { _thinkingText = value ?? string.Empty; Changed(); } }
    [JsonIgnore] public string ActivityStatus { get => _activityStatus; set { _activityStatus = value ?? string.Empty; Changed(); } }
    public ChatMessageState State { get => _state; set { _state = value; Changed(); Changed(nameof(StateText)); } }
    public string? FailoverNotice { get => _failoverNotice; set { _failoverNotice = value; Changed(); } }
    public IReadOnlyList<ChatExecutionStep> Steps { get => _steps; set { _steps = value ?? []; Changed(); } }

    [JsonIgnore] public string Header => Role == ChatMessageRole.User ? "你" : Sender + " · " + WorkspaceName;
    [JsonIgnore] public string TimeText => SentAtUtc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    [JsonIgnore] public string StateText => State switch
    {
        ChatMessageState.Pending => "等待回复",
        ChatMessageState.Running => "正在回复",
        ChatMessageState.Completed => "已完成",
        ChatMessageState.Failed => "失败",
        ChatMessageState.Cancelled => "已停止",
        _ => "上次运行中断",
    };
}

public sealed class GroupChatRoom : ChatObservable
{
    private string _title = "新群聊";
    private string _workingDirectory = string.Empty;
    private ChatSandboxMode _sandboxMode = ChatSandboxMode.ReadOnly;
    private string? _repositoryRoot;
    private string? _worktreeBranch;
    private bool _isRunning;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get => _title; set { _title = value; Changed(); } }
    public string WorkingDirectory { get => _workingDirectory; set { _workingDirectory = value; Changed(); } }
    public ChatSandboxMode SandboxMode { get => _sandboxMode; set { _sandboxMode = value; Changed(); } }
    public string? RepositoryRoot { get => _repositoryRoot; set { _repositoryRoot = value; Changed(); } }
    public string? WorktreeBranch { get => _worktreeBranch; set { _worktreeBranch = value; Changed(); Changed(nameof(IsIsolatedWorktree)); } }
    public string? DedicatedWorkspaceId { get; set; }
    public string? PrimaryWorkspaceId { get; set; }
    public ObservableCollection<GroupChatAgent> Agents { get; set; } = [];
    public ObservableCollection<GroupChatMessage> Messages { get; set; } = [];

    [JsonIgnore] public bool IsDedicatedAgentRoom => !string.IsNullOrWhiteSpace(DedicatedWorkspaceId);
    [JsonIgnore] public bool IsIsolatedWorktree => !string.IsNullOrWhiteSpace(WorktreeBranch);
    [JsonIgnore] public bool IsRunning { get => _isRunning; internal set { _isRunning = value; Changed(); Changed(nameof(ActivityText)); } }
    [JsonIgnore] public string ActivityText => IsRunning ? "正在回复…" : $"{Agents.Count} 个身份";
    public void RefreshConfiguration() { Changed(nameof(Agents)); Changed(nameof(ActivityText)); Changed(nameof(SandboxMode)); Changed(nameof(WorktreeBranch)); }
}

public sealed record GroupChatUpdate(string Text, string Activity, IReadOnlyList<ChatExecutionStep>? Steps = null, string? Thinking = null);

public sealed record ChatImageAttachment(
    string MimeType,
    string Base64Data,
    string? Label = null);

public sealed record ChatHistoryTurn(
    string Role,
    string Speaker,
    string Text);

public sealed record GroupChatRun(
    GroupChatAgent Agent,
    CodexWorkspace Workspace,
    string WorkingDirectory,
    string Prompt,
    ChatSandboxMode SandboxMode = ChatSandboxMode.ReadOnly,
    string? IncrementalPrompt = null,
    IReadOnlyList<ChatImageAttachment>? Attachments = null,
    IReadOnlyList<ChatHistoryTurn>? NativeTurns = null);

public interface IGroupChatRunner
{
    Task<string> RunAsync(GroupChatRun request, IProgress<GroupChatUpdate> progress, CancellationToken token);
}
