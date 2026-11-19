using System.Text.Json;

namespace CodexMultiLauncher;

public sealed record SandboxPreparation(bool Changed, string? BackupPath);

/// <summary>
/// Managed homes use the documented restricted-token sandbox. Elevated sandbox users
/// are host-global, so independent homes must not each rotate their credentials.
/// Only the official config writer may edit TOML. No setup markers or credentials are copied.
/// </summary>
public static class WorkspaceSandboxService
{
    public const string CompatibleMode = "unelevated";
    public const string NewProfileConfig = "cli_auth_credentials_store = \"file\"\n\n[windows]\nsandbox = \"unelevated\"\n";
    public static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(20);

    public static async Task<SandboxPreparation> PrepareAsync(CodexWorkspace workspace, CancellationToken token = default)
    {
        if (workspace.Client != ClientKind.Codex || workspace.Kind != CodexWorkspaceKind.Managed)
            throw new InvalidOperationException("沙箱迁移只允许用于启动器管理的账号。");
        var home = workspace.DataPaths.RequireCodexHome();
        var configPath = Path.Combine(home, "config.toml");
        ProfilePathSafety.ThrowIfReparsePath(configPath);
        using var timeout = new CancellationTokenSource(PreparationTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            await using var session = await CodexAppServerSession.StartAsync(workspace.DataPaths, linked.Token).ConfigureAwait(false);
            using var requirements = await session.RequestAsync("configRequirements/read", null, linked.Token).ConfigureAwait(false);
            RequireCompatibleModeAllowed(requirements.RootElement.GetProperty("result"));
            using var read = await session.RequestAsync("config/read", new { includeLayers = true }, linked.Token).ConfigureAwait(false);
            var result = read.RootElement.GetProperty("result");
            var user = GetUserLayer(result, configPath);
            if (IsCompatible(result.GetProperty("config")) && IsCompatible(user.GetProperty("config"))) return new(false, null);
            var version = user.GetProperty("version").GetString();
            if (string.IsNullOrEmpty(version)) throw new InvalidOperationException("官方配置未返回版本，无法安全迁移。");
            // Keep the exact prior bytes; the official writer enforces optimistic concurrency.
            var backup = configPath + $".before-multi-sandbox-{Guid.NewGuid():N}.bak";
            File.Copy(configPath, backup, overwrite: false);
            using var written = await session.RequestAsync("config/value/write", CreateWriteParameters(configPath, version), linked.Token).ConfigureAwait(false);
            var write = written.RootElement.GetProperty("result");
            if (write.GetProperty("status").GetString() != "ok")
                throw new InvalidOperationException("沙箱设置被更高层配置覆盖；已停止启动，请在官方客户端检查策略。");
            using var verified = await session.RequestAsync("config/read", new { includeLayers = true }, linked.Token).ConfigureAwait(false);
            if (!IsCompatible(verified.RootElement.GetProperty("result").GetProperty("config")))
                throw new InvalidOperationException("官方客户端未确认多开沙箱生效，已停止启动。");
            return new(true, backup);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        { throw new TimeoutException("准备多开沙箱超时；未启动新的客户端。"); }
    }

    public static object CreateWriteParameters(string configPath, string version) => new
    { keyPath = "windows.sandbox", value = CompatibleMode, mergeStrategy = "replace", filePath = Path.GetFullPath(configPath), expectedVersion = version };

    public static bool IsCompatible(JsonElement config) => config.ValueKind == JsonValueKind.Object
        && config.TryGetProperty("windows", out var windows) && windows.ValueKind == JsonValueKind.Object
        && windows.TryGetProperty("sandbox", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == CompatibleMode;

    public static void RequireCompatibleModeAllowed(JsonElement result)
    {
        if (!result.TryGetProperty("requirements", out var requirements))
            throw new InvalidOperationException("官方客户端未返回配置策略，无法确认沙箱兼容性。");
        if (requirements.ValueKind == JsonValueKind.Null) return;
        if (requirements.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("官方配置策略格式无效。");
        if (!requirements.TryGetProperty("allowedWindowsSandboxImplementations", out var allowed) || allowed.ValueKind == JsonValueKind.Null) return;
        if (allowed.ValueKind != JsonValueKind.Array || !allowed.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == CompatibleMode))
            throw new InvalidOperationException("此设备策略不允许兼容沙箱，未修改配置。请在独立 Windows 用户中运行其他账号。");
    }

    public static JsonElement GetUserLayer(JsonElement result, string configPath)
    {
        if (result.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
            foreach (var layer in layers.EnumerateArray())
                if (layer.TryGetProperty("name", out var name) && name.TryGetProperty("type", out var type) && type.GetString() == "user"
                    && name.TryGetProperty("file", out var file) && PathIdentity.TryCanonicalize(file.GetString(), out var path)
                    && PathIdentity.Equals(path, Path.GetFullPath(configPath))) return layer;
        throw new InvalidOperationException("无法验证受管账号的配置层，已停止配置迁移。");
    }
}
