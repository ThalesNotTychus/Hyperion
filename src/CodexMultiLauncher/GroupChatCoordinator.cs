using System.Collections.ObjectModel;
using System.Text;

namespace CodexMultiLauncher;

/// <summary>
/// Routes explicit mentions across Codex, Claude, and Antigravity identities, auto-enrolls
/// all available isolated accounts into the Command Center roster, and performs seamless quota failover.
/// </summary>
public sealed class GroupChatCoordinator
{
    private readonly GroupChatStore _store;
    private readonly Func<IReadOnlyList<CodexWorkspace>> _workspaces;
    private readonly IGroupChatRunner _runner;
    private readonly AccountPoolService? _accountPool;
    private readonly Func<UiSettings>? _settingsProvider;
    private readonly ModelCatalogService? _modelCatalog;
    private readonly object _gate = new();
    private readonly Dictionary<string, RoomRun> _running = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenWorkspaceIds = new(StringComparer.Ordinal);
    private bool _closing;

    public GroupChatCoordinator(
        GroupChatStore store,
        Func<IReadOnlyList<CodexWorkspace>> workspaces,
        IGroupChatRunner? runner = null,
        AccountPoolService? accountPool = null,
        Func<UiSettings>? settingsProvider = null,
        ModelCatalogService? modelCatalog = null)
    {
        _store = store;
        _workspaces = workspaces;
        _runner = runner ?? new MultiEngineChatRunner();
        _accountPool = accountPool;
        _settingsProvider = settingsProvider;
        _modelCatalog = modelCatalog;
        Rooms = store.Load();
        foreach (var boundId in Rooms.SelectMany(r => r.Agents).Select(a => a.WorkspaceId))
            _seenWorkspaceIds.Add(boundId);
    }

    public ObservableCollection<GroupChatRoom> Rooms { get; }
    public event Action<string>? Warning;
    public event Action<GroupChatRoom>? RoomUpdated;
    public string? PersistenceWarning { get; private set; }

    public GroupChatRoom CreateRoom(
        string directory,
        string? title = null,
        ChatSandboxMode sandboxMode = ChatSandboxMode.ReadOnly,
        string? repositoryRoot = null,
        string? worktreeBranch = null,
        string? dedicatedWorkspaceId = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var allWs = _workspaces();
            string roomTitle;
            if (!string.IsNullOrWhiteSpace(title))
            {
                roomTitle = ProfileStore.ValidateDisplayName(title);
            }
            else if (!string.IsNullOrWhiteSpace(dedicatedWorkspaceId))
            {
                var wsName = allWs.FirstOrDefault(w => w.Id == dedicatedWorkspaceId)?.DisplayName ?? "智能体";
                var existingCount = Rooms.Count(r => r.DedicatedWorkspaceId == dedicatedWorkspaceId);
                roomTitle = existingCount == 0 ? $"{wsName} · 专属工作台" : $"{wsName} · 任务 {existingCount + 1}";
            }
            else
            {
                var sharedCount = Rooms.Count(r => !r.IsDedicatedAgentRoom);
                roomTitle = sharedCount == 0 ? "主对话" : $"对话 {sharedCount + 1}";
            }

            var room = new GroupChatRoom
            {
                Title = roomTitle,
                WorkingDirectory = Path.GetFullPath(directory),
                SandboxMode = sandboxMode,
                RepositoryRoot = repositoryRoot,
                WorktreeBranch = worktreeBranch,
                DedicatedWorkspaceId = string.IsNullOrWhiteSpace(dedicatedWorkspaceId) ? null : dedicatedWorkspaceId.Trim(),
            };

            if (room.IsDedicatedAgentRoom)
            {
                var targetWs = allWs.FirstOrDefault(w => w.Id == room.DedicatedWorkspaceId && IsSupportedChatWorkspace(w))
                    ?? throw new InvalidOperationException("所选智能体当前不可用。");
                var alias = BuildUniqueAlias(targetWs, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                room.Agents.Add(new GroupChatAgent(Guid.NewGuid().ToString("N"), alias, targetWs.Id));
            }
            else
            {
                PopulateDefaultAgentsForRoom(room, allWs);
            }

            Rooms.Add(room);
            try { _store.Save(Rooms); }
            catch { Rooms.Remove(room); throw; }
            return room;
        }
    }

