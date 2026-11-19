using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexMultiLauncher;

/// <summary>One owned Codex exec process per addressed identity, with no credential reads or copies.</summary>
public sealed class CodexGroupChatRunner : IGroupChatRunner
{
    private readonly Func<GroupChatRun, ProcessStartInfo> _createStart;
    private readonly Action<string, long, long>? _onTokenUsage;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _threadIds = new(StringComparer.Ordinal);

    public CodexGroupChatRunner(Action<string, long, long>? onTokenUsage = null)
        : this(request => CreateStartInfo(CodexExecutableLocator.FindCurrentCandidateOrThrow(), request), onTokenUsage)
    {
    }

    internal CodexGroupChatRunner(Func<GroupChatRun, ProcessStartInfo> createStart, Action<string, long, long>? onTokenUsage = null)
    {
        _createStart = createStart;
        _onTokenUsage = onTokenUsage;
    }

    public static ProcessStartInfo CreateStartInfo(string executable, GroupChatRun request)
    {
        if (request.Workspace.Client != ClientKind.Codex || !request.Workspace.CanReadUsage)
            throw new InvalidOperationException("群聊身份目前需要绑定已验证的 Codex 账号。");
        var home = Path.GetFullPath(request.Workspace.DataPaths.RequireCodexHome());
        ProfilePathSafety.ThrowIfReparsePath(home);
        if (!Path.IsPathFullyQualified(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
            throw new InvalidOperationException("项目目录不存在，请打开群聊设置重新选择。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false, true), StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8,
        };
        var sandbox = request.SandboxMode == ChatSandboxMode.WorkspaceWrite ? "workspace-write" : "read-only";
        foreach (var argument in new[] { "-a", "never", "exec", "--json", "--color", "never", "--sandbox", sandbox, "--skip-git-repo-check", "--ephemeral", "--disable", "plugins" })
            start.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(request.Agent.Model)) { start.ArgumentList.Add("--model"); start.ArgumentList.Add(request.Agent.Model); }
        if (!string.IsNullOrWhiteSpace(request.Agent.ReasoningEffort))
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"model_reasoning_effort=\"{SanitizeTomlToken(request.Agent.ReasoningEffort)}\"");
        }
        if (!string.IsNullOrWhiteSpace(request.Agent.ServiceTier) &&
            !string.Equals(request.Agent.ServiceTier, "default", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Agent.ServiceTier, "standard", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"service_tier=\"{SanitizeTomlToken(request.Agent.ServiceTier)}\"");
        }
        start.ArgumentList.Add("-");
        start.Environment["CODEX_HOME"] = home; start.Environment["CODEX_SQLITE_HOME"] = home;
        // The selected account must not be replaced by an API key inherited from the launching shell.
        foreach (var key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL", "OPENAI_ORG_ID", "OPENAI_PROJECT_ID" }) start.Environment.Remove(key);
        return start;
    }

