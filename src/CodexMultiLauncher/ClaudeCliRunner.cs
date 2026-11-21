using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexMultiLauncher;

public sealed record ClaudeAuthInspection(
    bool CliAvailable,
    string? CliVersion,
    bool LoggedIn,
    string? AuthMethod,
    string? Email,
    string? SubscriptionType,
    string StatusSummary);

/// <summary>
/// Discovers bundled Claude Code CLI (`claude.exe`) inside Claude Desktop's `electron-user-data\claude-code`
/// or global installations, inspects account auth status headlessly, and runs multi-turn stream-json tasks
/// with zero desktop windows (`CreateNoWindow = true`).
/// </summary>
public sealed class ClaudeCliRunner : IGroupChatRunner
{
    private readonly Func<GroupChatRun, ProcessStartInfo> _createStart;
    private readonly ConcurrentDictionary<string, string> _sessionIds = new(StringComparer.Ordinal);
    private readonly Action<string, long, long>? _onTokenUsage;

    public ClaudeCliRunner(Action<string, long, long>? onTokenUsage = null)
        : this(request => CreateStartInfo(FindClaudeCliExecutableOrThrow(request.Workspace), request), onTokenUsage)
    {
    }

    internal ClaudeCliRunner(Func<GroupChatRun, ProcessStartInfo> createStart, Action<string, long, long>? onTokenUsage = null)
    {
        _createStart = createStart;
        _onTokenUsage = onTokenUsage;
    }

    private static (string? Path, long ExpiresTick) _cachedGlobalCli;

    public static void InvalidateCliCache() => _cachedGlobalCli = default;

    public static string? TryFindClaudeCliExecutable(CodexWorkspace? preferredWorkspace = null, IEnumerable<CodexWorkspace>? allWorkspaces = null)
    {
        if (preferredWorkspace is null)
        {
            var cached = _cachedGlobalCli;
            if (Environment.TickCount64 < cached.ExpiresTick)
                return cached.Path;
        }

        var resolved = TryFindClaudeCliExecutableCore(preferredWorkspace, allWorkspaces);
        if (preferredWorkspace is null)
            _cachedGlobalCli = (resolved, Environment.TickCount64 + 30_000);
        return resolved;
    }

    private static string? TryFindClaudeCliExecutableCore(CodexWorkspace? preferredWorkspace, IEnumerable<CodexWorkspace>? allWorkspaces)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var envLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var envRoamingAppData = Environment.GetEnvironmentVariable("APPDATA");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new List<string>();

        // 1. Check bundled claude.exe inside preferred workspace and all Claude Desktop electron-user-data folders!
        var electronRoots = new List<string>();
        if (preferredWorkspace is not null && !string.IsNullOrWhiteSpace(preferredWorkspace.ElectronUserData))
            electronRoots.Add(preferredWorkspace.ElectronUserData);
        if (allWorkspaces is not null)
        {
            foreach (var ws in allWorkspaces.Where(w => w.Client == ClientKind.Claude && !string.IsNullOrWhiteSpace(w.ElectronUserData)))
                electronRoots.Add(ws.ElectronUserData);
        }

        // Also scan sibling managed profiles under launcher root if present
        try
        {
            var projectRoot = RootLocator.FindProjectRoot();
            var profilesDir = Path.Combine(projectRoot, "data", "profiles");
            if (Directory.Exists(profilesDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(profilesDir))
                    electronRoots.Add(Path.Combine(dir, "electron-user-data"));
            }
        }
        catch { }

        foreach (var orig in ClaudeInstallation.GetOriginalDataDirectories())
            electronRoots.Add(orig);
        if (!string.IsNullOrWhiteSpace(envLocalAppData))
            electronRoots.Add(Path.Combine(envLocalAppData, "Claude-Data", ClaudeInstallation.PackageFamilyName.ToLowerInvariant()));
        if (!string.IsNullOrWhiteSpace(envRoamingAppData))
            electronRoots.Add(Path.Combine(envRoamingAppData, "Claude"));

