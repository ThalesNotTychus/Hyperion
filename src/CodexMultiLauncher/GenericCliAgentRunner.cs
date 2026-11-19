using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexMultiLauncher;

/// <summary>
/// Headless runner (`CreateNoWindow = true`) for mainstream lightweight CLI coding agents
/// (`codex`, `claude`, `gemini`, `agy`, `opencode`, `aider`, `qwen`, `goose`, and custom CLI templates)
/// without requiring heavy official desktop GUI clients.
/// </summary>
public sealed partial class GenericCliAgentRunner : IGroupChatRunner
{
    private readonly AgentProviderStore _store;
    private readonly Func<IReadOnlyList<CodexWorkspace>>? _desktopWorkspaces;
    private readonly Action<string, long, long>? _onTokenUsage;
    private readonly AntigravityAgentRunner _antigravityBridge;

    public GenericCliAgentRunner(
        AgentProviderStore store,
        Func<IReadOnlyList<CodexWorkspace>>? desktopWorkspaces = null,
        Action<string, long, long>? onTokenUsage = null)
    {
        _store = store;
        _desktopWorkspaces = desktopWorkspaces;
        _onTokenUsage = onTokenUsage;
        _antigravityBridge = new AntigravityAgentRunner(onTokenUsage: onTokenUsage);
    }