    public async Task<string> RunAsync(GroupChatRun request, IProgress<GroupChatUpdate> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var process = new Process { StartInfo = _createStart(request) };
        var started = false;
        Task? output = null, diagnostics = null;
        var events = new CodexGroupChatEvents();
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动群聊回复进程。");
            started = true;
            diagnostics = DrainAsync(process.StandardError);
            output = ReadEventsAsync(process.StandardOutput, events, progress, linked.Token);
            await process.StandardInput.WriteAsync(request.Prompt.AsMemory(), linked.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await output.ConfigureAwait(false);
            output = DrainAsync(process.StandardOutput);
            using var exit = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            exit.CancelAfter(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(exit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested)
            { StopOwned(process); }
            linked.Token.ThrowIfCancellationRequested();
            if (!events.Completed || events.Failed || string.IsNullOrWhiteSpace(events.Text))
            {
                var detail = events.FailureReason;
                if (!string.IsNullOrWhiteSpace(detail) && AccountPoolService.IsQuotaOrRateLimitFailure(detail))
                    throw new InvalidOperationException($"触发配额或限流限制（429）：{detail}");
                throw new InvalidOperationException("Codex 未能完成回复。请在“账号与客户端”中确认此账号已登录且额度可用，再重试。");
            }
            if (events.InputTokens > 0 || events.OutputTokens > 0)
                _onTokenUsage?.Invoke(request.Workspace.Id, events.InputTokens, events.OutputTokens);
            return events.Text;
        }
        catch (DecoderFallbackException) { throw new InvalidOperationException("群聊回复不是有效的 UTF-8，已停止本次请求。"); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        { throw new TimeoutException("本次回复超过 20 分钟，已停止。可以重新发送消息。"); }
        finally
        {
            if (started)
            {
                StopOwned(process);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
                try { await Task.WhenAll(output ?? Task.CompletedTask, diagnostics ?? Task.CompletedTask).WaitAsync(cleanup.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or JsonException or InvalidOperationException or DecoderFallbackException) { }
            }
        }
    }

    private static async Task ReadEventsAsync(StreamReader reader, CodexGroupChatEvents events, IProgress<GroupChatUpdate> progress, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (line.Length > 2_000_000) throw new InvalidOperationException("群聊响应事件过大，已停止本次请求。");
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var update = events.Accept(line);
                if (update is not null) progress.Report(update);
                if (events.Completed || events.Failed) return;
            }
            catch (JsonException) { throw new InvalidOperationException("Codex 返回了无效的群聊响应。"); }
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) > 0) { }
    }

