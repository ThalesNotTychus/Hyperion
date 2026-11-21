using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexMultiLauncher;

/// <summary>
/// Typed JSON-RPC + live event Push bridge between the WebView2 ADE frontend and
/// the native .NET 10 multi-account / worktree / ConPTY / desktop-orchestration services.
/// </summary>
internal sealed class WebUiBridge
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly MainWindow _host;

    public WebUiBridge(MainWindow host)
    {
        _host = host;
    }

    public async Task HandleRawMessageAsync(string rawJson)
    {
        string? requestId = null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("id", out var idProp))
                requestId = idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : idProp.ToString();
            if (!root.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
                return;

            var method = methodProp.GetString()!;
            var parameters = root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            var result = await DispatchAsync(method, parameters);
            if (requestId is not null)
                _host.PostToShell(new { id = requestId, ok = true, result });
        }
        catch (Exception ex)
        {
            if (requestId is not null)
                _host.PostToShell(new { id = requestId, ok = false, error = ex.Message });
            else
                _host.PushToast("error", ex.Message);
        }
    }

    private async Task<object?> DispatchAsync(string method, JsonElement p)
    {
        switch (method)
        {
            case "app.bootstrap":
                return _host.BuildFullStateDto();

            case "window.minimize":
                _host.WindowState = System.Windows.WindowState.Minimized;
                return new { ok = true };

            case "window.maximizeToggle":
                _host.WindowState = _host.WindowState == System.Windows.WindowState.Maximized
                    ? System.Windows.WindowState.Normal
                    : System.Windows.WindowState.Maximized;
                return new { isMaximized = _host.WindowState == System.Windows.WindowState.Maximized };

            case "window.close":
                _host.Close();
                return new { ok = true };

            case "window.drag":
                try { if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed) _host.DragMove(); }
                catch (InvalidOperationException) { }
                return new { ok = true };

            case "window.syncTheme":
                _host.ApplyNativeChromeTheme(GetString(p, "theme"));
                return new { ok = true };

            case "settings.update":
            {
                var current = _host.CurrentSettings;
                var next = new UiSettings(
                    Theme: GetString(p, "theme") ?? current.Theme,
                    AutoFailoverEnabled: GetBool(p, "autoFailoverEnabled") ?? current.AutoFailoverEnabled,
                    FailoverThresholdPercent: GetInt(p, "failoverThresholdPercent") ?? current.FailoverThresholdPercent,
                    DefaultSandboxMode: GetString(p, "defaultSandboxMode") ?? current.DefaultSandboxMode,
                    LastRepositoryPath: GetString(p, "lastRepositoryPath") ?? current.LastRepositoryPath,
                    LastPreviewUrl: GetString(p, "lastPreviewUrl") ?? current.LastPreviewUrl,
                    StealthBackgroundMode: GetBool(p, "stealthBackgroundMode") ?? current.StealthBackgroundMode,
                    HiddenWorkspaceIds: current.HiddenWorkspaceIds,
                    Language: GetString(p, "language") ?? current.Language,
                    RecentProjects: current.RecentProjects);
                return _host.UpdateSettings(next);
            }

            case "shell.openUrl":
            {
                var rawUrl = RequireString(p, "url").Trim();
                if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                {
                    throw new InvalidOperationException("仅支持打开安全的 http/https 链接。");
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = uri.AbsoluteUri,
                    UseShellExecute = true,
                });
                return new { ok = true };
            }

            case "dialog.pickFolder":
            {
                var initial = GetString(p, "initialDirectory");
                var title = GetString(p, "title") ?? "选择项目或 Git 仓库目录";
                return new { path = _host.PickFolderDialog(title, initial) };
            }

            case "profile.create":
            {
                var name = GetString(p, "displayName") ?? throw new InvalidOperationException("请输入账号名称。");
                var clientStr = GetString(p, "client") ?? "Codex";
                if (!Enum.TryParse<ClientKind>(clientStr, ignoreCase: true, out var client))
                    client = ClientKind.Codex;
                return await _host.CreateProfileFromBridgeAsync(name, client);
            }

            case "profile.rename":
            {
                var id = RequireString(p, "profileId");
                var name = RequireString(p, "displayName");
                return _host.RenameProfileFromBridge(id, name);
            }

            case "profile.delete":
            {
                var id = RequireString(p, "profileId");
                var deleteStorage = GetBool(p, "deleteStorage") ?? false;
                var stopRunning = GetBool(p, "stopRunning") ?? true;
                return await _host.DeleteProfileFromBridgeAsync(id, deleteStorage, stopRunning);
            }

            case "workspace.hide":
            {
                var id = RequireString(p, "workspaceId");
                return _host.HideWorkspaceFromBridge(id);
            }

            case "workspace.restoreHidden":
                return _host.RestoreHiddenWorkspacesFromBridge();

            case "workspace.convertToClientless":
            {
                var id = RequireString(p, "workspaceId");
                return _host.ConvertWorkspaceToClientlessFromBridge(id);
            }

            case "profile.openFolder":
            {
                var workspaceId = GetString(p, "workspaceId");
                var targetPath = GetString(p, "targetPath");
                _host.OpenFolderInExplorer(workspaceId, targetPath);
                return new { ok = true };
            }

            case "agent.upsert":
            {
                var req = ParseUniversalAgentRequest(p);
                var runPing = GetBool(p, "runPingTest") ?? false;
                return await _host.UpsertUniversalAgentFromBridgeAsync(req, runPing);
            }

            case "agent.delete":
            {
                var id = RequireString(p, "id");
                return _host.DeleteUniversalAgentFromBridge(id);
            }

            case "agent.testConnection":
            {
                var req = ParseUniversalAgentRequest(p);
                return await _host.TestUniversalAgentFromBridgeAsync(req);
            }

            case "agent.scanCli":
                return _host.ScanMarketClisFromBridge();

            case "agent.installCli":
            {
                var presetId = RequireString(p, "presetId");
                var cwd = GetString(p, "workingDirectory");
                return _host.InstallMarketCliInTerminalFromBridge(presetId, cwd);
            }

            case "pool.refreshUsage":
            {
                var workspaceId = GetString(p, "workspaceId");
                var all = GetBool(p, "all") ?? string.IsNullOrWhiteSpace(workspaceId);
                var force = GetBool(p, "force") ?? true;
                await _host.RefreshPoolUsageAsync(workspaceId, all, force);
                return _host.BuildFullStateDto();
            }

            case "pool.clearCooldown":
            {
                var workspaceId = RequireString(p, "workspaceId");
                _host.AccountPool.ClearCooldown(workspaceId);
                _host.BroadcastFullState();
                return new { ok = true };
            }

            case "sandbox.repairAll":
                await _host.PrepareManagedSandboxesAsync();
                return _host.BuildFullStateDto();

            case "client.launch":
            {
                var workspaceId = RequireString(p, "workspaceId");
                var workingDir = GetString(p, "workingDirectory");
                var stealth = GetBool(p, "stealth") ?? false;
                return await _host.LaunchOfficialClientAsync(workspaceId, workingDir, stealth);
            }

            case "stealth.hideAll":
                return _host.HideAllClientWindowsNow();

            case "stealth.revealAll":
                return _host.RevealAllClientWindowsNow();

            case "stealth.setLoginWindow":
            {
                var workspaceId = RequireString(p, "workspaceId");
                var reveal = GetBool(p, "reveal") ?? true;
                return await _host.SetWorkspaceLoginWindowVisibilityAsync(workspaceId, reveal);
            }

            case "client.refreshDiscovery":
                await _host.RefreshDiscoveryFromBridgeAsync();
                return _host.BuildFullStateDto();

            case "client.focus":
                return new { ok = _host.ControlInstance(RequireUInt(p, "processId"), GetString(p, "startedAtUtc"), "focus") };

            case "client.close":
                return new { ok = _host.ControlInstance(RequireUInt(p, "processId"), GetString(p, "startedAtUtc"), "close") };

            case "client.kill":
                return new { ok = _host.ControlInstance(RequireUInt(p, "processId"), GetString(p, "startedAtUtc"), "kill") };

            case "client.tileAll":
                return new { ok = _host.TileInstances(GetString(p, "workspaceId")) };

            case "client.minimizeAll":
                return new { ok = _host.MinimizeInstances(GetString(p, "workspaceId")) };

            case "storage.inspect":
                return await _host.InspectWorkspaceStorageAsync(RequireString(p, "workspaceId"));

            case "room.create":
            {
                var title = GetString(p, "title");
                var directory = GetString(p, "directory");
                var createWorktree = GetBool(p, "createWorktree") ?? false;
                var sandboxStr = GetString(p, "sandboxMode");
                var dedicatedWsId = GetString(p, "dedicatedWorkspaceId");
                var primaryWsId = GetString(p, "primaryWorkspaceId");
                return await _host.CreateRoomFromBridgeAsync(title, directory, createWorktree, sandboxStr, dedicatedWsId, primaryWsId);
            }

            case "project.create":
            {
                var parentDir = RequireString(p, "parentDirectory");
                var projectName = RequireString(p, "projectName");
                var initGit = GetBool(p, "initGit") ?? true;
                var primaryWsId = GetString(p, "primaryWorkspaceId");
                return await _host.CreateProjectFromBridgeAsync(parentDir, projectName, initGit, primaryWsId);
            }

            case "project.open":
            {
                var directory = GetString(p, "directory");
                var primaryWsId = GetString(p, "primaryWorkspaceId");
                return await _host.OpenProjectFromBridgeAsync(directory, primaryWsId);
            }

            case "project.remove":
            {
                var directory = RequireString(p, "directory");
                var removeThreads = GetBool(p, "removeThreads") ?? true;
                return await _host.RemoveProjectFromBridgeAsync(directory, removeThreads);
            }

            case "room.setPrimaryWorkspace":
            {
                var roomId = RequireString(p, "roomId");
                var workspaceId = GetString(p, "workspaceId");
                return _host.SetRoomPrimaryWorkspaceFromBridge(roomId, workspaceId);
            }

            case "models.list":
            {
                var workspaceId = RequireString(p, "workspaceId");
                var forceRefresh = GetBool(p, "forceRefresh") ?? false;
                var wakeDaemon = GetBool(p, "wakeDaemon") ?? false;
                return await _host.GetWorkspaceModelCatalogFromBridgeAsync(workspaceId, forceRefresh, wakeDaemon);
            }

            case "models.probeDraft":
            {
                var draft = ParseUniversalAgentRequest(p);
                return await _host.ProbeDraftApiModelsFromBridgeAsync(draft);
            }

            case "room.setAgentModelOptions":
            {
                var roomId = RequireString(p, "roomId");
                var agentId = GetString(p, "agentId");
                var workspaceId = GetString(p, "workspaceId");
                var model = GetString(p, "model");
                var reasoningEffort = GetString(p, "reasoningEffort");
                var serviceTier = GetString(p, "serviceTier");
                return _host.SetRoomAgentModelOptionsFromBridge(roomId, agentId, workspaceId, model, reasoningEffort, serviceTier);
            }

            case "studio.ensureRoom":
            {
                var workspaceId = RequireString(p, "workspaceId");
                var directory = GetString(p, "directory");
                return await _host.EnsureDedicatedAgentRoomAsync(workspaceId, directory);
            }

            case "room.configure":
            {
                var roomId = RequireString(p, "roomId");
                var title = RequireString(p, "title");
                var directory = RequireString(p, "directory");
                var sandboxStr = GetString(p, "sandboxMode");
                var agents = ParseAgents(p);
                return _host.ConfigureRoomFromBridge(roomId, title, directory, sandboxStr, agents);
            }

            case "room.delete":
            {
                var roomId = RequireString(p, "roomId");
                var removeWorktree = GetBool(p, "removeWorktree") ?? false;
                await _host.DeleteRoomFromBridgeAsync(roomId, removeWorktree);
                return _host.BuildFullStateDto();
            }

            case "room.switchAgentWorkspace":
            {
                var roomId = RequireString(p, "roomId");
                var agentId = RequireString(p, "agentId");
                var workspaceId = RequireString(p, "workspaceId");
                return _host.SwitchRoomAgentWorkspace(roomId, agentId, workspaceId);
            }

            case "room.setSandboxMode":
            {
                var roomId = RequireString(p, "roomId");
                var modeStr = RequireString(p, "sandboxMode");
                return _host.SetRoomSandboxModeFromBridge(roomId, modeStr);
            }

            case "room.syncRoster":
                return _host.SyncAllRoomRostersFromBridge();

            case "room.spawnWorktree":
            {
                var roomId = RequireString(p, "roomId");
                var labelHint = GetString(p, "labelHint");
                return await _host.SpawnWorktreeForRoomAsync(roomId, labelHint);
            }

            case "room.mergeWorktree":
            {
                var roomId = RequireString(p, "roomId");
                return await _host.MergeRoomWorktreeAsync(roomId);
            }

            case "room.send":
            {
                var roomId = RequireString(p, "roomId");
                var text = RequireString(p, "text");
                var attachments = ParseAttachments(p);
                _host.StartRoomSend(roomId, text, attachments);
                return new { started = true };
            }

            case "room.stop":
            {
                var roomId = RequireString(p, "roomId");
                _host.StopRoom(roomId);
                return new { stopped = true };
            }

            case "git.inspect":
                return await _host.GitWorktrees.InspectRepositoryAsync(RequireString(p, "directory"), _host.LifetimeToken);

            case "git.diff":
                return await _host.GitWorktrees.GetDiffSnapshotAsync(RequireString(p, "directory"), _host.LifetimeToken);

            case "git.submitReview":
            {
                var roomId = RequireString(p, "roomId");
                var targetMention = RequireString(p, "targetMention");
                var overallNote = GetString(p, "overallNote");
                var comments = ParseDiffComments(p);
                var prompt = GitWorktreeService.BuildInlineReviewPrompt(targetMention, overallNote, comments);
                _host.StartRoomSend(roomId, prompt);
                return new { started = true, prompt };
            }

            case "arena.start":
            {
                var title = GetString(p, "title") ?? string.Empty;
                var repoDir = RequireString(p, "repositoryDirectory");
                var prompt = RequireString(p, "prompt");
                var verifyCmd = GetString(p, "verificationCommand");
                var specs = ParseArenaSpecs(p);
                var session = await _host.Arena.StartRaceAsync(title, repoDir, prompt, specs, verifyCmd, _host.LifetimeToken);
                return session;
            }

            case "arena.stop":
                _host.Arena.StopRace(RequireString(p, "sessionId"));
                return new { ok = true };

            case "arena.diff":
            {
                var sessionId = RequireString(p, "sessionId");
                var contestantId = RequireString(p, "contestantId");
                var session = _host.Arena.GetSessions().FirstOrDefault(s => s.Id == sessionId)
                    ?? throw new InvalidOperationException("找不到该赛马场次。");
                var contestant = session.Contestants.FirstOrDefault(c => c.Id == contestantId)
                    ?? throw new InvalidOperationException("找不到该参赛方案。");
                return await _host.GitWorktrees.GetDiffSnapshotAsync(contestant.WorktreePath, _host.LifetimeToken);
            }

            case "arena.mergeWinner":
            {
                var sessionId = RequireString(p, "sessionId");
                var contestantId = RequireString(p, "contestantId");
                var message = await _host.Arena.MergeWinnerAsync(sessionId, contestantId, _host.LifetimeToken);
                return new { message, sessions = _host.Arena.GetSessions() };
            }

            case "arena.cleanup":
            {
                var sessionId = RequireString(p, "sessionId");
                var keepWinner = GetBool(p, "keepWinner") ?? false;
                var cleaned = await _host.Arena.CleanupSessionWorktreesAsync(sessionId, keepWinner, _host.LifetimeToken);
                _host.PushToast("success", cleaned > 0 ? $"✓ 已清理 {cleaned} 个赛马临时 Worktree 分支目录" : "当前场次无可清理的残留 Worktree 目录");
                return new { cleaned, sessions = _host.Arena.GetSessions() };
            }

            case "arena.delete":
            {
                var sessionId = RequireString(p, "sessionId");
                await _host.Arena.DeleteSessionAsync(sessionId, _host.LifetimeToken);
                _host.PushToast("info", "已移除该场赛马记录并清理关联隔离目录");
                return new { sessions = _host.Arena.GetSessions() };
            }

            case "terminal.create":
            {
                var title = GetString(p, "title");
                var workspaceId = GetString(p, "workspaceId");
                var cwd = GetString(p, "workingDirectory") ?? _host.ProjectRoot;
                var kind = GetString(p, "commandKind") ?? "shell";
                var cols = (short)(GetInt(p, "cols") ?? 120);
                var rows = (short)(GetInt(p, "rows") ?? 30);
                return _host.CreateTerminalSession(title, workspaceId, cwd, kind, cols, rows);
            }

            case "terminal.write":
                _host.Terminals.WriteInput(RequireString(p, "sessionId"), GetString(p, "data") ?? string.Empty);
                return null;

            case "terminal.resize":
                _host.Terminals.Resize(
                    RequireString(p, "sessionId"),
                    (short)(GetInt(p, "cols") ?? 120),
                    (short)(GetInt(p, "rows") ?? 30));
                return null;

            case "terminal.close":
                _host.Terminals.CloseSession(RequireString(p, "sessionId"));
                return new { terminals = _host.Terminals.ListSessions() };

            case "design.setVisible":
                return await _host.SetDesignPreviewVisibleAsync(GetBool(p, "visible") ?? false, GetString(p, "url"));

            case "design.navigate":
                return await _host.NavigateDesignPreviewAsync(RequireString(p, "url"));

            case "design.setInspect":
                return await _host.SetDesignInspectModeAsync(GetBool(p, "enabled") ?? false);

            case "design.captureViewport":
                return await _host.CaptureDesignViewportAsync();

            default:
                throw new InvalidOperationException($"未知的 UI 请求方法：{method}");
        }
    }

    private static UniversalAgentUpsertRequest ParseUniversalAgentRequest(JsonElement p)
    {
        var kindStr = GetString(p, "agentKind") ?? "DirectApi";
        if (!Enum.TryParse<UniversalAgentKind>(kindStr, ignoreCase: true, out var agentKind))
            agentKind = UniversalAgentKind.DirectApi;

        var protoStr = GetString(p, "protocol");
        var presetId = GetString(p, "presetId") ?? (agentKind == UniversalAgentKind.DirectApi ? "gemini" : "codex-cli");
        if (!Enum.TryParse<ApiProtocolKind>(protoStr, ignoreCase: true, out var protocol))
        {
            protocol = agentKind == UniversalAgentKind.DirectApi
                ? UniversalAgentCatalog.GetApiPreset(presetId).Protocol
                : ApiProtocolKind.OpenAiCompatible;
        }

        long? budget = null;
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("tokenBudgetLimit", out var bp) && bp.ValueKind == JsonValueKind.Number && bp.TryGetInt64(out var bv) && bv > 0)
            budget = bv;

        return new UniversalAgentUpsertRequest(
            Id: GetString(p, "id"),
            DisplayName: GetString(p, "displayName") ?? "新智能体",
            AgentKind: agentKind,
            PresetId: presetId,
            Protocol: protocol,
            BaseUrl: GetString(p, "baseUrl"),
            Model: GetString(p, "model"),
            ApiKeyPlaintext: GetString(p, "apiKeyPlaintext"),
            ClearApiKey: GetBool(p, "clearApiKey") ?? false,
            CustomCliExecutable: GetString(p, "customCliExecutable"),
            CustomCliArgsTemplate: GetString(p, "customCliArgsTemplate"),
            TokenBudgetLimit: budget,
            CustomHomeDirectory: GetString(p, "customHomeDirectory"));
    }

    private static IReadOnlyList<GroupChatAgent> ParseAgents(JsonElement p)
    {
        if (!p.TryGetProperty("agents", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("请提供聊天身份列表。");
        var list = new List<GroupChatAgent>();
        foreach (var item in arr.EnumerateArray())
        {
            var id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id) || !Guid.TryParseExact(id, "N", out _))
                id = Guid.NewGuid().ToString("N");
            var alias = RequireString(item, "alias").Trim();
            var workspaceId = RequireString(item, "workspaceId").Trim();
            var model = GetString(item, "model");
            var effort = GetString(item, "reasoningEffort");
            var tier = GetString(item, "serviceTier");
            list.Add(new GroupChatAgent(
                id,
                alias,
                workspaceId,
                string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
                string.IsNullOrWhiteSpace(effort) ? null : effort.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(tier) ? null : tier.Trim().ToLowerInvariant()));
        }
        return list;
    }

    private static IReadOnlyList<DiffLineComment> ParseDiffComments(JsonElement p)
    {
        if (!p.TryGetProperty("comments", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<DiffLineComment>();
        foreach (var item in arr.EnumerateArray())
        {
            list.Add(new DiffLineComment(
                RequireString(item, "filePath"),
                GetInt(item, "lineNumber") ?? 1,
                GetString(item, "side") ?? "new",
                GetString(item, "codeSnippet") ?? string.Empty,
                RequireString(item, "comment")));
        }
        return list;
    }

    private static IReadOnlyList<ChatImageAttachment>? ParseAttachments(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("attachments", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<ChatImageAttachment>();
        foreach (var item in arr.EnumerateArray())
        {
            if (list.Count >= 8) break;
            var mime = GetString(item, "mimeType") ?? "image/png";
            var b64 = GetString(item, "base64Data");
            if (string.IsNullOrWhiteSpace(b64) && GetString(item, "dataUrl") is { } dataUrl)
            {
                var commaIdx = dataUrl.IndexOf(',');
                if (commaIdx > 0 && dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var header = dataUrl[5..commaIdx];
                    var semiIdx = header.IndexOf(';');
                    if (semiIdx > 0) mime = header[..semiIdx];
                    b64 = dataUrl[(commaIdx + 1)..];
                }
            }
            if (string.IsNullOrWhiteSpace(b64) || b64.Length > 16_000_000) continue;
            list.Add(new ChatImageAttachment(mime, b64.Trim(), GetString(item, "label")));
        }
        return list.Count > 0 ? list : null;
    }

    private static IReadOnlyList<ArenaContestantSpec> ParseArenaSpecs(JsonElement p)
    {
        if (!p.TryGetProperty("contestants", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("请选择参赛身份。");
        var list = new List<ArenaContestantSpec>();
        foreach (var item in arr.EnumerateArray())
        {
            list.Add(new ArenaContestantSpec(
                GetString(item, "label") ?? string.Empty,
                RequireString(item, "workspaceId"),
                GetString(item, "model"),
                GetString(item, "reasoningEffort"),
                GetString(item, "serviceTier")));
        }
        return list;
    }

    private static string RequireString(JsonElement element, string name) =>
        GetString(element, name) is { Length: > 0 } val
            ? val
            : throw new InvalidOperationException($"缺少必要参数 {name}。");

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool? GetBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v)
            ? v
            : null;

    private static uint RequireUInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetUInt32(out var v)
            ? v
            : throw new InvalidOperationException($"参数 {name} 无效。");
}
