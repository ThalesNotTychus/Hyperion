using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security;
using System.Text.Json;

namespace CodexMultiLauncher;

public enum CodexUsageState { Available, Unauthenticated, Unavailable }

public sealed record CodexUsageSnapshot(
    CodexUsageState State,
    string? AccountType,
    string? Email,
    string? Plan,
    decimal? PrimaryUsedPercent,
    DateTimeOffset? PrimaryResetsAt,
    decimal? SecondaryUsedPercent,
    DateTimeOffset? SecondaryResetsAt,
    decimal? WorkspaceCredits,
    bool? WorkspaceHasCredits,
    bool? WorkspaceCreditsUnlimited,
    decimal? RateLimitResetCredits,
    decimal? LifetimeTokens,
    DateTimeOffset RefreshedAtUtc);

public sealed class CodexAppServerProtocolException : InvalidOperationException
{
    public int? Code { get; }
    public CodexAppServerProtocolException(string message, int? code = null) : base(message) { Code = code; }
}

/// <summary>Read-only JSONL client. It never reads auth.json or sends mutating methods.</summary>
public sealed class CodexAppServerUsageClient : IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    // Per-CODEX_HOME semaphore so distinct accounts refresh in parallel while same-home queries stay serialized; cancellation never poisons future requests.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _requestGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CodexUsageSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var pair in _requestGates.ToArray())
        {
            if (_requestGates.TryRemove(pair.Key, out var gate))
            {
                try { gate.Dispose(); } catch (ObjectDisposedException) { }
            }
        }
    }

    public Task<CodexUsageSnapshot> ReadAsync(CodexWorkspace workspace, CancellationToken cancellationToken = default) => ReadAsync(workspace, false, cancellationToken);
    public async Task<CodexUsageSnapshot> ReadAsync(CodexWorkspace workspace, bool forceRefresh, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(workspace);
        if (!workspace.CanReadUsage) throw new InvalidOperationException("此工作区没有经过验证的 CODEX_HOME，不能读取用量。");
        var key = Path.GetFullPath(workspace.DataPaths.RequireCodexHome());
        var gate = _requestGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.RefreshedAtUtc <= CacheLifetime) return cached;
            var snapshot = await ReadCoreAsync(workspace, cancellationToken).ConfigureAwait(false);
            _cache[key] = snapshot;
            return snapshot;
        }
        finally { gate.Release(); }
    }

    private static async Task<CodexUsageSnapshot> ReadCoreAsync(CodexWorkspace workspace, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await using var session = await CodexAppServerSession.StartAsync(workspace.DataPaths, linked.Token).ConfigureAwait(false);
            return await ReadAccountUsageAsync(session.RequestAsync, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("读取 Codex 账号用量超时。"); }
    }

    internal static async Task<CodexUsageSnapshot> ReadAccountUsageAsync(Func<string, object?, CancellationToken, Task<JsonDocument>> request, CancellationToken token)
    {
            using var account = await request("account/read", new { refreshToken = false }, token).ConfigureAwait(false);
            var accountSnapshot = CodexUsageResponseParser.Parse([account], DateTimeOffset.UtcNow);
            if (!ShouldReadDetailedUsage(accountSnapshot)) return accountSnapshot;
            using var limits = await request("account/rateLimits/read", null, token).ConfigureAwait(false);
            JsonDocument? usage = null;
            try
            {
                try { usage = await request("account/usage/read", null, token).ConfigureAwait(false); }
                // This extension is optional on older official app-server versions. Other errors remain visible.
                catch (CodexAppServerProtocolException e) when (e.Code == -32601) { }
                return CodexUsageResponseParser.Parse(usage is null ? [account, limits] : [account, limits, usage], DateTimeOffset.UtcNow);
            }
            finally { usage?.Dispose(); }
    }

    public static string CreateInitializeRequestJson(int id) => CreateRequestJson(id, "initialize", new { clientInfo = new { name = "CodexMultiLauncher", title = "Codex Multi Launcher", version = "2" } });
    public static string CreateAccountReadRequestJson(int id) => CreateRequestJson(id, "account/read", new { refreshToken = false });
    public static string CreateInitializedNotificationJson() => JsonSerializer.Serialize(new { method = "initialized", @params = new { } });
    public static string CreateRequestJson(int id, string method, object? parameters) => parameters is null ? JsonSerializer.Serialize(new { id, method }) : JsonSerializer.Serialize(new { id, method, @params = parameters });
    public static bool ShouldReadDetailedUsage(CodexUsageSnapshot accountSnapshot) => accountSnapshot.State == CodexUsageState.Available;

}

public sealed record CodexExecutableCandidate(string Path, DateTimeOffset LastWriteTimeUtc, bool IsDirect);

public static class CodexExecutableLocator
{
    public static string? TryFindCurrentCandidate()
    {
        try { return FindCurrentCandidateOrThrow(); }
        catch (InvalidOperationException) { return null; }
    }