    /// <summary>
    /// Prunes deleted/hidden accounts from room rosters and automatically enrolls newly created
    /// or discovered accounts into shared group-chat rooms (while keeping dedicated 1v1 rooms isolated).
    /// </summary>
    public void SyncRosterWithWorkspaces(bool upgradeEmptyDefaultRoomToWrite = false, bool forceReEnrollAll = false)
    {
        lock (_gate)
        {
            if (_closing) return;
            var eligible = OrderWorkspacesForRoster(_workspaces());
            var validIds = eligible.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
            _seenWorkspaceIds.IntersectWith(validIds);

            var anyChanged = false;
            foreach (var room in Rooms)
            {
                if (room.IsRunning) continue;
                var roomChanged = false;

                if (room.IsDedicatedAgentRoom)
                {
                    var targetWs = eligible.FirstOrDefault(w => w.Id == room.DedicatedWorkspaceId);
                    if (targetWs is null)
                    {
                        if (room.Agents.Count > 0) { room.Agents.Clear(); roomChanged = true; }
                    }
                    else if (room.Agents.Count != 1 || room.Agents[0].WorkspaceId != targetWs.Id)
                    {
                        var alias = BuildUniqueAlias(targetWs, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        room.Agents.Clear();
                        room.Agents.Add(new GroupChatAgent(Guid.NewGuid().ToString("N"), alias, targetWs.Id));
                        roomChanged = true;
                    }

                    if (roomChanged)
                    {
                        room.RefreshConfiguration();
                        anyChanged = true;
                    }
                    continue;
                }

                if (upgradeEmptyDefaultRoomToWrite && room.Messages.Count == 0 && room.SandboxMode == ChatSandboxMode.ReadOnly)
                {
                    room.SandboxMode = ChatSandboxMode.WorkspaceWrite;
                    roomChanged = true;
                }

                for (var i = room.Agents.Count - 1; i >= 0; i--)
                {
                    if (!validIds.Contains(room.Agents[i].WorkspaceId))
                    {
                        room.Agents.RemoveAt(i);
                        roomChanged = true;
                    }
                }

                var boundIds = room.Agents.Select(a => a.WorkspaceId).ToHashSet(StringComparer.Ordinal);
                var existingAliases = room.Agents.Select(a => a.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var ws in eligible)
                {
                    if (room.Agents.Count >= 8) break;
                    if (boundIds.Contains(ws.Id)) continue;
                    if (!forceReEnrollAll && _seenWorkspaceIds.Contains(ws.Id)) continue;
                    var alias = BuildUniqueAlias(ws, existingAliases);
                    room.Agents.Add(new GroupChatAgent(Guid.NewGuid().ToString("N"), alias, ws.Id));
                    boundIds.Add(ws.Id);
                    existingAliases.Add(alias);
                    roomChanged = true;
                }

                if (roomChanged)
                {
                    room.RefreshConfiguration();
                    anyChanged = true;
                }
            }

            foreach (var id in validIds)
                _seenWorkspaceIds.Add(id);

            if (anyChanged) SaveResult();
        }
    }

    private static void PopulateDefaultAgentsForRoom(GroupChatRoom room, IReadOnlyList<CodexWorkspace> allWorkspaces)
    {
        var eligible = OrderWorkspacesForRoster(allWorkspaces);
        var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codexIndex = 0;

        foreach (var ws in eligible)
        {
            if (room.Agents.Count >= 8) break;
            string alias;
            if (ws.Client == ClientKind.Codex && codexIndex < 2)
            {
                codexIndex++;
                alias = $"GPT{codexIndex}";
                usedAliases.Add(alias);
            }
            else
            {
                alias = BuildUniqueAlias(ws, usedAliases);
                usedAliases.Add(alias);
            }
            room.Agents.Add(new GroupChatAgent(Guid.NewGuid().ToString("N"), alias, ws.Id));
        }
    }

    private static List<CodexWorkspace> OrderWorkspacesForRoster(IReadOnlyList<CodexWorkspace> workspaces) =>
        workspaces
            .Where(IsSupportedChatWorkspace)
            .OrderBy(w => w.Client is ClientKind.DirectApi or ClientKind.MarketCli ? 0 : (w.Kind == CodexWorkspaceKind.Managed ? 1 : 2))
            .ThenBy(w => w.Client switch
            {
                ClientKind.DirectApi => 0,
                ClientKind.MarketCli => 1,
                ClientKind.Codex => 2,
                ClientKind.Claude => 3,
                ClientKind.Antigravity => 4,
                _ => 5,
            })
            .ToList();

    public static string BuildUniqueAlias(CodexWorkspace workspace, IReadOnlySet<string> usedAliases)
    {
        var sb = new StringBuilder();
        foreach (var ch in workspace.DisplayName.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-') sb.Append(ch);
        }

        var baseAlias = sb.ToString();
        if (string.IsNullOrEmpty(baseAlias) || !char.IsLetter(baseAlias[0]))
        {
            baseAlias = workspace.Client switch
            {
                ClientKind.DirectApi => "API",
                ClientKind.MarketCli => "CLI",
                ClientKind.Claude => "Claude",
                ClientKind.Antigravity => "AGY",
                _ => "Codex",
            } + baseAlias;
        }
        if (baseAlias.Length > 24) baseAlias = baseAlias[..24];

        var candidate = baseAlias;
        var seq = 2;
        while (usedAliases.Contains(candidate))
        {
            candidate = $"{baseAlias}{seq++}";
        }
        return candidate;
    }

    public void DeleteRoom(GroupChatRoom room)
    {
        lock (_gate)
        {
            RequireRoom(room);
            if (room.IsRunning) throw new InvalidOperationException("请先停止正在运行的回复，再删除此任务会话。");
            if (!room.IsDedicatedAgentRoom && Rooms.Count(r => !r.IsDedicatedAgentRoom) <= 1)
                throw new InvalidOperationException("请至少保留一个指挥舱群聊会话。");
            var index = Rooms.IndexOf(room);
            Rooms.Remove(room);
            try { _store.Save(Rooms); }
            catch { Rooms.Insert(index, room); throw; }
        }
    }

    public void Configure(
        GroupChatRoom room,
        string title,
        string directory,
        IEnumerable<GroupChatAgent> agents,
        ChatSandboxMode? sandboxMode = null,
        string? repositoryRoot = null,
        string? worktreeBranch = null)
    {
        lock (_gate)
        {
            RequireRoom(room);
            if (room.IsRunning) throw new InvalidOperationException("请先停止本群聊的回复，再修改身份。");
            title = ProfileStore.ValidateDisplayName(title);
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new InvalidOperationException("请选择存在的项目目录。");
            var list = agents.ToArray(); GroupChatRouting.ValidateAgents(list);
            if (list.Length == 0) throw new InvalidOperationException("请至少添加一个聊天身份。");
            foreach (var agent in list) ResolveWorkspace(agent);

            var oldTitle = room.Title;
            var oldDirectory = room.WorkingDirectory;
            var oldAgents = room.Agents;
            var oldSandbox = room.SandboxMode;
            var oldRepo = room.RepositoryRoot;
            var oldBranch = room.WorktreeBranch;

            room.Title = title;
            room.WorkingDirectory = Path.GetFullPath(directory);
            room.Agents = new(list);
            if (sandboxMode is not null) room.SandboxMode = sandboxMode.Value;
            if (repositoryRoot is not null) room.RepositoryRoot = string.IsNullOrWhiteSpace(repositoryRoot) ? null : repositoryRoot;
            if (worktreeBranch is not null) room.WorktreeBranch = string.IsNullOrWhiteSpace(worktreeBranch) ? null : worktreeBranch;
            room.RefreshConfiguration();
            try { _store.Save(Rooms); }
            catch
            {
                room.Title = oldTitle;
                room.WorkingDirectory = oldDirectory;
                room.Agents = oldAgents;
                room.SandboxMode = oldSandbox;
                room.RepositoryRoot = oldRepo;
                room.WorktreeBranch = oldBranch;
                room.RefreshConfiguration();
                throw;
            }
        }
        RoomUpdated?.Invoke(room);
    }

    public void SetRoomSandboxMode(GroupChatRoom room, ChatSandboxMode mode)
    {
        lock (_gate)
        {
            RequireRoom(room);
            room.SandboxMode = mode;
            room.RefreshConfiguration();
            SaveResult();
        }
        RoomUpdated?.Invoke(room);
    }

    public void SetRoomPrimaryWorkspace(GroupChatRoom room, string? workspaceId)
    {
        lock (_gate)
        {
            RequireRoom(room);
            if (!string.IsNullOrWhiteSpace(workspaceId))
            {
                var targetId = workspaceId.Trim();
                var ws = _workspaces().FirstOrDefault(w => string.Equals(w.Id, targetId, StringComparison.Ordinal) && IsSupportedChatWorkspace(w))
                    ?? throw new InvalidOperationException("所选主力智能体当前不可用。");
                room.PrimaryWorkspaceId = ws.Id;
                if (!room.Agents.Any(a => string.Equals(a.WorkspaceId, ws.Id, StringComparison.Ordinal)) && room.Agents.Count < 8)
                {
                    var used = room.Agents.Select(a => a.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    room.Agents.Add(new GroupChatAgent(Guid.NewGuid().ToString("N"), BuildUniqueAlias(ws, used), ws.Id));
                }
            }
            else
            {
                room.PrimaryWorkspaceId = null;
            }
            room.RefreshConfiguration();
            SaveResult();
        }
        RoomUpdated?.Invoke(room);
    }

    public GroupChatAgent UpdateAgentModelOptions(
        GroupChatRoom room,
        string? agentId,
        string? workspaceId,
        string? model,
        string? reasoningEffort,
        string? serviceTier)
    {
        GroupChatAgent result;
        lock (_gate)
        {
            RequireRoom(room);
            if (room.IsRunning) throw new InvalidOperationException("请先等待或停止当前回复，再切换模型或思考参数。");

            var cleanModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            var cleanEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort.Trim().ToLowerInvariant();
            var cleanTier = string.IsNullOrWhiteSpace(serviceTier) ||
                            string.Equals(serviceTier.Trim(), "default", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(serviceTier.Trim(), "standard", StringComparison.OrdinalIgnoreCase)
                ? null
                : serviceTier.Trim().ToLowerInvariant();

            var idx = -1;
            if (!string.IsNullOrWhiteSpace(agentId))
                idx = room.Agents.ToList().FindIndex(a => string.Equals(a.Id, agentId, StringComparison.Ordinal));
            if (idx < 0 && !string.IsNullOrWhiteSpace(workspaceId))
                idx = room.Agents.ToList().FindIndex(a => string.Equals(a.WorkspaceId, workspaceId, StringComparison.Ordinal));
            if (idx < 0 && !string.IsNullOrWhiteSpace(room.PrimaryWorkspaceId))
                idx = room.Agents.ToList().FindIndex(a => string.Equals(a.WorkspaceId, room.PrimaryWorkspaceId, StringComparison.Ordinal));

            if (idx < 0 && !string.IsNullOrWhiteSpace(workspaceId))
            {
                var ws = _workspaces().FirstOrDefault(w => string.Equals(w.Id, workspaceId, StringComparison.Ordinal) && IsSupportedChatWorkspace(w))
                    ?? throw new InvalidOperationException("所选智能体当前不可用。");
                var used = room.Agents.Select(a => a.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var added = new GroupChatAgent(Guid.NewGuid().ToString("N"), BuildUniqueAlias(ws, used), ws.Id, cleanModel, cleanEffort, cleanTier);
                GroupChatRouting.ValidateAgents([.. room.Agents, added]);
                room.Agents.Add(added);
                result = added;
            }
            else if (idx >= 0)
            {
                var updated = room.Agents[idx] with
                {
                    Model = cleanModel,
                    ReasoningEffort = cleanEffort,
                    ServiceTier = cleanTier,
                };
                var previewList = room.Agents.ToArray();
                previewList[idx] = updated;
                GroupChatRouting.ValidateAgents(previewList);
                room.Agents[idx] = updated;
                result = updated;
            }
            else
            {
                throw new InvalidOperationException("当前会话没有可配置模型的智能体身份。");
            }

            room.RefreshConfiguration();
            SaveResult();
        }
        RoomUpdated?.Invoke(room);
        return result;
    }

    public GroupChatAgent SwitchAgentWorkspace(GroupChatRoom room, string agentId, string newWorkspaceId)
    {
        lock (_gate)
        {
            RequireRoom(room);
            if (room.IsRunning) throw new InvalidOperationException("请先停止当前群聊回复，再切换绑定账号。");
            var idx = room.Agents.ToList().FindIndex(item => string.Equals(item.Id, agentId, StringComparison.Ordinal));
            if (idx < 0) throw new InvalidOperationException("未找到指定的聊天身份。");
            var updated = room.Agents[idx] with { WorkspaceId = newWorkspaceId };
            ResolveWorkspace(updated);
            var previous = room.Agents[idx];
            room.Agents[idx] = updated;
            room.RefreshConfiguration();
            try { _store.Save(Rooms); }
            catch { room.Agents[idx] = previous; room.RefreshConfiguration(); throw; }
            RoomUpdated?.Invoke(room);
            return updated;
        }
    }

    public void RebindWorkspaceId(string oldWorkspaceId, string newWorkspaceId)
    {
        if (string.IsNullOrWhiteSpace(oldWorkspaceId) || string.IsNullOrWhiteSpace(newWorkspaceId)) return;
        List<GroupChatRoom> updatedRooms = [];
        lock (_gate)
        {
            if (_closing) return;
            foreach (var room in Rooms)
            {
                if (room.IsRunning) continue;
                var changed = false;
                if (string.Equals(room.PrimaryWorkspaceId, oldWorkspaceId, StringComparison.Ordinal))
                {
                    room.PrimaryWorkspaceId = newWorkspaceId;
                    changed = true;
                }
                if (string.Equals(room.DedicatedWorkspaceId, oldWorkspaceId, StringComparison.Ordinal))
                {
                    room.DedicatedWorkspaceId = newWorkspaceId;
                    changed = true;
                }
                for (var i = 0; i < room.Agents.Count; i++)
                {
                    if (string.Equals(room.Agents[i].WorkspaceId, oldWorkspaceId, StringComparison.Ordinal))
                    {
                        room.Agents[i] = room.Agents[i] with { WorkspaceId = newWorkspaceId };
                        changed = true;
                    }
                }
                if (changed)
                {
                    room.RefreshConfiguration();
                    updatedRooms.Add(room);
                }
            }

            if (updatedRooms.Count > 0)
            {
                _seenWorkspaceIds.Remove(oldWorkspaceId);
                _seenWorkspaceIds.Add(newWorkspaceId);
                SaveResult();
            }
        }

        foreach (var room in updatedRooms)
            RoomUpdated?.Invoke(room);
    }

    public Task SendAsync(GroupChatRoom room, string text) =>
        SendAsync(room, text, attachments: null);

    public async Task SendAsync(GroupChatRoom room, string text, IReadOnlyList<ChatImageAttachment>? attachments)
    {
        RoomRun run;
        List<(GroupChatRun Request, GroupChatMessage Reply)> requests;
        lock (_gate)
        {
            RequireRoom(room);
            if (room.IsRunning) throw new InvalidOperationException("本会话正在回复，可以停止后再发送，或打开另一个会话。");
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("请输入消息。");
            if (text.Length > GroupChatRouting.MaximumInputCharacters) throw new InvalidOperationException("单条消息最多 20,000 字。");

            IReadOnlyList<GroupChatAgent> recipients;
            if (room.IsDedicatedAgentRoom)
            {
                if (room.Agents.Count == 0) throw new InvalidOperationException("该专属会话绑定的智能体不可用。");
                recipients = [room.Agents[0]];
            }
            else if (!GroupChatRouting.HasExplicitMention(text))
            {
                if (room.Agents.Count == 0) throw new InvalidOperationException("当前项目尚未绑定可用智能体，请先在账号池添加或启用账号。");
                var targetWsId = room.PrimaryWorkspaceId;
                GroupChatAgent? primaryAgent = null;
                if (!string.IsNullOrWhiteSpace(targetWsId))
                {
                    primaryAgent = room.Agents.FirstOrDefault(a => string.Equals(a.WorkspaceId, targetWsId, StringComparison.Ordinal));
                }
                if (primaryAgent is null && _accountPool is not null)
                {
                    var readySet = _accountPool.BuildSummary(_workspaces())
                        .Entries
                        .Where(e => e.AuthReady)
                        .Select(e => e.WorkspaceId)
                        .ToHashSet(StringComparer.Ordinal);
                    primaryAgent = room.Agents.FirstOrDefault(a => readySet.Contains(a.WorkspaceId));
                }
                primaryAgent ??= room.Agents[0];
                recipients = [primaryAgent];
            }
            else
            {
                recipients = GroupChatRouting.Resolve(text, room.Agents);
            }

            if (!Directory.Exists(room.WorkingDirectory)) throw new InvalidOperationException("项目目录不存在，请先打开会话设置。");
            var snapshotHistory = room.Messages.ToArray();
            var safeAttachments = attachments is { Count: > 0 } ? attachments : null;
            requests = recipients.Select(agent =>
            {
                var workspace = ResolveEffectiveWorkspace(agent, out var preflightNotice);
                var prompt = GroupChatRouting.BuildPrompt(agent, snapshotHistory, text, room.SandboxMode);
                var incremental = GroupChatRouting.BuildIncrementalPrompt(agent, snapshotHistory, text);
                var nativeTurns = GroupChatRouting.BuildNativeTurns(agent, snapshotHistory, text);
                var modelSummary = _modelCatalog?.FormatModelSummary(workspace, agent)
                    ?? ModelCatalogService.FormatModelSummaryCore(null, workspace, agent);
                return (
                    new GroupChatRun(
                        agent,
                        workspace,
                        room.WorkingDirectory,
                        prompt,
                        room.SandboxMode,
                        IncrementalPrompt: incremental,
                        Attachments: safeAttachments,
                        NativeTurns: nativeTurns),
                    new GroupChatMessage
                    {
                        Role = ChatMessageRole.Agent,
                        Sender = agent.Alias,
                        AgentId = agent.Id,
                        WorkspaceId = workspace.Id,
                        WorkspaceName = workspace.DisplayName,
                        ModelSummary = modelSummary,
                        FailoverNotice = preflightNotice,
                        State = ChatMessageState.Pending,
                    }
                );
            }).ToList();

            var user = new GroupChatMessage { Role = ChatMessageRole.User, Sender = "你", Text = text };
            room.Messages.Add(user);
            foreach (var (_, reply) in requests) room.Messages.Add(reply);
            room.IsRunning = true;
            try { _store.Save(Rooms); }
            catch
            {
                room.IsRunning = false; room.Messages.Remove(user);
                foreach (var (_, reply) in requests) room.Messages.Remove(reply);
                throw;
            }
            run = new RoomRun(); _running.Add(room.Id, run);
        }
        RoomUpdated?.Invoke(room);
        try { await Task.WhenAll(requests.Select(pair => ReplyAsync(room, pair.Request, pair.Reply, run.Cancellation.Token))); }
        finally
        {
            lock (_gate)
            {
                room.IsRunning = false; SaveResult();
                _running.Remove(room.Id); run.Cancellation.Dispose(); run.Completion.TrySetResult();
            }
            RoomUpdated?.Invoke(room);
        }
    }

    private async Task ReplyAsync(GroupChatRoom room, GroupChatRun initialRequest, GroupChatMessage reply, CancellationToken token)
    {
        var currentRequest = initialRequest;
        var triedWorkspaces = new HashSet<string>(StringComparer.Ordinal) { currentRequest.Workspace.Id };

        var progress = new Progress<GroupChatUpdate>(update =>
        {
            lock (_gate)
            {
                if (reply.State != ChatMessageState.Running) return;
                reply.Text = update.Text;
                if (!string.IsNullOrWhiteSpace(update.Activity)) reply.ActivityStatus = update.Activity;
                if (update.Thinking is not null) reply.ThinkingText = update.Thinking;
                if (update.Steps is not null) reply.Steps = update.Steps;
            }
            var stepHeadline = !string.IsNullOrWhiteSpace(update.Activity)
                ? update.Activity
                : (update.Steps is { Count: > 0 } ? update.Steps[^1].Title : (!string.IsNullOrWhiteSpace(update.Text) ? "正在输出回复内容…" : null));
            if (!string.IsNullOrWhiteSpace(stepHeadline))
                _accountPool?.RecordStepProgress(currentRequest.Workspace.Id, stepHeadline);
            RoomUpdated?.Invoke(room);
        });

        lock (_gate)
        {
            reply.State = ChatMessageState.Running;
            reply.ActivityStatus = "正在连接智能体引擎并准备思考…";
        }
        RoomUpdated?.Invoke(room);

        while (true)
        {
            _accountPool?.RecordTurnStarted(currentRequest.Workspace.Id, $"正在执行：{room.Title}");
            var succeeded = false;
            try
            {
                var answer = await _runner.RunAsync(currentRequest, progress, token);
                succeeded = !token.IsCancellationRequested;
                lock (_gate)
                {
                    reply.Text = answer;
                    reply.State = token.IsCancellationRequested ? ChatMessageState.Cancelled : ChatMessageState.Completed;
                }
                break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                lock (_gate) reply.State = ChatMessageState.Cancelled;
                break;
            }
            catch (Exception exception)
            {
                if (TrySelectFailover(currentRequest, exception, triedWorkspaces, out var fallbackWorkspace, out var notice))
                {
                    _accountPool?.RecordFailoverSave();
                    triedWorkspaces.Add(fallbackWorkspace.Id);
                    currentRequest = currentRequest with { Workspace = fallbackWorkspace, IncrementalPrompt = null };
                    lock (_gate)
                    {
                        reply.WorkspaceId = fallbackWorkspace.Id;
                        reply.WorkspaceName = fallbackWorkspace.DisplayName;
                        reply.FailoverNotice = notice;
                        reply.Text = string.Empty;
                        reply.ThinkingText = string.Empty;
                        reply.ActivityStatus = "已自动切换备用账号，正在重新发起思考…";
                        reply.Steps = [];
                    }
                    RoomUpdated?.Invoke(room);
                    continue;
                }

                lock (_gate)
                {
                    reply.Text = exception is InvalidOperationException or TimeoutException
                        ? exception.Message
                        : "回复未能完成，请检查此账号的登录、额度和网络后重试。";
                    reply.State = ChatMessageState.Failed;
                }
                break;
            }
            finally
            {
                _accountPool?.RecordTurnFinished(currentRequest.Workspace.Id, succeeded);
            }
        }

        lock (_gate) SaveResult();
        RoomUpdated?.Invoke(room);
    }

    private CodexWorkspace ResolveEffectiveWorkspace(GroupChatAgent agent, out string? failoverNotice)
    {
        failoverNotice = null;
        var primary = ResolveWorkspace(agent);
        if (_accountPool is null) return primary;
        if (primary.Client is not (ClientKind.Codex or ClientKind.DirectApi or ClientKind.MarketCli or ClientKind.Antigravity or ClientKind.Claude)) return primary;
        var settings = _settingsProvider?.Invoke() ?? new UiSettings();
        if (!settings.AutoFailoverEnabled) return primary;
        if (!_accountPool.IsPreFlightExhausted(primary.Id, settings.FailoverThresholdPercent)) return primary;

        var fallback = _accountPool.SelectFailoverWorkspace(_workspaces(), primary.Id, null, settings.FailoverThresholdPercent);
        if (fallback is null) return primary;
        _accountPool.RecordFailoverSave();
        failoverNotice = $"算力池预检：{primary.DisplayName} 额度或预算已耗尽，自动无感接力至 {fallback.DisplayName}";
        return fallback;
    }

    private bool TrySelectFailover(
        GroupChatRun failedRequest,
        Exception exception,
        IReadOnlySet<string> triedWorkspaces,
        out CodexWorkspace fallbackWorkspace,
        out string notice)
    {
        fallbackWorkspace = null!;
        notice = string.Empty;
        if (_accountPool is null) return false;
        var settings = _settingsProvider?.Invoke() ?? new UiSettings();
        if (!settings.AutoFailoverEnabled) return false;
        if (!AccountPoolService.IsQuotaOrRateLimitFailure(exception.Message)) return false;

        _accountPool.MarkRateLimited(failedRequest.Workspace.Id);
        var candidate = _accountPool.SelectFailoverWorkspace(_workspaces(), failedRequest.Workspace.Id, triedWorkspaces, settings.FailoverThresholdPercent);
        if (candidate is null) return false;
        fallbackWorkspace = candidate;
        notice = $"触发限流保护：{failedRequest.Workspace.DisplayName} → 已自动无感接力至 {candidate.DisplayName}";
        return true;
    }

    public void Stop(GroupChatRoom room)
    {
        lock (_gate) if (_running.TryGetValue(room.Id, out var run)) run.Cancellation.Cancel();
    }

    public async Task ShutdownAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            _closing = true;
            foreach (var run in _running.Values) run.Cancellation.Cancel();
            tasks = _running.Values.Select(run => run.Completion.Task).ToArray();
        }
        await Task.WhenAll(tasks);
    }

    public static bool IsSupportedChatWorkspace(CodexWorkspace workspace) =>
        workspace.IsLaunchable
        && ((workspace.Client == ClientKind.Codex && workspace.CanReadUsage)
            || workspace.Client == ClientKind.Claude
            || workspace.Client == ClientKind.Antigravity
            || workspace.Client == ClientKind.DirectApi
            || workspace.Client == ClientKind.MarketCli);

    private CodexWorkspace ResolveWorkspace(GroupChatAgent agent) =>
        _workspaces().FirstOrDefault(workspace => workspace.Id == agent.WorkspaceId && IsSupportedChatWorkspace(workspace))
        ?? throw new InvalidOperationException($"@{agent.Alias} 绑定的账号不可用，请在群聊设置中重新选择。");

    private void RequireRoom(GroupChatRoom room)
    {
        ObjectDisposedException.ThrowIf(_closing, this);
        if (!Rooms.Contains(room)) throw new InvalidOperationException("群聊已不存在。");
    }

    private void SaveResult()
    {
        try { _store.Save(Rooms); PersistenceWarning = null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        { PersistenceWarning = "当前回复仍保留在窗口中，但群聊记录未能保存：" + exception.Message; Warning?.Invoke(PersistenceWarning); }
    }

    private sealed class RoomRun
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
