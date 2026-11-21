using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodexMultiLauncher;

public enum AccountQuotaHealth
{
    Unknown,
    Healthy,
    Warning,
    Exhausted,
    Unauthenticated,
    UnsupportedClient,
}

public sealed record AccountPoolEntry(
    string WorkspaceId,
    string DisplayName,
    ClientKind Client,
    CodexWorkspaceKind Kind,
    AccountQuotaHealth Health,
    bool AuthReady,
    string EngineLabel,
    string? Email,
    string? Plan,
    decimal? PrimaryUsedPercent,
    decimal? PrimaryRemainingPercent,
    DateTimeOffset? PrimaryResetsAt,
    string? PrimaryResetCountdown,
    decimal? SecondaryUsedPercent,
    decimal? SecondaryRemainingPercent,
    DateTimeOffset? SecondaryResetsAt,
    string? SecondaryResetCountdown,
    decimal? WorkspaceCredits,
    bool? CreditsUnlimited,
    decimal? LifetimeTokens,
    int ActiveTasks,
    int SessionTurns,
    long SessionInputTokens,
    long SessionOutputTokens,
    string StatusText,
    DateTimeOffset? RefreshedAtUtc,
    string? CurrentActivity = null,
    DateTimeOffset? ActiveSinceUtc = null,
    string? LastActivity = null);

public sealed record AccountPoolSummary(
    IReadOnlyList<AccountPoolEntry> Entries,
    int ReadyNodeCount,
    int TotalNodeCount,
    int HealthyCodexCount,
    int TotalCodexCount,
    decimal? AverageRemainingPercent,
    string? NextResetSummary,
    int TotalCompletedTurns,
    int TotalFailoverSaves,
    long TotalSessionTokens,
    string? RecommendedWorkspaceId);

/// <summary>
/// Unified multi-platform account & quota telemetry engine covering Codex, Claude, and Antigravity:
/// tracks battery-style remaining percentages, reset countdowns, auth readiness, live task activity,
/// session token usage, and automatic rate-limit failover.
/// </summary>
public sealed partial class AccountPoolService : IDisposable
{
    private readonly CodexAppServerUsageClient _usageClient;
    private readonly bool _ownsUsageClient;
    private readonly AgentProviderStore? _agentProviders;
    private readonly ConcurrentDictionary<string, CodexUsageSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ClaudeAuthInspection> _claudeInspections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AntigravityUsageSnapshot> _antigravityUsage = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _lastErrors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldownUntilUtc = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AccountRuntimeStats> _runtimeStats = new(StringComparer.Ordinal);
    private int _totalFailoverSaves;

    public AccountPoolService(CodexAppServerUsageClient? usageClient = null, AgentProviderStore? agentProviders = null)
    {
        _ownsUsageClient = usageClient is null;
        _usageClient = usageClient ?? new CodexAppServerUsageClient();
        _agentProviders = agentProviders;
    }

    public CodexAppServerUsageClient UsageClient => _usageClient;

    public void Dispose()
    {
        if (_ownsUsageClient)
            _usageClient.Dispose();
    }

    public void RecordSnapshot(string workspaceId, CodexUsageSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return;
        _snapshots[workspaceId] = snapshot;
        _lastErrors.TryRemove(workspaceId, out _);
        if (snapshot.State == CodexUsageState.Available && (snapshot.PrimaryUsedPercent ?? 0m) < 99m)
            _cooldownUntilUtc.TryRemove(workspaceId, out _);
    }

    public void RecordAntigravityUsage(string workspaceId, AntigravityUsageSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return;
        _antigravityUsage[workspaceId] = snapshot;
        if ((snapshot.PrimaryUsedPercent ?? 0m) < 99m)
            _cooldownUntilUtc.TryRemove(workspaceId, out _);
    }