        foreach (var root in electronRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bundledDir = Path.Combine(root, "claude-code");
            if (!Directory.Exists(bundledDir)) continue;
            try
            {
                var found = Directory.EnumerateFiles(bundledDir, "claude.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToArray();
                candidates.AddRange(found);
            }
            catch { }
        }

        // 2. Standard standalone CLI locations
        candidates.Add(Path.Combine(userProfile, ".local", "bin", "claude.exe"));
        candidates.Add(Path.Combine(localAppData, "Programs", "claude-code", "claude.exe"));
        candidates.Add(Path.Combine(localAppData, "AnthropicClaude", "claude.exe"));
        candidates.Add(Path.Combine(roamingAppData, "npm", "claude.cmd"));

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Path.IsPathFullyQualified(dir)) continue;
                candidates.Add(Path.Combine(dir, "claude.exe"));
                candidates.Add(Path.Combine(dir, "claude.cmd"));
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                ProfilePathSafety.ThrowIfReparsePath(candidate);
                return Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
            }
        }

        return null;
    }

    public static string FindClaudeCliExecutableOrThrow(CodexWorkspace? workspace = null, IEnumerable<CodexWorkspace>? allWorkspaces = null) =>
        TryFindClaudeCliExecutable(workspace, allWorkspaces)
        ?? throw new InvalidOperationException("未找到可用的 Claude Code 引擎（claude.exe）。请启动一次 Claude 桌面版完成内置引擎初始化，或安装 Claude Code CLI。");

    public static async Task<ClaudeAuthInspection> InspectWorkspaceAuthAsync(
        CodexWorkspace workspace,
        IEnumerable<CodexWorkspace>? allWorkspaces,
        CancellationToken token)
    {
        var cli = TryFindClaudeCliExecutable(workspace, allWorkspaces);
        var hasDesktopSession = HasDesktopSessionFiles(workspace.ElectronUserData);
        if (cli is null)
        {
            return new ClaudeAuthInspection(
                false, null, hasDesktopSession, hasDesktopSession ? "desktop" : null, null, null,
                hasDesktopSession ? "桌面端已初始化 · 待加载 CLI 引擎" : "未初始化 · 请登录一次");
        }

        string? version = TryExtractVersionFromPath(cli);
        try
        {
            var isCmd = cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(isCmd ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") : cli)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (isCmd) { psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/s"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(cli); }
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--json");

            if (!string.IsNullOrWhiteSpace(workspace.ClaudeHome))
            {
                Directory.CreateDirectory(workspace.ClaudeHome);
                psi.Environment["CLAUDE_CONFIG_DIR"] = Path.GetFullPath(workspace.ClaudeHome);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var stdout = await proc.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    using var doc = JsonDocument.Parse(stdout);
                    var root = doc.RootElement;
                    var loggedIn = root.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True;
                    var method = root.TryGetProperty("authMethod", out var am) && am.ValueKind == JsonValueKind.String ? am.GetString() : null;
                    var email = root.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String ? em.GetString() : null;
                    var sub = root.TryGetProperty("subscriptionType", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;

                    if (loggedIn)
                    {
                        return new ClaudeAuthInspection(
                            true, version, true, method, email, sub,
                            $"无头引擎就绪{(sub is not null ? $" · {sub}" : "")}");
                    }
                }
            }
        }
        catch { }

        return new ClaudeAuthInspection(
            true,
            version,
            hasDesktopSession,
            hasDesktopSession ? "desktop-cookie" : "none",
            null,
            null,
            hasDesktopSession ? "桌面已登录 · CLI 首次可授权同步" : "待登录 · 点击临时登录或终端 claude auth login");
    }

    private static bool HasDesktopSessionFiles(string electronUserData)
    {
        try
        {
            if (!Directory.Exists(electronUserData)) return false;
            var cookies = Path.Combine(electronUserData, "Network", "Cookies");
            var localStorage = Path.Combine(electronUserData, "Local Storage", "leveldb");
            return (File.Exists(cookies) && new FileInfo(cookies).Length > 4096) || Directory.Exists(localStorage);
        }
        catch { return false; }
    }

    private static string? TryExtractVersionFromPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var idx = Array.FindIndex(parts, p => p.Equals("claude-code", StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && idx + 1 < parts.Length && Version.TryParse(parts[idx + 1], out _) ? parts[idx + 1] : null;
    }

    public static ProcessStartInfo CreateStartInfo(string executable, GroupChatRun request, string? resumeSessionId = null)
    {
        if (request.Workspace.Client != ClientKind.Claude || !request.Workspace.IsLaunchable)
            throw new InvalidOperationException("Claude 协同身份必须绑定已验证的受管或原始 Claude 账号。");
        if (!Path.IsPathFullyQualified(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
            throw new InvalidOperationException("项目目录不存在，请重新选择工作目录。");

        var isCmdWrapper = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(isCmdWrapper ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false, true),
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (isCmdWrapper)
        {
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(executable);
        }

        var permissionMode = request.SandboxMode == ChatSandboxMode.WorkspaceWrite ? "acceptEdits" : "plan";
        foreach (var arg in new[] { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--permission-mode", permissionMode })
            start.ArgumentList.Add(arg);

        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            start.ArgumentList.Add("--resume");
            start.ArgumentList.Add(resumeSessionId);
        }

        if (!string.IsNullOrWhiteSpace(request.Agent.Model))
        {
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(request.Agent.Model);
        }

        var normalizedEffort = NormalizeClaudeEffort(request.Agent.ReasoningEffort);
        if (!string.IsNullOrWhiteSpace(normalizedEffort))
        {
            start.ArgumentList.Add("--effort");
            start.ArgumentList.Add(normalizedEffort);
        }

        if (string.Equals(request.Agent.ServiceTier, "fast", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Agent.ServiceTier, "priority", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("--settings");
            start.ArgumentList.Add("{\"fastMode\":true}");
        }

        if (!string.IsNullOrWhiteSpace(request.Workspace.ClaudeHome))
        {
            var claudeHome = Path.GetFullPath(request.Workspace.ClaudeHome);
            ProfilePathSafety.ThrowIfReparsePath(claudeHome);
            Directory.CreateDirectory(claudeHome);
            start.Environment["CLAUDE_CONFIG_DIR"] = claudeHome;
        }

        foreach (var key in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL" })
            start.Environment.Remove(key);

        return start;
    }

    internal static string? NormalizeClaudeEffort(string? rawEffort) => rawEffort?.Trim().ToLowerInvariant() switch
    {
        "minimal" or "low" => "low",
        "medium" => "medium",
        "high" => "high",
        "xhigh" or "max" or "ultra" => "max",
        _ => null,
    };

    public async Task<string> RunAsync(GroupChatRun request, IProgress<GroupChatUpdate> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

        var sessionKey = $"{request.WorkingDirectory}::{request.Agent.Id}::{request.Workspace.Id}";
        _sessionIds.TryGetValue(sessionKey, out var existingSession);
        var isResuming = existingSession is not null && _createStart.Target is ClaudeCliRunner;
        var startInfo = isResuming
            ? CreateStartInfo(FindClaudeCliExecutableOrThrow(request.Workspace), request, existingSession)
            : _createStart(request);

        // Avoid O(N^2) prompt bloat: when resuming a stateful Claude session, send only incremental delta!
        var effectiveInput = isResuming && !string.IsNullOrWhiteSpace(request.IncrementalPrompt)
            ? request.IncrementalPrompt!
            : request.Prompt;
        var stagedFiles = StageImageAttachmentsForCli(request.Attachments, ref effectiveInput);

        using var process = new Process { StartInfo = startInfo };
        var started = false;
        Task? stdoutTask = null;
        Task<string>? stderrTask = null;
        var builder = new StringBuilder();
        var steps = new List<ChatExecutionStep>();

        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动后台 Claude 引擎进程。");
            started = true;
            stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            stdoutTask = ReadStreamEventsAsync(
                process.StandardOutput,
                builder,
                steps,
                progress,
                sid => _sessionIds[sessionKey] = sid,
                (inTok, outTok) => _onTokenUsage?.Invoke(request.Workspace.Id, inTok, outTok),
                linked.Token);
            await process.StandardInput.WriteAsync(effectiveInput.AsMemory(), linked.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await stdoutTask.ConfigureAwait(false);

            using var exitBudget = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            exitBudget.CancelAfter(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(exitBudget.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested) { StopOwned(process); }

            linked.Token.ThrowIfCancellationRequested();
            var finalAnswer = builder.ToString().Trim();
            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(finalAnswer))
            {
                var err = stderrTask is not null ? (await stderrTask.ConfigureAwait(false)).Trim() : string.Empty;
                if (isResuming)
                {
                    // Stale resumed session ID — clear it and retry once with full context
                    _sessionIds.TryRemove(sessionKey, out _);
                    return await RunAsync(request with { IncrementalPrompt = null }, progress, token).ConfigureAwait(false);
                }
                if (err.Contains("unknown option", StringComparison.OrdinalIgnoreCase) &&
                    (request.Agent.ReasoningEffort is not null || request.Agent.ServiceTier is not null))
                {
                    var strippedReq = request with { Agent = request.Agent with { ReasoningEffort = null, ServiceTier = null } };
                    return await RunAsync(strippedReq, progress, token).ConfigureAwait(false);
                }
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(err)
                    ? $"Claude 账号“{request.Workspace.DisplayName}”尚未完成 CLI 登录授权。请点击下方“⌨ 分屏终端 → ＋ Claude TUI”运行一次 `/login` 完成绑定，之后即可全程后台无窗运行。"
                    : $"Claude 后台引擎返回：{err[..Math.Min(err.Length, 260)]}");
            }
            if (string.IsNullOrWhiteSpace(finalAnswer))
                throw new InvalidOperationException("Claude 引擎未返回有效回复文本。");
            return finalAnswer;
        }
        finally
        {
            CleanupStagedFiles(stagedFiles);
            if (started)
            {
                StopOwned(process);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false); } catch { }
            }
        }
    }

    internal static List<string> StageImageAttachmentsForCli(IReadOnlyList<ChatImageAttachment>? attachments, ref string promptText)
    {
        var staged = new List<string>();
        if (attachments is not { Count: > 0 }) return staged;
        var dir = Path.Combine(Path.GetTempPath(), "Hyperion.Captures");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder(promptText);
        sb.AppendLine().AppendLine().AppendLine("附加视觉截图文件（可直接读取查看画面）：");
        foreach (var att in attachments)
        {
            if (string.IsNullOrWhiteSpace(att.Base64Data)) continue;
            try
            {
                var ext = att.MimeType?.Contains("jpeg", StringComparison.OrdinalIgnoreCase) == true ? ".jpg" : ".png";
                var file = Path.Combine(dir, $"capture-{Guid.NewGuid():N}{ext}");
                File.WriteAllBytes(file, Convert.FromBase64String(att.Base64Data));
                staged.Add(file);
                sb.Append("- ").Append(att.Label ?? "UI Screenshot").Append(": ").AppendLine(file);
            }
            catch { }
        }
        promptText = sb.ToString();
        return staged;
    }

    internal static void CleanupStagedFiles(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    private static async Task ReadStreamEventsAsync(
        StreamReader reader,
        StringBuilder answer,
        List<ChatExecutionStep> steps,
        IProgress<GroupChatUpdate> progress,
        Action<string> onSessionId,
        Action<long, long> onUsage,
        CancellationToken token)
    {
        var stepSeq = 0;
        var thinkingBuilder = new StringBuilder();
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length > 2_000_000) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("session_id", out var sidProp) && sidProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sidProp.GetString()))
                    onSessionId(sidProp.GetString()!);

                if (!root.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString();
                if (type == "stream_event" && root.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.Object)
                {
                    var evType = ev.TryGetProperty("type", out var etp) ? etp.GetString() : null;
                    if (evType == "content_block_delta" && ev.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                    {
                        var dt = delta.TryGetProperty("type", out var dtp) ? dtp.GetString() : null;
                        if (dt == "thinking_delta" && delta.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.String)
                        {
                            thinkingBuilder.Append(th.GetString());
                            progress.Report(new GroupChatUpdate(answer.ToString(), "正在深度思考…", steps.ToArray(), thinkingBuilder.ToString()));
                        }
                        else if (dt == "text_delta" && delta.TryGetProperty("text", out var td) && td.ValueKind == JsonValueKind.String)
                        {
                            answer.Append(td.GetString());
                            progress.Report(new GroupChatUpdate(answer.ToString(), "正在流式输出…", steps.ToArray(), thinkingBuilder.Length > 0 ? thinkingBuilder.ToString() : null));
                        }
                    }
                }
                else if (type == "result")
                {
                    if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String)
                    {
                        var text = res.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            answer.Clear();
                            answer.Append(text);
                        }
                    }
                    if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    {
                        var inTok = u.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var iv) ? iv : 0L;
                        var outTok = u.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var ov) ? ov : 0L;
                        if (inTok > 0 || outTok > 0) onUsage(inTok, outTok);
                    }
                    progress.Report(new GroupChatUpdate(answer.ToString(), "已完成", steps.ToArray(), thinkingBuilder.Length > 0 ? thinkingBuilder.ToString() : null));
                }
                else if (type == "assistant" && root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (!block.TryGetProperty("type", out var bt)) continue;
                        var blockType = bt.GetString();
                        if (blockType == "text" && block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                        {
                            answer.Clear();
                            answer.Append(txt.GetString());
                        }
                        else if (blockType == "tool_use")
                        {
                            steps.Add(BuildClaudeToolStep($"claude-step-{++stepSeq}", block));
                        }
                        else if (blockType == "thinking" && block.TryGetProperty("thinking", out var thTxt) && thTxt.ValueKind == JsonValueKind.String)
                        {
                            var rawTh = thTxt.GetString();
                            if (!string.IsNullOrWhiteSpace(rawTh) && thinkingBuilder.Length == 0)
                                thinkingBuilder.Append(rawTh);
                        }
                    }
                    progress.Report(new GroupChatUpdate(answer.ToString(), "正在处理…", steps.ToArray(), thinkingBuilder.Length > 0 ? thinkingBuilder.ToString() : null));
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    internal static ChatExecutionStep BuildClaudeToolStep(string id, JsonElement block)
    {
        var toolName = block.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "tool" : "tool";
        if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
        {
            var filePath = input.TryGetProperty("file_path", out var fp) && fp.ValueKind == JsonValueKind.String ? fp.GetString() :
                           (input.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null);
            if (toolName is "Read" or "View" && !string.IsNullOrWhiteSpace(filePath))
            {
                var baseName = Path.GetFileName(filePath);
                var offset = input.TryGetProperty("offset", out var of) && of.TryGetInt32(out var ov) ? ov : (int?)null;
                var limit = input.TryGetProperty("limit", out var lm) && lm.TryGetInt32(out var lv) ? lv : (int?)null;
                var meta = offset is not null ? (limit is not null ? $"#L{offset}-{offset + limit}" : $"#L{offset}") : null;
                return new ChatExecutionStep(id, "read", $"Analyzed {baseName}", "completed", baseName, meta, filePath);
            }
            if (toolName is "Edit" or "MultiEdit" && !string.IsNullOrWhiteSpace(filePath))
            {
                var baseName = Path.GetFileName(filePath);
                return new ChatExecutionStep(id, "edit", $"Edited {baseName}", "completed", baseName, null, filePath);
            }
            if (toolName == "Write" && !string.IsNullOrWhiteSpace(filePath))
            {
                var baseName = Path.GetFileName(filePath);
                return new ChatExecutionStep(id, "write", $"Created {baseName}", "completed", baseName, null, filePath);
            }
            if (toolName == "Bash" && input.TryGetProperty("command", out var cmdP) && cmdP.ValueKind == JsonValueKind.String)
            {
                var cmd = cmdP.GetString()!.Replace('\r', ' ').Replace('\n', ' ').Trim();
                var desc = input.TryGetProperty("description", out var dp) && dp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(dp.GetString())
                    ? dp.GetString()!
                    : cmd;
                return new ChatExecutionStep(id, "command", desc, "completed", cmd, null, cmd);
            }
            if (toolName is "Grep" or "Glob" or "LS")
            {
                var pat = input.TryGetProperty("pattern", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : filePath;
                return new ChatExecutionStep(id, "search", $"Searched {pat ?? toolName}", "completed", pat ?? toolName);
            }
        }
        return new ChatExecutionStep(id, "tool", toolName, "completed", toolName);
    }

    private static void StopOwned(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
    }
}

public sealed class MultiEngineChatRunner : IGroupChatRunner
{
    private readonly IGroupChatRunner _codexRunner;
    private readonly IGroupChatRunner _claudeRunner;
    private readonly IGroupChatRunner _antigravityRunner;
    private readonly IGroupChatRunner? _directApiRunner;
    private readonly IGroupChatRunner? _marketCliRunner;

    public MultiEngineChatRunner(
        IGroupChatRunner? codexRunner = null,
        IGroupChatRunner? claudeRunner = null,
        IGroupChatRunner? antigravityRunner = null,
        IGroupChatRunner? directApiRunner = null,
        IGroupChatRunner? marketCliRunner = null)
    {
        _codexRunner = codexRunner ?? new CodexGroupChatRunner();
        _claudeRunner = claudeRunner ?? new ClaudeCliRunner();
        _antigravityRunner = antigravityRunner ?? new AntigravityAgentRunner();
        _directApiRunner = directApiRunner;
        _marketCliRunner = marketCliRunner;
    }

    public Task<string> RunAsync(GroupChatRun request, IProgress<GroupChatUpdate> progress, CancellationToken token) =>
        request.Workspace.Client switch
        {
            ClientKind.Codex => _codexRunner.RunAsync(request, progress, token),
            ClientKind.Claude => _claudeRunner.RunAsync(request, progress, token),
            ClientKind.Antigravity => _antigravityRunner.RunAsync(request, progress, token),
            ClientKind.DirectApi when _directApiRunner is not null => _directApiRunner.RunAsync(request, progress, token),
            ClientKind.MarketCli when _marketCliRunner is not null => _marketCliRunner.RunAsync(request, progress, token),
            _ => throw new InvalidOperationException($"不支持或未初始化的客户端引擎类型：{request.Workspace.ClientName}"),
        };
}