    public static string FindCurrentCandidateOrThrow()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        }
        .Where(value => !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value))
        .Select(value => Path.Combine(value!, "OpenAI", "Codex", "bin"))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        if (roots.Length == 0) throw new InvalidOperationException("无法确定当前用户的 LocalAppData 目录。");
        foreach (var root in roots)
        {
            if (!TryGetSafeDirectory(root, out var canonicalRoot)) continue;
            var selected = SelectCurrentCandidate(EnumerateCandidates(canonicalRoot));
            if (selected is not null) return selected.Path;
        }
        throw new InvalidOperationException("当前用户没有可安全使用的 Codex CLI 安装目录。");
    }
    public static CodexExecutableCandidate? SelectCurrentCandidate(IEnumerable<CodexExecutableCandidate> candidates) => candidates.Where(candidate => candidate.IsDirect).OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? candidates.OrderByDescending(candidate => candidate.LastWriteTimeUtc).ThenByDescending(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    private static IReadOnlyList<CodexExecutableCandidate> EnumerateCandidates(string root)
    {
        var candidates = new List<CodexExecutableCandidate>();
        var direct = TryCreateCandidate(Path.Combine(root, "codex.exe"), true, root);
        if (direct is not null) candidates.Add(direct);
        try
        {
            foreach (var version in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if ((File.GetAttributes(version) & FileAttributes.ReparsePoint) != 0) continue;
                    var candidate = TryCreateCandidate(Path.Combine(version, "codex.exe"), false, root);
                    if (candidate is not null) candidates.Add(candidate);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException) { }
        return candidates;
    }
    private static CodexExecutableCandidate? TryCreateCandidate(string path, bool direct, string root)
    {
        if (!PathIdentity.TryCanonicalize(path, out var canonical) || !IsContained(root, canonical)) return null;
        try { return File.Exists(canonical) && (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) == 0 ? new CodexExecutableCandidate(canonical, File.GetLastWriteTimeUtc(canonical), direct) : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException) { return null; }
    }
    private static bool TryGetSafeDirectory(string path, out string canonical) { canonical = string.Empty; if (!PathIdentity.TryCanonicalize(path, out canonical) || !Directory.Exists(canonical)) return false; try { return (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) == 0; } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException) { return false; } }
    private static bool IsContained(string root, string candidate) { var relative = Path.GetRelativePath(root, candidate); return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal); }
}

public static class CodexUsageResponseParser
{
    public static CodexUsageSnapshot Parse(IEnumerable<JsonDocument> responses, DateTimeOffset refreshedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(responses);
        JsonElement? account = null, rateLimits = null, rateLimitResetCredits = null, usage = null;
        foreach (var response in responses)
        {
            var result = response.RootElement.TryGetProperty("result", out var value) ? value : default;
            if (result.ValueKind != JsonValueKind.Object) continue;
            if (result.TryGetProperty("account", out var candidate)) account = candidate;
            if (result.TryGetProperty("rateLimits", out candidate)) rateLimits = candidate;
            if (result.TryGetProperty("rateLimitResetCredits", out candidate)) rateLimitResetCredits = candidate;
            if (result.TryGetProperty("summary", out candidate)) usage = candidate;
        }
        var state = account is { ValueKind: JsonValueKind.Object } ? CodexUsageState.Available : account is { ValueKind: JsonValueKind.Null } ? CodexUsageState.Unauthenticated : CodexUsageState.Unavailable;
        var primary = GetObject(rateLimits, "primary"); var secondary = GetObject(rateLimits, "secondary"); var credits = GetObject(rateLimits, "credits");
        var unlimited = GetBoolean(credits, "unlimited"); var hasCredits = GetBoolean(credits, "hasCredits");
        var balance = hasCredits == false || unlimited == true ? null : GetDecimal(credits, "balance");
        return new CodexUsageSnapshot(state, GetString(account, "type"), GetString(account, "email"), GetString(account, "planType"), GetDecimal(primary, "usedPercent"), GetDate(primary, "resetsAt"), GetDecimal(secondary, "usedPercent"), GetDate(secondary, "resetsAt"), balance, hasCredits, unlimited, GetDecimal(rateLimitResetCredits, "availableCount"), GetDecimal(usage, "lifetimeTokens"), refreshedAtUtc);
    }
    private static JsonElement? GetObject(JsonElement? parent, string property) => parent is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty(property, out var value) ? value : null;
    private static string? GetString(JsonElement? parent, string property) => parent is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool? GetBoolean(JsonElement? parent, string property) => parent is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty(property, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False) ? value.GetBoolean() : null;
    private static decimal? GetDecimal(JsonElement? parent, string property)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } element || !element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out number) ? number : null;
    }
    private static DateTimeOffset? GetDate(JsonElement? parent, string property)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } element || !element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var date)) return date;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds)) try { return DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { }
        return null;
    }
}
