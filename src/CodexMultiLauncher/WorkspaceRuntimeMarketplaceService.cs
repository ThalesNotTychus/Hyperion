using System.Text.Json;

namespace CodexMultiLauncher;

public sealed record RuntimeMarketplacePreparation(bool Changed, string? BackupPath);

/// <summary>
/// The official desktop registers its local runtime marketplace in config.toml.
/// Moving the runtime cache must also move that one registration, while preserving
/// plugin settings. Only the official versioned config writer may change TOML.
/// </summary>
public static class WorkspaceRuntimeMarketplaceService
{
    public const string MarketplaceName = "openai-primary-runtime";
    public static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(20);

    public static string GetLegacyMarketplaceSource()
    {
        // Windows' known folder remains the real profile even when this process
        // was launched inside a desktop with an isolated USERPROFILE environment.
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!PathIdentity.TryCanonicalize(userProfile, out var actualHome))
            throw new InvalidOperationException("无法验证 Windows 用户主目录，未迁移运行时插件市场。");
        return Path.Combine(actualHome, ".cache", "codex-runtimes", "codex-primary-runtime", "plugins", MarketplaceName);
    }

    public static string GetMarketplaceSource(WorkspaceDataPaths paths)
    {
        var runtimeHome = RuntimeHomeIsolation.Validate(paths)
            ?? throw new InvalidOperationException("运行时插件市场迁移需要受管账号的隔离目录。");
        var source = ProfilePathSafety.GetContainedFullPath(runtimeHome,
            ".cache", "codex-runtimes", "codex-primary-runtime", "plugins", MarketplaceName);
        ProfilePathSafety.ThrowIfReparsePath(Path.Combine(source, ".agents", "plugins", "marketplace.json"));
        return source;
    }

    public static async Task<RuntimeMarketplacePreparation> PrepareAsync(CodexWorkspace workspace, CancellationToken token = default)
    {
        if (workspace.Client != ClientKind.Codex || workspace.Kind != CodexWorkspaceKind.Managed)
            throw new InvalidOperationException("运行时插件市场迁移只允许用于启动器管理的账号。");
        var targetSource = GetMarketplaceSource(workspace.DataPaths);
        var legacySource = GetLegacyMarketplaceSource();
        var configPath = Path.Combine(workspace.DataPaths.RequireCodexHome(), "config.toml");
        ProfilePathSafety.ThrowIfReparsePath(configPath);
        using var timeout = new CancellationTokenSource(PreparationTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            await using var session = await CodexAppServerSession.StartAsync(workspace.DataPaths, linked.Token).ConfigureAwait(false);
            using var read = await session.RequestAsync("config/read", new { includeLayers = true }, linked.Token).ConfigureAwait(false);
            var result = read.RootElement.GetProperty("result");
            var user = WorkspaceSandboxService.GetUserLayer(result, configPath);
            if (!NeedsMigration(user.GetProperty("config"), legacySource, targetSource)) return new(false, null);
            var effective = result.GetProperty("config");
            if (!HasSource(effective, legacySource) && !HasSource(effective, targetSource))
                throw new InvalidOperationException("运行时插件市场被其他配置层覆盖，未修改配置或启动客户端。");
            var version = user.GetProperty("version").GetString();
            if (string.IsNullOrEmpty(version))
                throw new InvalidOperationException("官方配置未返回版本，无法安全迁移运行时插件市场。");
            ValidateExistingManifest(targetSource);
            var registeredSource = WindowsCanonicalDirectory.Resolve(targetSource);
            // The target may not exist on the first isolated launch. The official
            // writer accepts this path; the desktop supplies the runtime later.
            var backup = configPath + $".before-runtime-marketplace-{Guid.NewGuid():N}.bak";
            File.Copy(configPath, backup, overwrite: false);
            using var written = await session.RequestAsync("config/value/write",
                CreateWriteParameters(configPath, version, registeredSource), linked.Token).ConfigureAwait(false);
            if (written.RootElement.GetProperty("result").GetProperty("status").GetString() != "ok")
                throw new InvalidOperationException("运行时插件市场更新被策略覆盖，已停止启动；原配置已备份。");
            using var verified = await session.RequestAsync("config/read", new { includeLayers = true }, linked.Token).ConfigureAwait(false);
            var verification = verified.RootElement.GetProperty("result");
            if (!HasSource(verification.GetProperty("config"), registeredSource)
                || !HasSource(WorkspaceSandboxService.GetUserLayer(verification, configPath).GetProperty("config"), registeredSource))
                throw new InvalidOperationException("官方客户端未确认运行时插件市场新路径，已停止启动；原配置已备份。");
            return new(true, backup);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        { throw new TimeoutException("准备运行时插件市场超时；未启动新的客户端。"); }
    }

    public static bool NeedsMigration(JsonElement config, string legacySource, string targetSource) =>
        HasSource(config, legacySource) && !HasSource(config, targetSource);

    public static object CreateWriteParameters(string configPath, string version, string targetSource) => new
    { keyPath = "marketplaces.openai-primary-runtime.source", value = ToRegisteredSource(targetSource), mergeStrategy = "replace", filePath = Path.GetFullPath(configPath), expectedVersion = version };

    private static string ToRegisteredSource(string source)
    {
        if (!TryNormalizeSource(source, out var path))
            throw new InvalidOperationException("运行时插件市场必须使用绝对目录。");
        // marketplace/add canonicalizes local paths to the Windows verbatim form
        // and compares source strings exactly. A plain C:\ path still conflicts.
        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    private static bool HasSource(JsonElement config, string expectedSource) => config.ValueKind == JsonValueKind.Object
        && config.TryGetProperty("marketplaces", out var marketplaces) && marketplaces.ValueKind == JsonValueKind.Object
        && marketplaces.TryGetProperty(MarketplaceName, out var marketplace) && marketplace.ValueKind == JsonValueKind.Object
        && marketplace.TryGetProperty("source_type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "local"
        && (!marketplace.TryGetProperty("ref", out var reference) || reference.ValueKind == JsonValueKind.Null)
        && (!marketplace.TryGetProperty("sparse_paths", out var sparse) || sparse.ValueKind == JsonValueKind.Null
            || (sparse.ValueKind == JsonValueKind.Array && sparse.GetArrayLength() == 0))
        && marketplace.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String
        && TryNormalizeSource(source.GetString(), out var path) && TryNormalizeSource(expectedSource, out var expected)
        && PathIdentity.Equals(path, expected);

    private static bool TryNormalizeSource(string? source, out string path)
    {
        // The official Rust writer stores Windows canonical paths with a verbatim
        // prefix. Match the same absolute directory, never a string prefix.
        if (source?.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) == true)
            source = @"\\" + source[8..];
        else if (source?.StartsWith(@"\\?\", StringComparison.Ordinal) == true)
            source = source[4..];
        return PathIdentity.TryCanonicalize(source, out path);
    }

    private static void ValidateExistingManifest(string source)
    {
        var manifest = Path.Combine(source, ".agents", "plugins", "marketplace.json");
        if (!File.Exists(manifest)) return;
        using var stream = File.OpenRead(manifest);
        using var json = JsonDocument.Parse(stream);
        if (!json.RootElement.TryGetProperty("name", out var name) || name.GetString() != MarketplaceName)
            throw new InvalidOperationException("隔离目录中的运行时插件市场标识不正确，未修改配置。");
    }
}