    public void RecordTurnStarted(string workspaceId, string? initialActivity = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return;
        var stats = _runtimeStats.GetOrAdd(workspaceId, _ => new AccountRuntimeStats());
        var count = Interlocked.Increment(ref stats.ActiveTasks);
        if (count == 1)
            Interlocked.Exchange(ref stats.ActiveSinceUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stats.CurrentActivity = string.IsNullOrWhiteSpace(initialActivity) ? "正在唤醒智能体执行引擎…" : initialActivity.Trim();
    }

    public void RecordStepProgress(string workspaceId, string? stepTitle)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(stepTitle)) return;
        var stats = _runtimeStats.GetOrAdd(workspaceId, _ => new AccountRuntimeStats());
        stats.CurrentActivity = stepTitle.Trim();
    }

    public void RecordTurnFinished(string workspaceId, bool succeeded)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return;
        var stats = _runtimeStats.GetOrAdd(workspaceId, _ => new AccountRuntimeStats());
        var cur = Interlocked.Decrement(ref stats.ActiveTasks);
        if (cur <= 0)
        {
            Interlocked.Exchange(ref stats.ActiveTasks, 0);
            var since = Interlocked.Exchange(ref stats.ActiveSinceUnixMs, 0);
            var elapsedSec = since > 0 ? Math.Max(1, (int)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - since) / 1000)) : 0;
            stats.CurrentActivity = null;
            stats.LastActivity = succeeded
                ? $"上次完成于 {DateTimeOffset.Now:HH:mm:ss}" + (elapsedSec > 0 ? $" · 耗时 {elapsedSec}s" : string.Empty)
                : $"上次中止于 {DateTimeOffset.Now:HH:mm:ss}";
        }
        if (succeeded) Interlocked.Increment(ref stats.CompletedTurns);
    }

    public void RecordTokenUsage(string workspaceId, long inputTokens, long outputTokens)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return;
        var stats = _runtimeStats.GetOrAdd(workspaceId, _ => new AccountRuntimeStats());
        if (inputTokens > 0) Interlocked.Add(ref stats.InputTokens, inputTokens);
        if (outputTokens > 0) Interlocked.Add(ref stats.OutputTokens, outputTokens);
    }

    public void RecordFailoverSave() => Interlocked.Increment(ref _totalFailoverSaves);

    public void MarkRateLimited(string workspaceId, TimeSpan? cooldown = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return;
        _cooldownUntilUtc[workspaceId] = DateTimeOffset.UtcNow + (cooldown ?? TimeSpan.FromMinutes(10));
    }

    public void ClearCooldown(string workspaceId) => _cooldownUntilUtc.TryRemove(workspaceId, out _);

    public async Task<CodexUsageSnapshot> RefreshWorkspaceAsync(CodexWorkspace workspace, bool force, CancellationToken token)
    {
        try
        {
            var snapshot = await _usageClient.ReadAsync(workspace, force, token).ConfigureAwait(false);
            RecordSnapshot(workspace.Id, snapshot);
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastErrors[workspace.Id] = ex.Message;
            throw;
        }
    }

    public async Task RefreshAllCodexAsync(
        IEnumerable<CodexWorkspace> workspaces,
        bool force,
        CancellationToken token,
        Action? onEachUpdated = null)
    {
        var all = workspaces.ToArray();

        // 1. Refresh Codex quota in parallel across isolated profiles
        var codexTasks = all
            .Where(item => item.CanReadUsage)
            .Select(async workspace =>
            {
                try
                {
                    await RefreshWorkspaceAsync(workspace, force, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { Debug.WriteLine($"[AccountPool] Codex quota refresh skipped for {workspace.Id}: {ex.Message}"); }
                finally { onEachUpdated?.Invoke(); }
            });

        // 2. Refresh Claude CLI auth inspection in parallel
        var claudeTasks = all
            .Where(item => item.Client == ClientKind.Claude && item.IsLaunchable)
            .Select(async workspace =>
            {
                try
                {
                    var info = await ClaudeCliRunner.InspectWorkspaceAuthAsync(workspace, all, token).ConfigureAwait(false);
                    _claudeInspections[workspace.Id] = info;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { Debug.WriteLine($"[AccountPool] Claude auth inspection skipped for {workspace.Id}: {ex.Message}"); }
                finally { onEachUpdated?.Invoke(); }
            });

        // 3. Refresh official Antigravity CLI quota (`agy -p /usage --output-format json`) when standalone `agy` CLI is installed
        IEnumerable<Task> agyTasks = [];
        if (AntigravityAgentRunner.TryFindAgyCliExecutable() is not null)
        {
            agyTasks = all
                .Where(item =>
                {
                    if (item.Client == ClientKind.Antigravity && item.IsLaunchable)
                        return AntigravityAgentRunner.InspectHome(AntigravityAgentRunner.GetHomeForWorkspace(item)).HasAuthState;
                    if (item.Client == ClientKind.MarketCli)
                    {
                        var rec = _agentProviders?.FindById(item.ProviderId ?? item.Id);
                        var home = item.AntigravityHome ?? item.ElectronUserData;
                        return AntigravityAgentRunner.IsAntigravityBackedCliRecord(rec, home);
                    }
                    return false;
                })
                .Select(async workspace =>
                {
                    try
                    {
                        var home = workspace.Client == ClientKind.Antigravity
                            ? AntigravityAgentRunner.GetHomeForWorkspace(workspace)
                            : (workspace.AntigravityHome ?? workspace.ElectronUserData);
                        var snap = await AntigravityAgentRunner.ReadCliUsageAsync(home, token).ConfigureAwait(false);
                        if (snap is not null)
                            RecordAntigravityUsage(workspace.Id, snap);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { Debug.WriteLine($"[AccountPool] Antigravity CLI usage check skipped for {workspace.Id}: {ex.Message}"); }
                    finally { onEachUpdated?.Invoke(); }
                });
        }

        await Task.WhenAll(codexTasks.Concat(claudeTasks).Concat(agyTasks)).ConfigureAwait(false);
    }

    public AccountPoolSummary BuildSummary(IReadOnlyList<CodexWorkspace> workspaces, int failoverThresholdPercent = 92)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        var now = DateTimeOffset.UtcNow;
        var entries = new List<AccountPoolEntry>(workspaces.Count);

        foreach (var workspace in workspaces)
        {
            _runtimeStats.TryGetValue(workspace.Id, out var rt);
            var activeTasks = Math.Max(0, rt?.ActiveTasks ?? 0);
            var turns = rt?.CompletedTurns ?? 0;
            var inTok = rt?.InputTokens ?? 0L;
            var outTok = rt?.OutputTokens ?? 0L;

            if (workspace.Client == ClientKind.Claude)
            {
                entries.Add(BuildClaudeEntry(workspace, activeTasks, turns, inTok, outTok));
                continue;
            }

            if (workspace.Client == ClientKind.Antigravity)
            {
                entries.Add(BuildAntigravityEntry(workspace, activeTasks, turns, inTok, outTok, now, failoverThresholdPercent));
                continue;
            }

            if (workspace.Client == ClientKind.DirectApi)
            {
                entries.Add(BuildDirectApiEntry(workspace, activeTasks, turns, inTok, outTok, now, failoverThresholdPercent));
                continue;
            }

            if (workspace.Client == ClientKind.MarketCli)
            {
                entries.Add(BuildMarketCliEntry(workspace, workspaces, activeTasks, turns, inTok, outTok, now, failoverThresholdPercent));
                continue;
            }

            if (!workspace.CanReadUsage)
            {
                entries.Add(new AccountPoolEntry(
                    workspace.Id, workspace.DisplayName, workspace.Client, workspace.Kind,
                    AccountQuotaHealth.UnsupportedClient, false, "外部未验证路径", null, null,
                    null, null, null, null, null, null, null, null, null, null, null,
                    activeTasks, turns, inTok, outTok,
                    "外部工作区 · 未验证配额", null));
                continue;
            }

            var inCooldown = _cooldownUntilUtc.TryGetValue(workspace.Id, out var until) && until > now;
            if (!_snapshots.TryGetValue(workspace.Id, out var snapshot))
            {
                _lastErrors.TryGetValue(workspace.Id, out var lastError);
                entries.Add(new AccountPoolEntry(
                    workspace.Id, workspace.DisplayName, workspace.Client, workspace.Kind,
                    inCooldown ? AccountQuotaHealth.Exhausted : AccountQuotaHealth.Unknown,
                    false, "Codex 无头引擎", null, null,
                    null, null, null, null, null, null, null, null, null, null, null,
                    activeTasks, turns, inTok, outTok,
                    inCooldown ? $"限流冷却 · {FormatCountdown(until - now)}后恢复" : (lastError ?? "正在同步官方配额…"),
                    null));
                continue;
            }

            // Important fix: Logged-in idle Codex accounts return PrimaryUsedPercent == null before any window usage!
            decimal? effectivePrimaryUsed = snapshot.State == CodexUsageState.Available
                ? Math.Clamp(snapshot.PrimaryUsedPercent ?? 0m, 0m, 100m)
                : snapshot.PrimaryUsedPercent;
            decimal? effectiveSecondaryUsed = snapshot.State == CodexUsageState.Available && snapshot.SecondaryUsedPercent is not null
                ? Math.Clamp(snapshot.SecondaryUsedPercent.Value, 0m, 100m)
                : (snapshot.State == CodexUsageState.Available ? 0m : snapshot.SecondaryUsedPercent);

            decimal? primaryRemaining = effectivePrimaryUsed is not null ? Math.Clamp(100m - effectivePrimaryUsed.Value, 0m, 100m) : null;
            decimal? secondaryRemaining = effectiveSecondaryUsed is not null ? Math.Clamp(100m - effectiveSecondaryUsed.Value, 0m, 100m) : null;
            var primaryCountdown = FormatResetCountdown(snapshot.PrimaryResetsAt, now);
            var secondaryCountdown = FormatResetCountdown(snapshot.SecondaryResetsAt, now);

            var health = ClassifyHealth(snapshot, effectivePrimaryUsed, effectiveSecondaryUsed, failoverThresholdPercent, inCooldown);
            var authReady = snapshot.State == CodexUsageState.Available;

            var status = activeTasks > 0
                ? $"⚡ 正在后台执行任务 ({activeTasks})"
                : inCooldown
                    ? $"限流冷却 · {FormatCountdown(until - now)}后恢复"
                    : snapshot.State switch
                    {
                        CodexUsageState.Unauthenticated => "未登录 · 点击临时弹窗登录一次",
                        CodexUsageState.Unavailable => _lastErrors.TryGetValue(workspace.Id, out var err) ? err : "配额暂不可用",
                        _ => health switch
                        {
                            AccountQuotaHealth.Exhausted => primaryCountdown is not null ? $"额度耗尽 · {primaryCountdown}重置" : "额度即将耗尽",
                            AccountQuotaHealth.Warning => $"余量偏低 ({primaryRemaining:0}%) · 自动接力保护中",
                            _ => $"满血待命 · 剩余 {primaryRemaining:0}%",
                        },
                    };

            entries.Add(new AccountPoolEntry(
                workspace.Id, workspace.DisplayName, workspace.Client, workspace.Kind,
                health, authReady, "Codex 无头引擎",
                snapshot.Email, snapshot.Plan,
                effectivePrimaryUsed, primaryRemaining, snapshot.PrimaryResetsAt, primaryCountdown,
                effectiveSecondaryUsed, secondaryRemaining, snapshot.SecondaryResetsAt, secondaryCountdown,
                snapshot.WorkspaceCredits, snapshot.WorkspaceCreditsUnlimited,
                snapshot.LifetimeTokens,
                activeTasks, turns, inTok, outTok,
                status, snapshot.RefreshedAtUtc));
        }

        for (var idx = 0; idx < entries.Count; idx++)
        {
            var item = entries[idx];
            if (_runtimeStats.TryGetValue(item.WorkspaceId, out var rts))
            {
                var sinceMs = Interlocked.Read(ref rts.ActiveSinceUnixMs);
                entries[idx] = item with
                {
                    CurrentActivity = item.ActiveTasks > 0 ? rts.CurrentActivity : null,
                    ActiveSinceUtc = item.ActiveTasks > 0 && sinceMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(sinceMs) : null,
                    LastActivity = rts.LastActivity,
                };
            }
        }

        var launchableEntries = entries.Where(e => e.Kind != CodexWorkspaceKind.ExternalUnresolved).ToArray();
        var readyNodes = launchableEntries.Count(e => e.AuthReady && e.Health != AccountQuotaHealth.Exhausted);

        var codexEntries = entries.Where(item => item.Client == ClientKind.Codex && item.Health != AccountQuotaHealth.UnsupportedClient).ToArray();
        var healthyCodexCount = codexEntries.Count(item => item.Health is AccountQuotaHealth.Healthy or AccountQuotaHealth.Warning);
        var measured = entries
            .Where(item => item.PrimaryRemainingPercent is not null
                && item.Health is not (AccountQuotaHealth.Unauthenticated or AccountQuotaHealth.UnsupportedClient))
            .ToArray();
        decimal? avgRemaining = measured.Length == 0
            ? null
            : Math.Clamp(measured.Average(item => item.PrimaryRemainingPercent!.Value), 0m, 100m);

        var nextResetItem = codexEntries
            .Where(e => e.PrimaryResetsAt is not null && e.PrimaryResetsAt > now && (e.PrimaryRemainingPercent ?? 100m) < 100m)
            .OrderBy(e => e.PrimaryResetsAt)
            .FirstOrDefault();
        var nextResetSummary = nextResetItem is not null
            ? $"{nextResetItem.DisplayName} · {nextResetItem.PrimaryResetCountdown}重置"
            : null;

        var totalTurns = entries.Sum(e => e.SessionTurns);
        var totalSessionTok = entries.Sum(e => e.SessionInputTokens + e.SessionOutputTokens);
        var recommended = SelectBestCodexWorkspace(workspaces, excludedWorkspaceIds: null, failoverThresholdPercent)?.Id;

        return new AccountPoolSummary(
            entries,
            readyNodes,
            launchableEntries.Length,
            healthyCodexCount,
            codexEntries.Length,
            avgRemaining,
            nextResetSummary,
            totalTurns,
            _totalFailoverSaves,
            totalSessionTok,
            recommended);
    }

    private AccountPoolEntry BuildClaudeEntry(CodexWorkspace workspace, int activeTasks, int turns, long inTok, long outTok)
    {
        if (!workspace.IsLaunchable)
        {
            return new AccountPoolEntry(
                workspace.Id, workspace.DisplayName, ClientKind.Claude, workspace.Kind,
                AccountQuotaHealth.UnsupportedClient, false, "外部 Claude", null, null,
                null, null, null, null, null, null, null, null, null, null, null,
                activeTasks, turns, inTok, outTok, "外部未验证路径", null);
        }

        var now = DateTimeOffset.UtcNow;
        var inCooldown = _cooldownUntilUtc.TryGetValue(workspace.Id, out var until) && until > now;
        _claudeInspections.TryGetValue(workspace.Id, out var info);
        var engine = info?.CliVersion is not null ? $"Claude Code v{info.CliVersion}" : "Claude 无头引擎";
        var loggedIn = info?.LoggedIn ?? Directory.Exists(Path.Combine(workspace.ElectronUserData, "Local Storage"));
        var ready = loggedIn && !inCooldown;
        var health = !loggedIn
            ? AccountQuotaHealth.Unauthenticated
            : inCooldown
                ? AccountQuotaHealth.Exhausted
                : AccountQuotaHealth.Healthy;
        var status = activeTasks > 0
            ? $"⚡ 正在后台执行任务 ({activeTasks})"
            : inCooldown
                ? $"限流冷却 · {FormatCountdown(until - now)}后恢复"
                : (info?.StatusSummary ?? (loggedIn ? "已初始化 · 后台无头就绪" : "待登录 · 可在终端执行 claude auth login"));

        return new AccountPoolEntry(
            workspace.Id, workspace.DisplayName, ClientKind.Claude, workspace.Kind,
            health, ready, engine, info?.Email, info?.SubscriptionType ?? "Claude",
            null, null, null, null, null, null, null, null, null, null, null,
            activeTasks, turns, inTok, outTok, status, now);
    }

    private AccountPoolEntry BuildAntigravityEntry(
        CodexWorkspace workspace,
        int activeTasks,
        int turns,
        long inTok,
        long outTok,
        DateTimeOffset now,
        int thresholdPercent)
    {
        if (!workspace.IsLaunchable)
        {
            return new AccountPoolEntry(
                workspace.Id, workspace.DisplayName, ClientKind.Antigravity, workspace.Kind,
                AccountQuotaHealth.UnsupportedClient, false, "外部 Antigravity", null, null,
                null, null, null, null, null, null, null, null, null, null, null,
                activeTasks, turns, inTok, outTok, "外部未验证路径", null);
        }

        var inspection = AntigravityAgentRunner.InspectHome(AntigravityAgentRunner.GetHomeForWorkspace(workspace));
        var inCooldown = _cooldownUntilUtc.TryGetValue(workspace.Id, out var until) && until > now;
        _antigravityUsage.TryGetValue(workspace.Id, out var agyUsage);

        decimal? pUsed = agyUsage?.PrimaryUsedPercent is { } pu ? Math.Clamp(pu, 0m, 100m) : null;
        decimal? pRem = pUsed is not null ? Math.Clamp(100m - pUsed.Value, 0m, 100m) : null;
        decimal? sUsed = agyUsage?.SecondaryUsedPercent is { } su ? Math.Clamp(su, 0m, 100m) : null;
        decimal? sRem = sUsed is not null ? Math.Clamp(100m - sUsed.Value, 0m, 100m) : null;
        var pCountdown = FormatResetCountdown(agyUsage?.PrimaryResetsAt, now);
        var sCountdown = FormatResetCountdown(agyUsage?.SecondaryResetsAt, now);

        var health = !inspection.IsAuthenticated
            ? AccountQuotaHealth.Unauthenticated
            : inCooldown || (pUsed is not null && pUsed >= thresholdPercent) || (sUsed is not null && sUsed >= thresholdPercent)
                ? AccountQuotaHealth.Exhausted
                : (pUsed is not null && pUsed >= Math.Max(70, thresholdPercent - 15))
                    ? AccountQuotaHealth.Warning
                    : AccountQuotaHealth.Healthy;

        var status = activeTasks > 0
            ? $"⚡ 正在执行 ACP 任务 ({activeTasks})"
            : !inspection.HasAgyCli
                ? "待安装官方 `agy` CLI · 按订阅周期限额"
                : !inspection.IsAuthenticated
                    ? "待登录 · 请在终端执行 `agy acp --login` 授权"
                    : inCooldown
                        ? $"限流冷却 · {FormatCountdown(until - now)}后恢复"
                        : pRem is not null
                            ? $"官方 `agy` 额度 {pRem:0}%"
                            : "官方 `agy acp` 就绪 · 按订阅周期限额";

        return new AccountPoolEntry(
            workspace.Id,
            workspace.DisplayName,
            ClientKind.Antigravity,
            workspace.Kind,
            health,
            inspection.IsAuthenticated && !inCooldown,
            "Antigravity ACP",
            agyUsage?.Account,
            agyUsage?.Plan ?? "AGY ACP",
            pUsed,
            pRem,
            agyUsage?.PrimaryResetsAt,
            pCountdown,
            sUsed,
            sRem,
            agyUsage?.SecondaryResetsAt,
            sCountdown,
            null,
            CreditsUnlimited: false,
            (inTok + outTok) > 0 ? (inTok + outTok) : null,
            activeTasks,
            turns,
            inTok,
            outTok,
            status,
            agyUsage?.RefreshedAtUtc ?? now);
    }

    private AccountPoolEntry BuildDirectApiEntry(
        CodexWorkspace workspace,
        int activeTasks,
        int turns,
        long inTok,
        long outTok,
        DateTimeOffset now,
        int thresholdPercent)
    {
        var rec = _agentProviders?.FindById(workspace.ProviderId ?? workspace.Id);
        var preset = UniversalAgentCatalog.GetApiPreset(rec?.PresetId);
        var model = rec?.Model ?? workspace.DefaultModel ?? preset.DefaultModel;
        var inCooldown = _cooldownUntilUtc.TryGetValue(workspace.Id, out var until) && until > now;
        var isLocalOllama = string.Equals(preset.Id, "ollama", StringComparison.OrdinalIgnoreCase);
        var hasRequiredKey = rec is null || !preset.RequiresApiKey || DpapiSecretProtector.CanUnprotect(rec.EncryptedApiKey);

        var totalTok = inTok + outTok;
        decimal? usedPct = null;
        decimal? remPct = isLocalOllama ? 100m : null;
        bool? unlimited = isLocalOllama ? true : null;
        if (rec?.TokenBudgetLimit is > 0)
        {
            unlimited = false;
            usedPct = Math.Clamp((decimal)totalTok * 100m / rec.TokenBudgetLimit.Value, 0m, 100m);
            remPct = Math.Clamp(100m - usedPct.Value, 0m, 100m);
        }

        var health = !hasRequiredKey
            ? AccountQuotaHealth.Unauthenticated
            : inCooldown || (usedPct is not null && usedPct >= thresholdPercent)
                ? AccountQuotaHealth.Exhausted
                : (usedPct is not null && usedPct >= Math.Max(70, thresholdPercent - 15))
                    ? AccountQuotaHealth.Warning
                    : AccountQuotaHealth.Healthy;

        var status = activeTasks > 0
            ? $"⚡ 正在直连执行 ({activeTasks})"
            : !hasRequiredKey
                ? "待配置密钥 · 点击编辑填入 API Key"
                : inCooldown
                    ? $"限流冷却 · {FormatCountdown(until - now)}后恢复"
                    : rec?.TokenBudgetLimit is > 0
                        ? $"预算余量 {remPct:0}% · 免客户端直连"
                        : $"免客户端就绪 · 内置工具链 ({model})";

        return new AccountPoolEntry(
            workspace.Id,
            workspace.DisplayName,
            ClientKind.DirectApi,
            workspace.Kind,
            health,
            hasRequiredKey && !inCooldown,
            $"内置直连 · {preset.Badge}",
            rec?.MaskedApiKey,
            model,
            usedPct,
            remPct,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            unlimited,
            totalTok > 0 ? totalTok : null,
            activeTasks,
            turns,
            inTok,
            outTok,
            status,
            now);
    }

    private AccountPoolEntry BuildMarketCliEntry(
        CodexWorkspace workspace,
        IReadOnlyList<CodexWorkspace> allWorkspaces,
        int activeTasks,
        int turns,
        long inTok,
        long outTok,
        DateTimeOffset now,
        int thresholdPercent)
    {
        var rec = _agentProviders?.FindById(workspace.ProviderId ?? workspace.Id);
        var homeDir = workspace.AntigravityHome ?? workspace.ElectronUserData;
        var isAntigravityBacked = AntigravityAgentRunner.IsAntigravityBackedCliRecord(rec, homeDir);
        var effectivePresetId = isAntigravityBacked ? "antigravity-cli" : rec?.PresetId;
        var preset = UniversalAgentCatalog.GetCliPreset(effectivePresetId);

        var hasBinary = !string.IsNullOrWhiteSpace(rec?.CustomCliExecutable)
            ? File.Exists(rec!.CustomCliExecutable) || UniversalAgentCatalog.TryFindCliBinary("custom", Path.GetFileNameWithoutExtension(rec.CustomCliExecutable)) is not null
            : preset.Id == "custom-cli" || UniversalAgentCatalog.TryFindCliBinary(preset.Id, preset.BinaryName, allWorkspaces) is not null;

        var inCooldown = _cooldownUntilUtc.TryGetValue(workspace.Id, out var until) && until > now;
        var hasCliAuth = hasBinary;
        AntigravityHomeInspection? agyInspection = null;

        if (isAntigravityBacked)
        {
            agyInspection = AntigravityAgentRunner.InspectHome(homeDir);
            hasBinary = !string.IsNullOrWhiteSpace(rec?.CustomCliExecutable) ? hasBinary : agyInspection.HasAgyCli;
            hasCliAuth = hasBinary && agyInspection.HasAuthState;
        }
        else if (preset.Id == "gemini-cli" && string.IsNullOrWhiteSpace(rec?.EncryptedApiKey))
        {
            hasCliAuth = hasBinary && AntigravityAgentRunner.HasStandaloneGeminiCliOauth(homeDir);
        }

        var totalTok = inTok + outTok;
        decimal? usedPct = null;
        decimal? remPct = null;
        DateTimeOffset? pResetAt = null;
        string? pCountdown = null;
        decimal? sUsedPct = null;
        decimal? sRemPct = null;
        DateTimeOffset? sResetAt = null;
        string? sCountdown = null;
        string? accountEmail = rec?.MaskedApiKey;
        string? agyPlanOverride = null;

        if (isAntigravityBacked && _antigravityUsage.TryGetValue(workspace.Id, out var agyUsage))
        {
            if (!string.IsNullOrWhiteSpace(agyUsage.Account))
                accountEmail = agyUsage.Account;
            if (!string.IsNullOrWhiteSpace(agyUsage.Plan))
                agyPlanOverride = agyUsage.Plan + " · AGY";

            if (agyUsage.PrimaryUsedPercent is { } agyPu)
            {
                usedPct = Math.Clamp(agyPu, 0m, 100m);
                remPct = Math.Clamp(100m - usedPct.Value, 0m, 100m);
                pResetAt = agyUsage.PrimaryResetsAt;
                pCountdown = FormatResetCountdown(pResetAt, now);
            }
            if (agyUsage.SecondaryUsedPercent is { } agySu)
            {
                sUsedPct = Math.Clamp(agySu, 0m, 100m);
                sRemPct = Math.Clamp(100m - sUsedPct.Value, 0m, 100m);
                sResetAt = agyUsage.SecondaryResetsAt;
                sCountdown = FormatResetCountdown(sResetAt, now);
            }
        }
        else if (hasCliAuth && rec?.TokenBudgetLimit is > 0)
        {
            usedPct = Math.Clamp((decimal)totalTok * 100m / rec.TokenBudgetLimit.Value, 0m, 100m);
            remPct = Math.Clamp(100m - usedPct.Value, 0m, 100m);
        }

        var health = !hasBinary || !hasCliAuth
            ? AccountQuotaHealth.Unauthenticated
            : inCooldown || (usedPct is not null && usedPct >= thresholdPercent) || (sUsedPct is not null && sUsedPct >= thresholdPercent)
                ? AccountQuotaHealth.Exhausted
                : (usedPct is not null && usedPct >= Math.Max(70, thresholdPercent - 15))
                    ? AccountQuotaHealth.Warning
                    : AccountQuotaHealth.Healthy;

        var status = activeTasks > 0
            ? $"⚡ 正在无头执行 ({activeTasks})"
            : !hasBinary
                ? (isAntigravityBacked
                    ? "待安装官方 `agy` CLI · 按订阅周期限额"
                    : $"待安装 `{preset.BinaryName}` · 支持一键终端安装")
                : !hasCliAuth
                    ? (isAntigravityBacked ? "待登录 · 请在终端执行 `agy acp --login` 授权" : "已就绪 CLI · 点击「🔑 登录」在隔离目录授权一次 Google 号")
                    : inCooldown
                        ? $"限流冷却 · {FormatCountdown(until - now)}后恢复"
                        : isAntigravityBacked && agyInspection?.IsAuthenticated == true
                            ? (remPct is not null ? $"官方 `agy` 额度 {remPct:0}%" : "官方 `agy acp` 就绪 · 按订阅周期限额")
                            : "免客户端就绪 · 独立目录永不串号";

        var engineLabel = isAntigravityBacked ? "Antigravity ACP" : $"轻量 CLI · {preset.Badge}";
        var planLabel = isAntigravityBacked
            ? (agyPlanOverride ?? "AGY ACP")
            : (string.IsNullOrWhiteSpace(rec?.Model) ? preset.Badge : rec!.Model);

        return new AccountPoolEntry(
            workspace.Id,
            workspace.DisplayName,
            ClientKind.MarketCli,
            workspace.Kind,
            health,
            hasCliAuth && !inCooldown,
            engineLabel,
            accountEmail,
            planLabel,
            usedPct,
            remPct,
            pResetAt,
            pCountdown,
            sUsedPct,
            sRemPct,
            sResetAt,
            sCountdown,
            null,
            CreditsUnlimited: false,
            totalTok > 0 ? totalTok : null,
            activeTasks,
            turns,
            inTok,
            outTok,
            status,
            now);
    }

    public CodexWorkspace? SelectFailoverWorkspace(
        IReadOnlyList<CodexWorkspace> workspaces,
        string currentWorkspaceId,
        IReadOnlySet<string>? triedWorkspaceIds,
        int failoverThresholdPercent = 92)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal) { currentWorkspaceId };
        if (triedWorkspaceIds is not null)
        {
            foreach (var id in triedWorkspaceIds) excluded.Add(id);
        }

        var origin = workspaces.FirstOrDefault(w => string.Equals(w.Id, currentWorkspaceId, StringComparison.Ordinal));
        var now = DateTimeOffset.UtcNow;

        bool IsNonCodexHealthyReady(CodexWorkspace w)
        {
            if (IsPreFlightExhausted(w.Id, failoverThresholdPercent)) return false;
            return w.Client switch
            {
                ClientKind.DirectApi => BuildDirectApiEntry(w, 0, 0, 0, 0, now, failoverThresholdPercent).AuthReady,
                ClientKind.MarketCli => BuildMarketCliEntry(w, workspaces, 0, 0, 0, 0, now, failoverThresholdPercent).AuthReady,
                ClientKind.Antigravity => BuildAntigravityEntry(w, 0, 0, 0, 0, now, failoverThresholdPercent).AuthReady,
                ClientKind.Claude => BuildClaudeEntry(w, 0, 0, 0, 0).AuthReady,
                _ => false,
            };
        }

        // 1. Prefer healthy fallback of the same engine family first (preserving prompt/tool behavior)
        if (origin is not null && origin.Client != ClientKind.Codex)
        {
            var sameFamily = workspaces
                .Where(w => w.Client == origin.Client && !excluded.Contains(w.Id))
                .FirstOrDefault(IsNonCodexHealthyReady);
            if (sameFamily is not null) return sameFamily;
        }

        // 2. Best Codex pool account
        var bestCodex = SelectBestCodexWorkspace(workspaces, excluded, failoverThresholdPercent);
        if (bestCodex is not null) return bestCodex;

        // 3. Any other healthy DirectApi / MarketCli / Antigravity / Claude account
        return workspaces
            .Where(w => w.Client is ClientKind.DirectApi or ClientKind.MarketCli or ClientKind.Antigravity or ClientKind.Claude && !excluded.Contains(w.Id))
            .OrderBy(w => w.Client switch
            {
                ClientKind.DirectApi => 0,
                ClientKind.MarketCli => 1,
                ClientKind.Antigravity => 2,
                _ => 3,
            })
            .FirstOrDefault(IsNonCodexHealthyReady);
    }

    public CodexWorkspace? SelectBestCodexWorkspace(
        IReadOnlyList<CodexWorkspace> workspaces,
        IReadOnlySet<string>? excludedWorkspaceIds,
        int failoverThresholdPercent = 92)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = workspaces
            .Where(workspace => workspace.Client == ClientKind.Codex && workspace.CanReadUsage)
            .Where(workspace => excludedWorkspaceIds is null || !excludedWorkspaceIds.Contains(workspace.Id))
            .Select(workspace =>
            {
                var inCooldown = _cooldownUntilUtc.TryGetValue(workspace.Id, out var until) && until > now;
                _snapshots.TryGetValue(workspace.Id, out var snapshot);
                decimal? used = snapshot?.State == CodexUsageState.Available ? (snapshot.PrimaryUsedPercent ?? 0m) : snapshot?.PrimaryUsedPercent;
                decimal? sec = snapshot?.State == CodexUsageState.Available ? (snapshot.SecondaryUsedPercent ?? 0m) : snapshot?.SecondaryUsedPercent;
                var health = snapshot is null
                    ? (inCooldown ? AccountQuotaHealth.Exhausted : AccountQuotaHealth.Unknown)
                    : ClassifyHealth(snapshot, used, sec, failoverThresholdPercent, inCooldown);
                return (Workspace: workspace, Used: used, Sec: sec, Health: health, InCooldown: inCooldown);
            })
            .Where(item => !item.InCooldown && item.Health is not (AccountQuotaHealth.Exhausted or AccountQuotaHealth.Unauthenticated))
            .OrderBy(item => item.Health switch
            {
                AccountQuotaHealth.Healthy => 0,
                AccountQuotaHealth.Unknown => 1,
                AccountQuotaHealth.Warning => 2,
                _ => 3,
            })
            .ThenBy(item => item.Workspace.Kind == CodexWorkspaceKind.Managed ? 0 : 1)
            .ThenBy(item => item.Used ?? 50m)
            .ThenBy(item => item.Sec ?? 50m)
            .ToArray();

        return candidates.FirstOrDefault().Workspace;
    }

    public bool IsPreFlightExhausted(string workspaceId, int failoverThresholdPercent = 92)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cooldownUntilUtc.TryGetValue(workspaceId, out var until) && until > now) return true;

        var rec = _agentProviders?.FindById(workspaceId);
        if (rec?.TokenBudgetLimit is > 0 && _runtimeStats.TryGetValue(workspaceId, out var rt))
        {
            var totalTok = Interlocked.Read(ref rt.InputTokens) + Interlocked.Read(ref rt.OutputTokens);
            var usedPct = (decimal)totalTok * 100m / rec.TokenBudgetLimit.Value;
            if (usedPct >= failoverThresholdPercent || usedPct >= 99m) return true;
        }

        if (_antigravityUsage.TryGetValue(workspaceId, out var agySnap))
        {
            var agyPrimary = agySnap.PrimaryUsedPercent ?? 0m;
            var agySecondary = agySnap.SecondaryUsedPercent ?? 0m;
            if (agyPrimary >= failoverThresholdPercent || agySecondary >= failoverThresholdPercent || agyPrimary >= 99m || agySecondary >= 99m)
                return true;
        }

        if (!_snapshots.TryGetValue(workspaceId, out var snapshot)) return false;
        decimal? used = snapshot.State == CodexUsageState.Available ? (snapshot.PrimaryUsedPercent ?? 0m) : snapshot.PrimaryUsedPercent;
        decimal? sec = snapshot.State == CodexUsageState.Available ? (snapshot.SecondaryUsedPercent ?? 0m) : snapshot.SecondaryUsedPercent;
        return ClassifyHealth(snapshot, used, sec, failoverThresholdPercent, false) == AccountQuotaHealth.Exhausted;
    }

    public static bool IsQuotaOrRateLimitFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        if (Http429Regex().IsMatch(message)) return true;
        string[] markers =
        [
            "rate limit",
            "rate_limit",
            "usage limit",
            "usage_limit",
            "quota exceeded",
            "insufficient_quota",
            "too many requests",
            "reached your usage",
            "hit your limit",
            "resource_exhausted",
            "额度已用尽",
            "配额不足",
            "触发限流",
        ];
        return markers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"(?<!\d)429(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Http429Regex();

    private static AccountQuotaHealth ClassifyHealth(
        CodexUsageSnapshot snapshot,
        decimal? effectivePrimaryUsed,
        decimal? effectiveSecondaryUsed,
        int thresholdPercent,
        bool inCooldown)
    {
        if (inCooldown) return AccountQuotaHealth.Exhausted;
        if (snapshot.State == CodexUsageState.Unauthenticated) return AccountQuotaHealth.Unauthenticated;
        if (snapshot.State != CodexUsageState.Available) return AccountQuotaHealth.Unknown;
        var primary = effectivePrimaryUsed ?? 0m;
        var secondary = effectiveSecondaryUsed ?? 0m;
        var peak = Math.Max(primary, secondary);
        if (peak >= thresholdPercent || peak >= 99m) return AccountQuotaHealth.Exhausted;
        if (peak >= Math.Max(70, thresholdPercent - 15)) return AccountQuotaHealth.Warning;
        return AccountQuotaHealth.Healthy;
    }

    private static string? FormatResetCountdown(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null) return null;
        var delta = resetsAt.Value - now;
        if (delta <= TimeSpan.Zero) return "即将";
        return FormatCountdown(delta) + "后";
    }

    private static string FormatCountdown(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}天{span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h{span.Minutes:00}m";
        return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
    }

    private sealed class AccountRuntimeStats
    {
        public int ActiveTasks;
        public int CompletedTurns;
        public long InputTokens;
        public long OutputTokens;
        public long ActiveSinceUnixMs;
        public volatile string? CurrentActivity;
        public volatile string? LastActivity;
    }
}