    public static (ProcessStartInfo StartInfo, string? StdinPayload) BuildCliStartInfo(
        UniversalAgentRecord record,
        AgentProviderStore store,
        GroupChatRun request,
        IEnumerable<CodexWorkspace>? desktopWorkspaces = null)
    {
        if (!Path.IsPathFullyQualified(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
            throw new InvalidOperationException("项目工作目录不存在。");

        var preset = UniversalAgentCatalog.GetCliPreset(record.PresetId);
        var model = !string.IsNullOrWhiteSpace(request.Agent.Model) ? request.Agent.Model! : record.Model;
        var writable = request.SandboxMode == ChatSandboxMode.WorkspaceWrite;

        string executable;
        if (!string.IsNullOrWhiteSpace(record.CustomCliExecutable))
        {
            executable = record.CustomCliExecutable!;
        }
        else
        {
            executable = UniversalAgentCatalog.TryFindCliBinary(preset.Id, preset.BinaryName, desktopWorkspaces)
                ?? throw new InvalidOperationException(
                    $"本机尚未检测到「{preset.Badge}」命令行引擎（`{preset.BinaryName}`）。无需下载笨重桌面客户端，点击算力池或添加弹窗中的「⬇ 一键终端安装」即可自动执行 `{preset.InstallCommand}`。");
        }

        var isBatchWrapper = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo(isBatchWrapper ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") : executable)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (isBatchWrapper)
        {
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(executable);
        }

        string? stdinPayload = null;

        if (!string.IsNullOrWhiteSpace(record.CustomCliArgsTemplate) || preset.Id == "custom-cli")
        {
            var template = string.IsNullOrWhiteSpace(record.CustomCliArgsTemplate) ? "-p {prompt}" : record.CustomCliArgsTemplate!;
            var promptInArgs = template.Contains("{prompt}", StringComparison.OrdinalIgnoreCase);
            foreach (var tokenArg in SplitTemplateArguments(template))
            {
                var expanded = tokenArg
                    .Replace("{prompt}", request.Prompt, StringComparison.OrdinalIgnoreCase)
                    .Replace("{model}", model ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("{cwd}", request.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
                    .Replace("{sandbox}", writable ? "workspace-write" : "read-only", StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(expanded))
                    psi.ArgumentList.Add(expanded);
            }
            if (!promptInArgs) stdinPayload = request.Prompt;
        }
        else
        {
            switch (preset.Id)
            {
                case "codex-cli":
                    foreach (var a in new[] { "exec", "--skip-git-repo-check", "--json", "--color", "never", "-s", writable ? "workspace-write" : "read-only", "-C", request.WorkingDirectory })
                        psi.ArgumentList.Add(a);
                    if (!string.IsNullOrWhiteSpace(model)) { psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(model); }
                    if (!string.IsNullOrWhiteSpace(request.Agent.ReasoningEffort))
                    {
                        psi.ArgumentList.Add("-c");
                        psi.ArgumentList.Add($"model_reasoning_effort=\"{request.Agent.ReasoningEffort.Trim()}\"");
                    }
                    if (!string.IsNullOrWhiteSpace(request.Agent.ServiceTier) &&
                        !string.Equals(request.Agent.ServiceTier, "default", StringComparison.OrdinalIgnoreCase))
                    {
                        psi.ArgumentList.Add("-c");
                        psi.ArgumentList.Add($"service_tier=\"{request.Agent.ServiceTier.Trim()}\"");
                    }
                    psi.ArgumentList.Add("-");
                    stdinPayload = request.Prompt;
                    break;

                case "claude-code":
                    foreach (var a in new[] { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--permission-mode", writable ? "acceptEdits" : "plan" })
                        psi.ArgumentList.Add(a);
                    if (!string.IsNullOrWhiteSpace(model)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model); }
                    if (ClaudeCliRunner.NormalizeClaudeEffort(request.Agent.ReasoningEffort) is { } claudeEff)
                    {
                        psi.ArgumentList.Add("--effort");
                        psi.ArgumentList.Add(claudeEff);
                    }
                    if (string.Equals(request.Agent.ServiceTier, "fast", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(request.Agent.ServiceTier, "priority", StringComparison.OrdinalIgnoreCase))
                    {
                        psi.ArgumentList.Add("--settings");
                        psi.ArgumentList.Add("{\"fastMode\":true}");
                    }
                    stdinPayload = request.Prompt;
                    break;

                case "gemini-cli":
                    if (!string.IsNullOrWhiteSpace(model)) { psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(model); }
                    if (writable) psi.ArgumentList.Add("--yolo");
                    psi.ArgumentList.Add("-p");
                    psi.ArgumentList.Add(request.Prompt);
                    break;

                case "antigravity-cli":
                    psi.ArgumentList.Add("-p");
                    psi.ArgumentList.Add(request.Prompt);
                    psi.ArgumentList.Add("--output-format");
                    psi.ArgumentList.Add("stream-json");
                    var resolvedAgyModel = ModelCatalogService.ResolveAntigravityModelArgument(model, request.Agent.ReasoningEffort, request.Agent.ServiceTier);
                    if (!string.IsNullOrWhiteSpace(resolvedAgyModel)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(resolvedAgyModel); }
                    if (writable) psi.ArgumentList.Add("--trust");
                    break;

                case "opencode":
                    psi.ArgumentList.Add("run");
                    psi.ArgumentList.Add("--format");
                    psi.ArgumentList.Add("json");
                    if (!string.IsNullOrWhiteSpace(model)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model); }
                    psi.ArgumentList.Add(request.Prompt);
                    break;

                case "aider":
                    foreach (var a in new[] { "--yes-always", "--no-pretty", "--no-auto-commits" })
                        psi.ArgumentList.Add(a);
                    if (!writable) psi.ArgumentList.Add("--chat-mode");
                    if (!writable) psi.ArgumentList.Add("ask");
                    if (!string.IsNullOrWhiteSpace(model)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model); }
                    psi.ArgumentList.Add("--message");
                    psi.ArgumentList.Add(request.Prompt);
                    break;

                case "qwen-code":
                    if (!string.IsNullOrWhiteSpace(model)) { psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(model); }
                    if (writable) psi.ArgumentList.Add("--yolo");
                    psi.ArgumentList.Add("-p");
                    psi.ArgumentList.Add(request.Prompt);
                    break;

                case "goose":
                    psi.ArgumentList.Add("run");
                    psi.ArgumentList.Add("--text");
                    psi.ArgumentList.Add(request.Prompt);
                    break;

                default:
                    psi.ArgumentList.Add("-p");
                    psi.ArgumentList.Add(request.Prompt);
                    break;
            }
        }

        foreach (var (key, value) in store.CreateIsolatedEnvironment(record))
            psi.Environment[key] = value;

        return (psi, stdinPayload);
    }

    public async Task<string> RunAsync(GroupChatRun request, IProgress<GroupChatUpdate> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Workspace.Client != ClientKind.MarketCli)
            throw new InvalidOperationException("此身份必须绑定轻量级 CLI 智能体。");

        var record = _store.FindById(request.Workspace.ProviderId ?? request.Workspace.Id)
            ?? throw new InvalidOperationException("找不到此 CLI 智能体的配置。");

        var home = _store.ResolveHomeDirectory(record);

        // Route official Antigravity CLI accounts strictly through standard `agy acp` (Agent Client Protocol)
        if (string.IsNullOrWhiteSpace(record.CustomCliExecutable) && AntigravityAgentRunner.IsAntigravityBackedCliRecord(record, home))
        {
            return await _antigravityBridge.RunWithCustomHomeAsync(home, request, progress, token).ConfigureAwait(false);
        }

        var (startInfo, stdinPayload) = BuildCliStartInfo(record, _store, request, _desktopWorkspaces?.Invoke());

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

        using var process = new Process { StartInfo = startInfo };
        var steps = new List<ChatExecutionStep>
        {
            new("cli-boot", "system", $"后台无窗启动 {record.DisplayName} ({record.PresetId})", "completed"),
        };
        progress.Report(new GroupChatUpdate(string.Empty, "已启动无头 CLI", steps.ToArray()));

        if (!process.Start())
            throw new InvalidOperationException($"无法启动 CLI 智能体进程：{startInfo.FileName}");

        try
        {
            if (stdinPayload is not null)
            {
                await process.StandardInput.WriteAsync(stdinPayload.AsMemory(), linked.Token).ConfigureAwait(false);
            }
            process.StandardInput.Close();

            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
            var answerBuilder = new StringBuilder();
            var thinkingBuilder = new StringBuilder();
            var rawFallback = new StringBuilder();
            var stepSeq = 0;

            while (await process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length > 2_000_000) continue;
                var trimmed = line.Trim();
                if (trimmed.StartsWith('{') && trimmed.EndsWith('}') && TryConsumeJsonEvent(trimmed, answerBuilder, thinkingBuilder, steps, ref stepSeq, (inTok, outTok) => _onTokenUsage?.Invoke(request.Workspace.Id, inTok, outTok)))
                {
                    var liveThink = thinkingBuilder.Length > 0 ? thinkingBuilder.ToString() : null;
                    progress.Report(new GroupChatUpdate(answerBuilder.ToString(), answerBuilder.Length > 0 ? "正在生成…" : "正在思考…", steps.ToArray(), liveThink));
                    continue;
                }

                var clean = StripAnsi(line);
                rawFallback.AppendLine(clean);
                if (answerBuilder.Length == 0)
                {
                    progress.Report(new GroupChatUpdate(rawFallback.ToString().TrimEnd(), "正在输出…", steps.ToArray(), thinkingBuilder.Length > 0 ? thinkingBuilder.ToString() : null));
                }
            }

            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var finalText = (answerBuilder.Length > 0 ? answerBuilder.ToString() : rawFallback.ToString()).Trim();
            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(finalText))
            {
                var stderr = StripAnsi(await stderrTask.ConfigureAwait(false)).Trim();
                var preset = UniversalAgentCatalog.GetCliPreset(record.PresetId);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                    ? $"CLI 智能体退出码 {process.ExitCode}。提示：{preset.LoginHint}"
                    : $"CLI 智能体返回错误：{stderr[..Math.Min(stderr.Length, 280)]}");
            }

            if (string.IsNullOrWhiteSpace(finalText))
                finalText = "（CLI 智能体已完成执行，无文本输出。）";

            progress.Report(new GroupChatUpdate(finalText, "已完成", steps.ToArray(), thinkingBuilder.Length > 0 ? thinkingBuilder.ToString() : null));
            return finalText;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static bool TryConsumeJsonEvent(
        string jsonLine,
        StringBuilder answer,
        StringBuilder thinking,
        List<ChatExecutionStep> steps,
        ref int stepSeq,
        Action<long, long> onUsage)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var tp) || tp.ValueKind != JsonValueKind.String) return false;
            var type = tp.GetString() ?? string.Empty;

            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                var inTok = u.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var iv) ? iv : 0L;
                var outTok = u.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var ov) ? ov : 0L;
                if (inTok > 0 || outTok > 0) onUsage(inTok, outTok);
            }

            if (type == "stream_event" && root.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.Object)
            {
                var evType = ev.TryGetProperty("type", out var etp) ? etp.GetString() : null;
                if (evType == "content_block_delta" && ev.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    var dt = delta.TryGetProperty("type", out var dtp) ? dtp.GetString() : null;
                    if (dt == "thinking_delta" && delta.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.String)
                        thinking.Append(th.GetString());
                    else if (dt == "text_delta" && delta.TryGetProperty("text", out var td) && td.ValueKind == JsonValueKind.String)
                        answer.Append(td.GetString());
                }
                return true;
            }

            // Claude / AGY stream-json result
            if (type == "result" && root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String)
            {
                var text = res.GetString();
                if (!string.IsNullOrWhiteSpace(text)) { answer.Clear(); answer.Append(text); }
                return true;
            }

            // Claude assistant message
            if (type == "assistant" && root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    var bt = block.TryGetProperty("type", out var btp) ? btp.GetString() : null;
                    if (bt == "text" && block.TryGetProperty("text", out var txt))
                    {
                        answer.Clear();
                        answer.Append(txt.GetString());
                    }
                    else if (bt == "thinking" && block.TryGetProperty("thinking", out var thTxt) && thTxt.ValueKind == JsonValueKind.String && thinking.Length == 0)
                    {
                        thinking.Append(thTxt.GetString());
                    }
                    else if (bt == "tool_use")
                    {
                        steps.Add(ClaudeCliRunner.BuildClaudeToolStep($"cli-tool-{++stepSeq}", block));
                    }
                }
                return true;
            }

            // Codex exec --json item.started / item.updated / item.completed
            if (type is "item.started" or "item.updated" or "item.completed" && root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
            {
                var itemType = item.TryGetProperty("type", out var itp) ? itp.GetString() : null;
                var st = type == "item.completed" ? "completed" : "running";
                var itemId = item.TryGetProperty("id", out var idP) && idP.ValueKind == JsonValueKind.String ? idP.GetString() ?? $"cli-{++stepSeq}" : $"cli-{++stepSeq}";
                if (itemType == "agent_message" && item.TryGetProperty("text", out var txt))
                {
                    answer.Clear();
                    answer.Append(txt.GetString());
                }
                else if (itemType == "reasoning" && item.TryGetProperty("text", out var rtxt) && rtxt.ValueKind == JsonValueKind.String)
                {
                    thinking.Clear();
                    thinking.Append(rtxt.GetString());
                }
                else if (itemType == "command_execution")
                {
                    var cmd = item.TryGetProperty("command", out var cp) && cp.ValueKind == JsonValueKind.String ? cp.GetString() ?? "command" : "command";
                    var idx = steps.FindIndex(x => x.Id == itemId);
                    var step = new ChatExecutionStep(itemId, "command", cmd, st, cmd, null, cmd);
                    if (idx >= 0) steps[idx] = step; else steps.Add(step);
                }
                return true;
            }

            // OpenCode json event
            if (type == "text" && root.TryGetProperty("part", out var part) && part.TryGetProperty("text", out var ptxt))
            {
                answer.Append(ptxt.GetString());
                return true;
            }
            if (type == "reasoning" && root.TryGetProperty("part", out var rpart) && rpart.TryGetProperty("text", out var rptxt))
            {
                thinking.Append(rptxt.GetString());
                return true;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> SplitTemplateArguments(string template)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        foreach (var ch in template)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else current.Append(ch);
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static string StripAnsi(string text) => string.IsNullOrEmpty(text) ? string.Empty : AnsiRegex().Replace(text, string.Empty);

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiRegex();
}