    private static string SanitizeTomlToken(string raw) =>
        new(raw.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

    private static void StopOwned(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
    }
}

internal sealed class CodexGroupChatEvents
{
    private readonly Dictionary<string, string> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _thoughts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatExecutionStep> _steps = new(StringComparer.Ordinal);
    public bool Completed { get; private set; }
    public bool Failed { get; private set; }
    public string? FailureReason { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public string Text => string.Join("\n\n", _messages.Values);
    public string? Thinking => _thoughts.Count > 0 ? string.Join("\n\n", _thoughts.Values) : null;
    public IReadOnlyList<ChatExecutionStep> Steps => _steps.Values.ToArray();

    public GroupChatUpdate? Accept(string line)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) throw new JsonException();
        var eventType = type.GetString();
        switch (eventType)
        {
            case "turn.completed":
                Completed = true;
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var iv)) InputTokens = iv;
                    if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var ov)) OutputTokens = ov;
                }
                return new(Text, "已完成", Steps, Thinking);
            case "turn.failed":
                Failed = true;
                if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                    && err.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                {
                    var raw = msg.GetString();
                    if (AccountPoolService.IsQuotaOrRateLimitFailure(raw))
                        FailureReason = "账户配额或速率达到上限";
                }
                return null;
            case "thread.started":
            case "turn.started":
                return new(Text, "正在连接 Codex 并开始推理…", Steps, Thinking);
            case "item.started":
            case "item.updated":
            case "item.completed":
                if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("type", out var itemType) || itemType.ValueKind != JsonValueKind.String)
                    return null;
                var kind = itemType.GetString();
                var itemId = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString() ?? "item"
                    : $"item-{_steps.Count + 1}";
                var stepStatus = eventType == "item.completed" ? "completed" : "running";

                if (kind == "agent_message" && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    _messages[itemId] = text.GetString()!;
                    if (Text.Length > 1_000_000) throw new InvalidOperationException("回复超过可保存长度，已停止。");
                    return new(Text, "正在回复…", Steps, Thinking);
                }

                if (kind == "reasoning")
                {
                    var extractedThought = ExtractReasoningText(item);
                    if (!string.IsNullOrWhiteSpace(extractedThought))
                        _thoughts[itemId] = extractedThought!;
                    _steps[itemId] = new ChatExecutionStep(itemId, "thinking", "推理分析任务路径", stepStatus);
                    return new(Text, "正在深度思考…", Steps, Thinking);
                }

                if (kind == "command_execution")
                {
                    var rawCmd = ExtractRawShellCommand(item);
                    var exitCode = item.TryGetProperty("exit_code", out var ec) && ec.TryGetInt32(out var ecv) ? ecv : (int?)null;
                    if (eventType == "item.completed" && exitCode is not null and not 0)
                        stepStatus = "failed";
                    var meta = exitCode is not null and not 0 ? $"exit {exitCode}" : null;
                    _steps[itemId] = new ChatExecutionStep(itemId, "command", rawCmd, stepStatus, rawCmd, meta, rawCmd);
                    return new(Text, $"执行命令：{rawCmd}", Steps, Thinking);
                }

                if (kind == "file_change")
                {
                    RecordFileChangeSteps(itemId, item, stepStatus);
                    return new(Text, "正在修改项目文件…", Steps, Thinking);
                }

                var fallbackTitle = kind == "mcp_tool_call" ? "调用扩展工具" : $"执行步骤 ({kind})";
                _steps[itemId] = new ChatExecutionStep(itemId, kind ?? "tool", fallbackTitle, stepStatus);
                return new(Text, "正在处理…", Steps, Thinking);
            default:
                return null;
        }
    }

    private static string? ExtractReasoningText(JsonElement item)
    {
        if (item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
            return t.GetString()!.Trim();
        if (item.TryGetProperty("summary", out var sum) && sum.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var el in sum.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                    parts.Add(el.GetString()!.Trim());
                else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("text", out var st) && st.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(st.GetString()))
                    parts.Add(st.GetString()!.Trim());
            }
            if (parts.Count > 0) return string.Join("\n", parts);
        }
        return null;
    }

    private static string ExtractRawShellCommand(JsonElement item)
    {
        if (item.TryGetProperty("command", out var cmd))
        {
            if (cmd.ValueKind == JsonValueKind.Array)
            {
                var arr = cmd.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
                if (arr.Length >= 3 && (arr[0].Contains("powershell", StringComparison.OrdinalIgnoreCase) || arr[0].Contains("pwsh", StringComparison.OrdinalIgnoreCase) || arr[0].Contains("cmd", StringComparison.OrdinalIgnoreCase) || arr[0].EndsWith("sh", StringComparison.OrdinalIgnoreCase)))
                {
                    return arr[^1].Replace('\r', ' ').Replace('\n', ' ').Trim();
                }
                if (arr.Length > 0)
                {
                    return string.Join(' ', arr).Replace('\r', ' ').Replace('\n', ' ').Trim();
                }
            }
            else if (cmd.ValueKind == JsonValueKind.String)
            {
                var text = cmd.GetString() ?? string.Empty;
                var compact = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                var cmdIdx = compact.IndexOf("-Command ", StringComparison.OrdinalIgnoreCase);
                if (cmdIdx >= 0)
                    compact = compact[(cmdIdx + 9)..].Trim().Trim('"', '\'');
                return string.IsNullOrWhiteSpace(compact) ? "command" : compact;
            }
        }
        return "command";
    }

    private void RecordFileChangeSteps(string itemId, JsonElement item, string stepStatus)
    {
        if (item.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array && changes.GetArrayLength() > 0)
        {
            var idx = 0;
            foreach (var ch in changes.EnumerateArray())
            {
                idx++;
                var path = ch.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "file" : "file";
                var baseName = Path.GetFileName(path);
                var changeKind = ch.TryGetProperty("kind", out var ck) && ck.ValueKind == JsonValueKind.String ? ck.GetString() : "update";
                var stepKind = string.Equals(changeKind, "add", StringComparison.OrdinalIgnoreCase) ? "write" : "edit";
                var subId = $"{itemId}-{idx}";
                _steps[subId] = new ChatExecutionStep(subId, stepKind, $"{(stepKind == "write" ? "Created" : "Edited")} {baseName}", stepStatus, baseName, null, path);
            }
            return;
        }
        _steps[itemId] = new ChatExecutionStep(itemId, "edit", "修改项目文件", stepStatus);
    }
}
