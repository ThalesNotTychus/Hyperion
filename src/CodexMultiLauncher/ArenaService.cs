using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexMultiLauncher;

[JsonConverter(typeof(JsonStringEnumConverter<ArenaRunState>))]
public enum ArenaRunState { Pending, Running, Completed, Failed, Cancelled, Merged }

public sealed class ArenaContestant
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public string WorkspaceName { get; set; } = string.Empty;
    public ClientKind Client { get; set; } = ClientKind.Codex;
    public string? Model { get; set; }
    public string WorktreePath { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public ArenaRunState State { get; set; } = ArenaRunState.Pending;
    public string OutputText { get; set; } = string.Empty;
    public string? FailoverNotice { get; set; }
    public IReadOnlyList<ChatExecutionStep> Steps { get; set; } = [];
    public int ChangedFiles { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public double DurationSeconds { get; set; }
    public int? VerificationExitCode { get; set; }
    public string? VerificationOutput { get; set; }
}

public sealed class ArenaSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string RepositoryRoot { get; set; } = string.Empty;
    public string? VerificationCommand { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? MergedContestantId { get; set; }
    public List<ArenaContestant> Contestants { get; set; } = [];
}

public sealed record ArenaContestantSpec(string Label, string WorkspaceId, string? Model = null);

/// <summary>
/// Orchestrates side-by-side multi-account / multi-model coding races ("赛马竞技场")
/// where each contestant gets its own isolated Git worktree, automatic quota failover,
/// optional verification command, structured diff metrics, and 1-click winner merge.
/// </summary>
public sealed class ArenaService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly GitWorktreeService _git;
    private readonly Func<IReadOnlyList<CodexWorkspace>> _workspaces;
    private readonly IGroupChatRunner _runner;
    private readonly AccountPoolService? _accountPool;
    private readonly Func<UiSettings>? _settings;
    private readonly object _gate = new();
    private readonly List<ArenaSession> _sessions;
    private readonly Dictionary<string, CancellationTokenSource> _activeRuns = new(StringComparer.Ordinal);

    public ArenaService(
        string projectRoot,
        GitWorktreeService git,
        Func<IReadOnlyList<CodexWorkspace>> workspaces,
        IGroupChatRunner? runner = null,
        AccountPoolService? accountPool = null,
        Func<UiSettings>? settings = null)
    {
        _file = ProfilePathSafety.GetContainedFullPath(projectRoot, "data", "arena.json");
        _git = git;
        _workspaces = workspaces;
        _runner = runner ?? new MultiEngineChatRunner();
        _accountPool = accountPool;
        _settings = settings;
        _sessions = LoadSessions();
    }

    public event Action<ArenaSession>? SessionUpdated;

    public IReadOnlyList<ArenaSession> GetSessions()
    {
        lock (_gate) return _sessions.ToArray();
    }

    public bool IsRunning(string sessionId)
    {
        lock (_gate) return _activeRuns.ContainsKey(sessionId);
    }

    public async Task<ArenaSession> StartRaceAsync(
        string title,
        string repositoryDirectory,
        string prompt,
        IReadOnlyList<ArenaContestantSpec> contestantSpecs,
        string? verificationCommand,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("请输入赛马任务指令。");
        if (contestantSpecs is null || contestantSpecs.Count < 2 || contestantSpecs.Count > 6)
            throw new InvalidOperationException("赛马竞技场请选择 2 到 6 个参赛身份（可跨账号或跨模型）。");

        var repoStatus = await _git.InspectRepositoryAsync(repositoryDirectory, cancellationToken).ConfigureAwait(false);
        if (!repoStatus.IsGitRepository || string.IsNullOrWhiteSpace(repoStatus.RepositoryRoot))
            throw new InvalidOperationException("赛马竞技场需要选择一个已初始化提交的 Git 仓库目录，以便为每位选手自动派生隔离 Worktree。");

        var sessionTitle = string.IsNullOrWhiteSpace(title)
            ? (prompt.Trim().Length > 28 ? prompt.Trim()[..28] + "…" : prompt.Trim())
            : ProfileStore.ValidateDisplayName(title);

        var session = new ArenaSession
        {
            Title = sessionTitle,
            Prompt = prompt.Trim(),
            RepositoryRoot = repoStatus.RepositoryRoot,
            VerificationCommand = string.IsNullOrWhiteSpace(verificationCommand) ? null : verificationCommand.Trim(),
        };

        var availableWorkspaces = _workspaces();
        var index = 0;
        foreach (var spec in contestantSpecs)
        {
            index++;
            var workspace = availableWorkspaces.FirstOrDefault(w => w.Id == spec.WorkspaceId && GroupChatCoordinator.IsSupportedChatWorkspace(w))
                ?? throw new InvalidOperationException($"参赛选手“{spec.Label}”绑定的账号不可用。");
            var label = string.IsNullOrWhiteSpace(spec.Label) ? $"选手{index}" : spec.Label.Trim();
            var wt = await _git.CreateIsolatedWorktreeAsync(
                repoStatus.RepositoryRoot,
                $"arena-{session.Id[..8]}",
                $"{label}-{index}",
                baseRef: null,
                cancellationToken).ConfigureAwait(false);

            session.Contestants.Add(new ArenaContestant
            {
                Label = label,
                WorkspaceId = workspace.Id,
                WorkspaceName = workspace.DisplayName,
                Client = workspace.Client,
                Model = string.IsNullOrWhiteSpace(spec.Model) ? null : spec.Model.Trim(),
                WorktreePath = wt.WorktreePath,
                BranchName = wt.BranchName,
                State = ArenaRunState.Pending,
            });
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _sessions.Insert(0, session);
            _activeRuns[session.Id] = cts;
            SaveUnlocked();
        }
        SessionUpdated?.Invoke(session);

        _ = ExecuteRaceInBackgroundAsync(session, cts);
        return session;
    }

    private async Task ExecuteRaceInBackgroundAsync(ArenaSession session, CancellationTokenSource cts)
    {
        try
        {
            await Task.WhenAll(session.Contestants.Select(contestant => RunContestantAsync(session, contestant, cts.Token))).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _activeRuns.Remove(session.Id);
                SaveUnlocked();
            }
            cts.Dispose();
            SessionUpdated?.Invoke(session);
        }
    }

    private async Task RunContestantAsync(ArenaSession session, ArenaContestant contestant, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var allWorkspaces = _workspaces();
        var workspace = allWorkspaces.FirstOrDefault(w => w.Id == contestant.WorkspaceId)
            ?? throw new InvalidOperationException("找不到参赛账号。");

        var uiSettings = _settings?.Invoke() ?? new UiSettings();
        if (_accountPool is not null && workspace.Client is ClientKind.Codex or ClientKind.DirectApi or ClientKind.MarketCli
            && uiSettings.AutoFailoverEnabled
            && _accountPool.IsPreFlightExhausted(workspace.Id, uiSettings.FailoverThresholdPercent))
        {
            var preflight = _accountPool.SelectFailoverWorkspace(allWorkspaces, workspace.Id, null, uiSettings.FailoverThresholdPercent);
            if (preflight is not null)
            {
                _accountPool.RecordFailoverSave();
                lock (_gate)
                {
                    contestant.FailoverNotice = $"预检换号：{workspace.DisplayName} → {preflight.DisplayName}";
                    contestant.WorkspaceId = preflight.Id;
                    contestant.WorkspaceName = preflight.DisplayName;
                    contestant.Client = preflight.Client;
                }
                workspace = preflight;
            }
        }

        lock (_gate) contestant.State = ArenaRunState.Running;
        SessionUpdated?.Invoke(session);

        var agent = new GroupChatAgent(contestant.Id, "ArenaAgent", workspace.Id, contestant.Model);
        var fullPrompt = $"你正在参与多智能体并行开发竞技场（独立 Git 分支 {contestant.BranchName}）。\n"
            + "请直接在当前工作目录完成以下开发任务，确保代码完整、可编译，并简要总结你的设计思路与改动要点：\n\n"
            + session.Prompt;
        var request = new GroupChatRun(agent, workspace, contestant.WorktreePath, fullPrompt, ChatSandboxMode.WorkspaceWrite);
        var tried = new HashSet<string>(StringComparer.Ordinal) { workspace.Id };

        var progress = new Progress<GroupChatUpdate>(update =>
        {
            lock (_gate)
            {
                if (contestant.State != ArenaRunState.Running) return;
                contestant.OutputText = update.Text;
                if (update.Steps is not null) contestant.Steps = update.Steps;
                contestant.DurationSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
            }
            SessionUpdated?.Invoke(session);
        });

        while (true)
        {
            _accountPool?.RecordTurnStarted(request.Workspace.Id);
            var succeeded = false;
            try
            {
                var answer = await _runner.RunAsync(request, progress, token).ConfigureAwait(false);
                var diff = await _git.GetDiffSnapshotAsync(contestant.WorktreePath, token).ConfigureAwait(false);
                (int ExitCode, string Output)? verification = null;
                if (!string.IsNullOrWhiteSpace(session.VerificationCommand))
                {
                    verification = await RunVerificationCommandAsync(contestant.WorktreePath, session.VerificationCommand!, token).ConfigureAwait(false);
                }

                succeeded = true;
                lock (_gate)
                {
                    contestant.OutputText = answer;
                    contestant.ChangedFiles = diff.TotalFiles;
                    contestant.Additions = diff.TotalAdditions;
                    contestant.Deletions = diff.TotalDeletions;
                    contestant.DurationSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
                    if (verification is not null)
                    {
                        contestant.VerificationExitCode = verification.Value.ExitCode;
                        contestant.VerificationOutput = verification.Value.Output;
                    }
                    contestant.State = ArenaRunState.Completed;
                    SaveUnlocked();
                }
                break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                lock (_gate)
                {
                    contestant.DurationSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
                    contestant.State = ArenaRunState.Cancelled;
                    SaveUnlocked();
                }
                break;
            }
            catch (Exception exception)
            {
                if (_accountPool is not null
                    && (_settings?.Invoke().AutoFailoverEnabled ?? true)
                    && AccountPoolService.IsQuotaOrRateLimitFailure(exception.Message))
                {
                    _accountPool.MarkRateLimited(request.Workspace.Id);
                    var fallback = _accountPool.SelectFailoverWorkspace(_workspaces(), request.Workspace.Id, tried, _settings?.Invoke().FailoverThresholdPercent ?? 92);
                    if (fallback is not null)
                    {
                        _accountPool.RecordFailoverSave();
                        tried.Add(fallback.Id);
                        request = request with { Workspace = fallback };
                        lock (_gate)
                        {
                            contestant.FailoverNotice = $"限流自动接力：{contestant.WorkspaceName} → {fallback.DisplayName}";
                            contestant.WorkspaceId = fallback.Id;
                            contestant.WorkspaceName = fallback.DisplayName;
                            contestant.Client = fallback.Client;
                        }
                        SessionUpdated?.Invoke(session);
                        continue;
                    }
                }

                lock (_gate)
                {
                    contestant.OutputText = exception.Message;
                    contestant.DurationSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
                    contestant.State = ArenaRunState.Failed;
                    SaveUnlocked();
                }
                break;
            }
            finally
            {
                _accountPool?.RecordTurnFinished(request.Workspace.Id, succeeded);
            }
        }

        SessionUpdated?.Invoke(session);
    }

    public void StopRace(string sessionId)
    {
        lock (_gate)
        {
            if (_activeRuns.TryGetValue(sessionId, out var cts))
                cts.Cancel();
        }
    }

    public async Task<string> MergeWinnerAsync(string sessionId, string contestantId, CancellationToken token)
    {
        ArenaSession session;
        ArenaContestant winner;
        lock (_gate)
        {
            session = _sessions.FirstOrDefault(s => s.Id == sessionId) ?? throw new InvalidOperationException("找不到该赛马场次。");
            if (_activeRuns.ContainsKey(sessionId)) throw new InvalidOperationException("请等待赛马完成或先停止运行后再合并胜出方案。");
            winner = session.Contestants.FirstOrDefault(c => c.Id == contestantId) ?? throw new InvalidOperationException("找不到指定的参赛方案。");
        }

        await CommitWorktreeIfNeededAsync(winner.WorktreePath, $"ADE Arena winner: {winner.Label} ({session.Title})", token).ConfigureAwait(false);
        var mergeSummary = await _git.MergeBranchIntoRepositoryAsync(session.RepositoryRoot, winner.BranchName, token).ConfigureAwait(false);

        lock (_gate)
        {
            session.MergedContestantId = winner.Id;
            winner.State = ArenaRunState.Merged;
            SaveUnlocked();
        }
        SessionUpdated?.Invoke(session);
        return mergeSummary;
    }

    public async Task ShutdownAsync()
    {
        CancellationTokenSource[] active;
        lock (_gate) active = _activeRuns.Values.ToArray();
        foreach (var cts in active)
        {
            try { cts.Cancel(); } catch { }
        }
        await Task.Delay(120).ConfigureAwait(false);
    }

    private static async Task CommitWorktreeIfNeededAsync(string worktreePath, string message, CancellationToken token)
    {
        var git = GitWorktreeService.FindGitExecutable();
        await RunSimpleProcessAsync(git, ["add", "-A"], worktreePath, token).ConfigureAwait(false);
        var status = await RunSimpleProcessAsync(git, ["status", "--porcelain=v1"], worktreePath, token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(status.Output))
        {
            await RunSimpleProcessAsync(
                git,
                ["-c", "user.name=Hyperion", "-c", "user.email=hyperion@local", "commit", "-m", message],
                worktreePath,
                token).ConfigureAwait(false);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunVerificationCommandAsync(string workingDirectory, string command, CancellationToken token)
    {
        var shell = GitWorktreeService.FindPreferredPowerShellExecutable();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMinutes(3));
        var result = await RunSimpleProcessAsync(shell, ["-NoLogo", "-NoProfile", "-Command", command], workingDirectory, budget.Token).ConfigureAwait(false);
        var trimmed = result.Output.Trim();
        if (trimmed.Length > 4000) trimmed = trimmed[^4000..];
        return (result.ExitCode, trimmed);
    }

    private static async Task<(int ExitCode, string Output)> RunSimpleProcessAsync(string fileName, IReadOnlyList<string> args, string workingDirectory, CancellationToken token)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"无法启动进程 {fileName}");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            return (process.ExitCode, (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private List<ArenaSession> LoadSessions()
    {
        try
        {
            ProfilePathSafety.ThrowIfReparsePath(_file);
            if (!File.Exists(_file)) return [];
            var loaded = JsonSerializer.Deserialize<List<ArenaSession>>(File.ReadAllText(_file), Json) ?? [];
            foreach (var c in loaded.SelectMany(s => s.Contestants))
            {
                if (c.State is ArenaRunState.Pending or ArenaRunState.Running)
                    c.State = ArenaRunState.Cancelled;
            }
            return loaded;
        }
        catch
        {
            return [];
        }
    }

    private void SaveUnlocked()
    {
        try
        {
            ProfilePathSafety.ThrowIfReparsePath(_file);
            var dir = Path.GetDirectoryName(_file)!;
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, $".arena.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(_sessions.Take(50).ToList(), Json));
                File.Move(tmp, _file, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        catch
        {
        }
    }
}
