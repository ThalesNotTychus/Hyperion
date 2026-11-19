using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexMultiLauncher;

public sealed record ReasoningEffortOption(
    string Id,
    string Label,
    string? Description = null,
    string? ResolvedModelId = null);

public sealed record SpeedTierOption(
    string Id,
    string Label,
    string? Description = null);

public sealed record ModelCatalogEntry(
    string Id,
    string DisplayName,
    string Group,
    string? Description,
    string? DefaultEffort,
    IReadOnlyList<ReasoningEffortOption> ReasoningEfforts,
    IReadOnlyList<SpeedTierOption> SpeedTiers,
    bool IsDefault = false,
    string? Badge = null,
    double? RemainingQuotaPercent = null);

public sealed record WorkspaceModelCatalog(
    string WorkspaceId,
    string ProviderKind,
    string Source,
    string? DefaultModelId,
    string? DefaultEffort,
    string? DefaultSpeedTier,
    bool SupportsCustomModelInput,
    IReadOnlyList<ModelCatalogEntry> Models,
    DateTimeOffset FetchedAtUtc);

/// <summary>
/// Universal dynamic runtime model, reasoning-effort, and speed-tier discovery service across:
/// - OpenAI Codex (`codex app-server` `model/list` JSON-RPC + `models_cache.json` + `config.toml`)
/// - Google Antigravity (Official `agy acp` `session/new` `configOptions` + CLI settings)
/// - Anthropic Claude (`CLAUDE_CONFIG_DIR/settings.json` + `/v1/models` + Claude Code CLI aliases)
/// - Direct API (`GET /v1/models`, OpenRouter `/api/v1/models`, Anthropic `/v1/models`, Gemini `/v1beta/models`, Ollama `/api/tags`)
/// - Lightweight Market CLIs (`gemini-cli`, `opencode`, `aider`, `qwen-code`, etc.)
/// </summary>
public sealed partial class ModelCatalogService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(3);
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    private static readonly string[] EffortSortOrder = ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];

    private readonly AgentProviderStore _agentProviders;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, WorkspaceModelCatalog> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _antigravityVariantLookup = new(StringComparer.OrdinalIgnoreCase);

    public ModelCatalogService(
        AgentProviderStore agentProviders,
        HttpClient? httpClient = null)
    {
        _agentProviders = agentProviders;
        _http = httpClient ?? SharedHttp;
    }

    public WorkspaceModelCatalog? TryGetCached(string workspaceId) =>
        _cache.TryGetValue(workspaceId, out var cat) ? cat : null;

    public async Task<WorkspaceModelCatalog> GetCatalogAsync(
        CodexWorkspace workspace,
        bool forceRefresh = false,
        bool wakeDaemonIfNeeded = false,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!forceRefresh && _cache.TryGetValue(workspace.Id, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAtUtc <= CacheLifetime &&
            cached.Source != "preset-fallback")
        {
            return cached;
        }

        var catalog = workspace.Client switch
        {
            ClientKind.Codex => await DiscoverCodexCatalogAsync(workspace.Id, workspace.DataPaths, forceRefresh, token).ConfigureAwait(false),
            ClientKind.Antigravity => await DiscoverAntigravityCatalogAsync(workspace.Id, AntigravityAgentRunner.GetHomeForWorkspace(workspace), wakeDaemonIfNeeded, token).ConfigureAwait(false),
            ClientKind.Claude => await DiscoverClaudeCatalogAsync(workspace.Id, workspace.ClaudeHome, null, null, token).ConfigureAwait(false),
            ClientKind.DirectApi => await DiscoverDirectApiCatalogAsync(workspace, token).ConfigureAwait(false),
            ClientKind.MarketCli => await DiscoverMarketCliCatalogAsync(workspace, forceRefresh, wakeDaemonIfNeeded, token).ConfigureAwait(false),
            _ => BuildFallbackCodexCatalog(workspace.Id, null, null, null),
        };

        _cache[workspace.Id] = catalog;
        IndexAntigravityVariants(catalog);
        return catalog;
    }

    /// <summary>
    /// Discovers models for an unsaved or edited Direct API draft inside the Add/Edit Agent modal.
    /// </summary>
    public async Task<WorkspaceModelCatalog> ProbeDirectApiDraftAsync(UniversalAgentRecord draftRecord, CancellationToken token = default)
    {
        var apiKey = DpapiSecretProtector.Unprotect(draftRecord.EncryptedApiKey);
        return await FetchRemoteApiModelsAsync("draft:" + draftRecord.PresetId, draftRecord.PresetId, draftRecord.Protocol, draftRecord.BaseUrl, draftRecord.Model, apiKey, token).ConfigureAwait(false);
    }

    public string FormatModelSummary(CodexWorkspace workspace, GroupChatAgent agent)
    {
        _cache.TryGetValue(workspace.Id, out var catalog);
        return FormatModelSummaryCore(catalog, workspace, agent);
    }

    public static string FormatModelSummaryCore(WorkspaceModelCatalog? catalog, CodexWorkspace workspace, GroupChatAgent agent)
    {
        var rawModel = !string.IsNullOrWhiteSpace(agent.Model)
            ? agent.Model!
            : (!string.IsNullOrWhiteSpace(catalog?.DefaultModelId) ? catalog!.DefaultModelId! : workspace.DefaultModel);

        string? modelLabel = null;
        ModelCatalogEntry? matchedEntry = null;
        if (catalog is not null && !string.IsNullOrWhiteSpace(rawModel))
        {
            matchedEntry = catalog.Models.FirstOrDefault(m =>
                string.Equals(m.Id, rawModel, StringComparison.OrdinalIgnoreCase) ||
                m.ReasoningEfforts.Any(e => string.Equals(e.ResolvedModelId, rawModel, StringComparison.OrdinalIgnoreCase)));
            modelLabel = matchedEntry?.DisplayName;
        }

        modelLabel ??= !string.IsNullOrWhiteSpace(rawModel) ? rawModel : workspace.Client.DisplayName();

        var effort = !string.IsNullOrWhiteSpace(agent.ReasoningEffort)
            ? agent.ReasoningEffort
            : matchedEntry?.DefaultEffort ?? catalog?.DefaultEffort;

        var parts = new List<string> { modelLabel! };
        if (!string.IsNullOrWhiteSpace(effort))
            parts.Add(FormatEffortLabel(effort!));

        if (!string.IsNullOrWhiteSpace(agent.ServiceTier) &&
            !string.Equals(agent.ServiceTier, "default", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(agent.ServiceTier, "standard", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(string.Equals(agent.ServiceTier, "ultrafast", StringComparison.OrdinalIgnoreCase) ? "⚡⚡Ultrafast" : "⚡Fast");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Resolves `(model, reasoningEffort, serviceTier)` to the normalized `--model` identifier for `agy` CLI.
    /// </summary>
    public static string? ResolveAntigravityModelArgument(
        string? model,
        string? reasoningEffort,
        string? serviceTier,
        WorkspaceModelCatalog? catalog = null,
        IReadOnlyDictionary<string, string>? variantLookup = null)
    {
        var cleanModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        var cleanEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort.Trim().ToLowerInvariant();
        var cleanTier = string.IsNullOrWhiteSpace(serviceTier) ? null : serviceTier.Trim().ToLowerInvariant();

        // Legacy placeholder "gemini-2.5-pro" from older MarketCli presets wasn't pinned to a user choice
        if (string.Equals(cleanModel, "gemini-2.5-pro", StringComparison.OrdinalIgnoreCase))
            cleanModel = null;

        if (cleanModel is null)
        {
            if (cleanTier is "fast" or "priority") return "flash";
            if (cleanTier is "ultrafast" or "flash_lite") return "flash_lite";
            return null;
        }

        if (cleanModel is "pro" or "flash" or "flash_lite")
            return cleanModel;

        if (catalog is not null)
        {
            var entry = catalog.Models.FirstOrDefault(m =>
                string.Equals(m.Id, cleanModel, StringComparison.OrdinalIgnoreCase) ||
                m.ReasoningEfforts.Any(r => string.Equals(r.ResolvedModelId, cleanModel, StringComparison.OrdinalIgnoreCase)));

            if (entry is not null)
            {
                if (entry.ReasoningEfforts.Count == 0)
                    return entry.Id;

                var targetEffort = cleanEffort ?? entry.DefaultEffort ?? "high";
                var exact = entry.ReasoningEfforts.FirstOrDefault(r => string.Equals(r.Id, targetEffort, StringComparison.OrdinalIgnoreCase))
                    ?? entry.ReasoningEfforts.FirstOrDefault(r => string.Equals(r.Id, entry.DefaultEffort, StringComparison.OrdinalIgnoreCase))
                    ?? entry.ReasoningEfforts[0];
                if (!string.IsNullOrWhiteSpace(exact.ResolvedModelId))
                    return exact.ResolvedModelId;
            }
        }

        if (variantLookup is not null)
        {
            var baseKey = StripEffortSuffix(cleanModel);
            var effortKey = cleanEffort ?? ExtractEffortSuffix(cleanModel) ?? "high";
            if (variantLookup.TryGetValue($"{baseKey}::{effortKey}", out var mapped))
                return mapped;
            if (variantLookup.TryGetValue($"{baseKey}::high", out mapped))
                return mapped;
            if (variantLookup.TryGetValue($"{baseKey}::medium", out mapped))
                return mapped;
        }

        if (!string.IsNullOrWhiteSpace(cleanEffort))
        {
            var stripped = StripEffortSuffix(cleanModel);
            // If model looks like a multi-effort family slug (e.g. gemini-4-neon, claude-opus-5-5, gemini-3.8-flash)
            if (!cleanModel.Contains("orchestrator", StringComparison.OrdinalIgnoreCase))
                return $"{stripped}-{cleanEffort}";
        }

        return cleanModel;
    }

    public string? ResolveAntigravityForWorkspace(string workspaceId, string? model, string? reasoningEffort, string? serviceTier)
    {
        _cache.TryGetValue(workspaceId, out var cat);
        _antigravityVariantLookup.TryGetValue(workspaceId, out var lookup);
        return ResolveAntigravityModelArgument(model, reasoningEffort, serviceTier, cat, lookup);
    }

    private void IndexAntigravityVariants(WorkspaceModelCatalog catalog)
    {
        if (!string.Equals(catalog.ProviderKind, "antigravity", StringComparison.OrdinalIgnoreCase))
            return;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in catalog.Models)
        {
            foreach (var eff in m.ReasoningEfforts)
            {
                if (!string.IsNullOrWhiteSpace(eff.ResolvedModelId))
                    map[$"{m.Id}::{eff.Id}"] = eff.ResolvedModelId!;
            }
        }
        if (map.Count > 0)
            _antigravityVariantLookup[catalog.WorkspaceId] = map;
    }

    // =========================================================================
    // 1. OpenAI Codex Discovery (`app-server` `model/list` + `models_cache.json` + `config.toml`)
    // =========================================================================
    private static async Task<WorkspaceModelCatalog> DiscoverCodexCatalogAsync(
        string workspaceId,
        WorkspaceDataPaths paths,
        bool forceRefresh,
        CancellationToken token)
    {
        string? codexHome = paths.CodexHome;
        var (cfgModel, cfgEffort, cfgTier) = ReadCodexConfigDefaults(codexHome);

        // If not forcing live RPC and models_cache.json exists on disk, read immediately (<2ms) for instant UI responsiveness;
        // if forcing refresh or models_cache.json is missing, query live `codex app-server` `model/list`!
        var cacheFile = !string.IsNullOrWhiteSpace(codexHome) ? Path.Combine(codexHome, "models_cache.json") : null;
        if (!forceRefresh && cacheFile is not null && File.Exists(cacheFile))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(cacheFile, token).ConfigureAwait(false);
                var parsed = ParseCodexModelsCacheJson(workspaceId, raw, cfgModel, cfgEffort, cfgTier);
                if (parsed.Models.Count > 0)
                    return parsed;
            }
            catch
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(codexHome) && Directory.Exists(codexHome))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                await using var session = await CodexAppServerSession.StartAsync(paths, cts.Token).ConfigureAwait(false);
                using var doc = await session.RequestAsync("model/list", new { }, cts.Token).ConfigureAwait(false);
                var live = ParseCodexAppServerModelList(workspaceId, doc.RootElement, cfgModel, cfgEffort, cfgTier);
                if (live.Models.Count > 0)
                    return live;
            }
            catch
            {
            }

            if (cacheFile is not null && File.Exists(cacheFile))
            {
                try
                {
                    var raw = await File.ReadAllTextAsync(cacheFile, token).ConfigureAwait(false);
                    var parsed = ParseCodexModelsCacheJson(workspaceId, raw, cfgModel, cfgEffort, cfgTier);
                    if (parsed.Models.Count > 0)
                        return parsed;
                }
                catch
                {
                }
            }
        }

        return BuildFallbackCodexCatalog(workspaceId, cfgModel, cfgEffort, cfgTier);
    }

    internal static (string? Model, string? Effort, string? ServiceTier) ReadCodexConfigDefaults(string? codexHome)
    {
        if (string.IsNullOrWhiteSpace(codexHome)) return (null, null, null);
        var tomlPath = Path.Combine(codexHome, "config.toml");
        if (!File.Exists(tomlPath)) return (null, null, null);

        string? model = null, effort = null, tier = null;
        try
        {
            foreach (var rawLine in File.ReadLines(tomlPath))
            {
                var line = rawLine.Trim();
                if (line.StartsWith('[')) break; // Stop after top-level keys
                if (line.StartsWith('#') || !line.Contains('=')) continue;
                var idx = line.IndexOf('=');
                var key = line[..idx].Trim();
                var val = line[(idx + 1)..].Trim().Trim('"', '\'');
                if (string.IsNullOrWhiteSpace(val)) continue;
                if (key == "model") model = val;
                else if (key == "model_reasoning_effort") effort = val.ToLowerInvariant();
                else if (key == "service_tier") tier = val.ToLowerInvariant();
            }
        }
        catch
        {
        }
        return (model, effort, tier);
    }

    internal static WorkspaceModelCatalog ParseCodexAppServerModelList(
        string workspaceId,
        JsonElement responseRoot,
        string? cfgModel,
        string? cfgEffort,
        string? cfgTier)
    {
        var result = responseRoot.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? r : responseRoot;
        var dataArr = result.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array
            ? d
            : (result.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array ? m : default);

        var entries = new List<ModelCatalogEntry>();
        string? serverDefaultId = null;

        if (dataArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataArr.EnumerateArray())
            {
                var id = GetJsonString(item, "id") ?? GetJsonString(item, "model") ?? GetJsonString(item, "slug");
                if (string.IsNullOrWhiteSpace(id)) continue;

                var hidden = item.TryGetProperty("hidden", out var hp) && hp.ValueKind == JsonValueKind.True;
                if (hidden && !string.Equals(id, cfgModel, StringComparison.OrdinalIgnoreCase))
                    continue;

                var displayName = GetJsonString(item, "displayName") ?? GetJsonString(item, "display_name") ?? id!;
                var desc = GetJsonString(item, "description");
                var defEffort = (GetJsonString(item, "defaultReasoningEffort") ?? GetJsonString(item, "default_reasoning_level"))?.ToLowerInvariant();
                var isDef = item.TryGetProperty("isDefault", out var idp) && idp.ValueKind == JsonValueKind.True;
                if (isDef && serverDefaultId is null) serverDefaultId = id;

                var efforts = ParseCodexEfforts(item);
                var tiers = ParseCodexServiceTiers(item);
                var group = CategorizeOpenAiGroup(id!, desc);
                var badge = tiers.Any(t => t.Id == "ultrafast") ? "⚡ Ultrafast" : (isDef ? "推荐" : null);

                entries.Add(new ModelCatalogEntry(
                    id!,
                    displayName,
                    group,
                    desc,
                    defEffort,
                    efforts,
                    tiers,
                    IsDefault: isDef,
                    Badge: badge));
            }
        }

        var chosenModel = !string.IsNullOrWhiteSpace(cfgModel) && entries.Any(e => string.Equals(e.Id, cfgModel, StringComparison.OrdinalIgnoreCase))
            ? cfgModel
            : (serverDefaultId ?? entries.FirstOrDefault()?.Id);
        var chosenEffort = cfgEffort ?? entries.FirstOrDefault(e => e.Id == chosenModel)?.DefaultEffort ?? "medium";
        var normTier = cfgTier is "default" or "auto" or "" ? null : cfgTier;

        return new WorkspaceModelCatalog(
            workspaceId,
            "codex",
            "live-rpc",
            chosenModel,
            chosenEffort,
            normTier,
            SupportsCustomModelInput: true,
            entries,
            DateTimeOffset.UtcNow);
    }

    internal static WorkspaceModelCatalog ParseCodexModelsCacheJson(
        string workspaceId,
        string rawJson,
        string? cfgModel,
        string? cfgEffort,
        string? cfgTier)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var entries = new List<ModelCatalogEntry>();

        if (root.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var idx = 0;
            foreach (var item in arr.EnumerateArray())
            {
                var slug = GetJsonString(item, "slug") ?? GetJsonString(item, "id");
                if (string.IsNullOrWhiteSpace(slug)) continue;

                var visibility = GetJsonString(item, "visibility");
                if (string.Equals(visibility, "hide", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(slug, cfgModel, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var displayName = GetJsonString(item, "display_name") ?? GetJsonString(item, "displayName") ?? slug!;
                var desc = GetJsonString(item, "description");
                var defEffort = GetJsonString(item, "default_reasoning_level")?.ToLowerInvariant();
                var efforts = ParseCodexEfforts(item);
                var tiers = ParseCodexServiceTiers(item);
                var isFirst = idx == 0;
                idx++;

                entries.Add(new ModelCatalogEntry(
                    slug!,
                    displayName,
                    CategorizeOpenAiGroup(slug!, desc),
                    desc,
                    defEffort,
                    efforts,
                    tiers,
                    IsDefault: isFirst,
                    Badge: tiers.Any(t => t.Id == "ultrafast") ? "⚡ Ultrafast" : null));
            }
        }

        var chosenModel = !string.IsNullOrWhiteSpace(cfgModel) && entries.Any(e => string.Equals(e.Id, cfgModel, StringComparison.OrdinalIgnoreCase))
            ? cfgModel
            : entries.FirstOrDefault()?.Id;
        var chosenEffort = cfgEffort ?? entries.FirstOrDefault(e => e.Id == chosenModel)?.DefaultEffort ?? "medium";
        var normTier = cfgTier is "default" or "auto" or "" ? null : cfgTier;

        return new WorkspaceModelCatalog(
            workspaceId,
            "codex",
            "local-cache",
            chosenModel,
            chosenEffort,
            normTier,
            SupportsCustomModelInput: true,
            entries,
            DateTimeOffset.UtcNow);
    }

    private static List<ReasoningEffortOption> ParseCodexEfforts(JsonElement item)
    {
        var list = new List<ReasoningEffortOption>();
        var propName = item.TryGetProperty("supportedReasoningEfforts", out var sre) && sre.ValueKind == JsonValueKind.Array
            ? "supportedReasoningEfforts"
            : (item.TryGetProperty("supported_reasoning_levels", out _) ? "supported_reasoning_levels" : null);

        if (propName is not null && item.TryGetProperty(propName, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                var eff = (GetJsonString(el, "reasoningEffort") ?? GetJsonString(el, "effort"))?.ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(eff)) continue;
                var desc = GetJsonString(el, "description");
                list.Add(new ReasoningEffortOption(eff!, FormatEffortLabel(eff!), desc));
            }
        }

        if (list.Count == 0)
        {
            foreach (var fallback in new[] { "low", "medium", "high", "xhigh" })
                list.Add(new ReasoningEffortOption(fallback, FormatEffortLabel(fallback)));
        }
        return list;
    }

    private static List<SpeedTierOption> ParseCodexServiceTiers(JsonElement item)
    {
        var tiers = new List<SpeedTierOption>();
        var propName = item.TryGetProperty("serviceTiers", out var st) && st.ValueKind == JsonValueKind.Array
            ? "serviceTiers"
            : (item.TryGetProperty("service_tiers", out _) ? "service_tiers" : null);

        if (propName is not null && item.TryGetProperty(propName, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                var id = GetJsonString(el, "id")?.ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var name = GetJsonString(el, "name") ?? (id == "ultrafast" ? "Ultrafast" : "Fast");
                var desc = GetJsonString(el, "description");
                tiers.Add(new SpeedTierOption(id!, name, desc));
            }
        }

        if (tiers.Count == 0)
        {
            tiers.Add(new SpeedTierOption("priority", "Fast", "Priority high-speed inference"));
        }
        return tiers;
    }

    private static WorkspaceModelCatalog BuildFallbackCodexCatalog(string workspaceId, string? cfgModel, string? cfgEffort, string? cfgTier)
    {
        ReasoningEffortOption[] stdEfforts =
        [
            new("low", "Low", "Fast responses with lighter reasoning"),
            new("medium", "Medium", "Balanced speed and reasoning depth"),
            new("high", "High", "Greater reasoning depth for complex problems"),
            new("xhigh", "XHigh", "Extra high reasoning depth"),
        ];
        SpeedTierOption[] stdTiers = [new("priority", "Fast", "Priority processing (~2x faster)")];

        var models = new List<ModelCatalogEntry>();
        if (!string.IsNullOrWhiteSpace(cfgModel))
        {
            models.Add(new ModelCatalogEntry(cfgModel!, cfgModel!, "Configured", "当前配置模型", cfgEffort ?? "medium", stdEfforts, stdTiers, IsDefault: true));
        }
        models.AddRange([
            new ModelCatalogEntry("gpt-5.4", "GPT-5.4", "GPT / Codex", "Flagship agentic coding & reasoning model", "medium", stdEfforts, stdTiers, IsDefault: models.Count == 0),
            new ModelCatalogEntry("gpt-5.3-codex", "GPT-5.3-Codex", "GPT / Codex", "Optimized software engineering model", "medium", stdEfforts, stdTiers),
            new ModelCatalogEntry("o3", "o3", "GPT / Codex", "Deep reasoning model", "high", stdEfforts, stdTiers),
            new ModelCatalogEntry("o4-mini", "o4-mini", "GPT / Codex", "Fast lightweight reasoning model", "medium", stdEfforts, stdTiers, Badge: "⚡ Fast"),
        ]);

        return new WorkspaceModelCatalog(
            workspaceId,
            "codex",
            "preset-fallback",
            cfgModel ?? models[0].Id,
            cfgEffort ?? "medium",
            cfgTier is "default" or "auto" or "" ? null : cfgTier,
            SupportsCustomModelInput: true,
            models,
            DateTimeOffset.UtcNow);
    }

    // =========================================================================
    // 2. Google Antigravity Discovery (Official `agy acp` `session/new` `configOptions` + CLI settings)
    // =========================================================================
    private static async Task<WorkspaceModelCatalog> DiscoverAntigravityCatalogAsync(
        string workspaceId,
        string homeRoot,
        bool probeLiveAcp,
        CancellationToken token)
    {
        _ = probeLiveAcp; // `agy acp` is a windowless stdio CLI subprocess, safe to probe directly without waking any Electron UI
        string? configuredModel = null;
        string? configuredEffort = null;
        try
        {
            var cliSettingsPath = Path.Combine(Path.GetFullPath(homeRoot), ".gemini", "antigravity-cli", "settings.json");
            if (File.Exists(cliSettingsPath))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(cliSettingsPath, token).ConfigureAwait(false));
                configuredModel = GetJsonString(doc.RootElement, "defaultModel") ?? GetJsonString(doc.RootElement, "model");
                configuredEffort = GetJsonString(doc.RootElement, "effort")?.ToLowerInvariant();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        if (AntigravityAgentRunner.TryFindAgyCliExecutable() is not null)
        {
            try
            {
                using var acpDoc = await AntigravityAgentRunner.ProbeAcpSessionNewAsync(homeRoot, Path.GetFullPath(homeRoot), token).ConfigureAwait(false);
                if (acpDoc is not null)
                {
                    var parsed = ParseAntigravityAcpSessionNewJson(workspaceId, acpDoc.RootElement.GetRawText(), configuredModel, configuredEffort);
                    if (parsed.Models.Count > 0)
                        return parsed;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        ReasoningEffortOption[] xhighEfforts =
        [
            new("low", "Low", "快速轻量思考"),
            new("medium", "Medium", "标准均衡推理"),
            new("high", "High", "深度复杂逻辑推理"),
            new("xhigh", "Extra", "最高强度深度推理"),
        ];
        ReasoningEffortOption[] stdAgyEfforts =
        [
            new("low", "Low", "快速轻量思考"),
            new("medium", "Medium", "标准均衡推理"),
            new("high", "High", "深度复杂逻辑推理"),
        ];

        var fallback = new List<ModelCatalogEntry>
        {
            new("gemini-4-carbon", "Gemini 4 Carbon", "Google Gemini 4", "旗舰深度架构推理与复杂多文件排障模型", configuredEffort ?? "high", stdAgyEfforts, [], IsDefault: string.IsNullOrWhiteSpace(configuredModel), Badge: "New"),
            new("gemini-4-neon", "Gemini 4 Neon", "Google Gemini 4", "高判断力生产级研发与多子智能体编排主力", "medium", xhighEfforts, [], Badge: "New"),
            new("gemini-4-argon", "Gemini 4 Argon", "Google Gemini 4", "长程系统规划与跨模块工程推理模型", "medium", stdAgyEfforts, [], Badge: "New"),
            new("gemini-4-hydrogen", "Gemini 4 Hydrogen", "Google Gemini 4", "高速低耗能日常编码与并行执行引擎", "medium", xhighEfforts, [], Badge: "New"),
            new("gemini-pro-orchestrator-neon", "Orchestrator (Neon)", "Orchestrator 多模编排", "自动协同 Neon 规划与 Hydrogen 高速执行以节省配额", null, [], [], Badge: "New"),
            new("gemini-pro-orchestrator", "Orchestrator (Carbon)", "Orchestrator 多模编排", "自动协同 Carbon 深度架构与并行执行子智能体", null, [], [], Badge: "New"),
            new("gemini-pro-orchestrator-argon", "Orchestrator (Argon)", "Orchestrator 多模编排", "自动协同 Argon 系统设计与高效验证链路", null, [], [], Badge: "New"),
            new("gemini-3.8-flash", "Gemini 3.8 Flash", "Google Gemini", "高吞吐低延迟日常工程主力模型", "medium", stdAgyEfforts, [], Badge: "⚡ Flash"),
            new("claude-opus-5-5", "Claude Opus 5.5", "Anthropic Claude", "Antigravity 内置 Claude Opus 深度推理通道", "medium", stdAgyEfforts, [], Badge: "New"),
            new("claude-sonnet-5-5", "Claude Sonnet 5.5", "Anthropic Claude", "Antigravity 内置 Claude Sonnet 高效编码通道", "medium", stdAgyEfforts, [], Badge: "New"),
            new("gpt-oss-120b-medium", "GPT-OSS 120B (Medium)", "Open Models", "Antigravity 托管 120B 开源推理模型", null, [], [], Badge: "Notice"),
        };
        EnsureConfiguredModelPresent(fallback, configuredModel, id => new ModelCatalogEntry(id, id, "Configured", "当前 CLI 配置模型", configuredEffort ?? "high", stdAgyEfforts, [], IsDefault: true));

        return new WorkspaceModelCatalog(
            workspaceId,
            "antigravity",
            "acp-preset",
            configuredModel ?? "gemini-4-carbon",
            configuredEffort ?? "high",
            null,
            SupportsCustomModelInput: true,
            fallback,
            DateTimeOffset.UtcNow);
    }

    internal static WorkspaceModelCatalog ParseAntigravityAcpSessionNewJson(
        string workspaceId,
        string rawJson,
        string? configuredModel = null,
        string? configuredEffort = null)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var entries = new List<ModelCatalogEntry>();
        var efforts = new List<ReasoningEffortOption>();
        string? currentModel = configuredModel;
        string? currentEffort = configuredEffort;

        // Optional per-model effort map extracted from `models.availableModels` (`agy acp` v1.5+)
        var availableEffortsByBase = new Dictionary<string, List<ReasoningEffortOption>>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("models", out var modelsObj) && modelsObj.ValueKind == JsonValueKind.Object &&
            modelsObj.TryGetProperty("availableModels", out var availArr) && availArr.ValueKind == JsonValueKind.Array)
        {
            var rawVariants = new List<(string ModelId, string BaseId, string Effort)>();
            foreach (var av in availArr.EnumerateArray())
            {
                var mid = GetJsonString(av, "modelId") ?? GetJsonString(av, "id");
                if (string.IsNullOrWhiteSpace(mid) || mid!.Contains("orchestrator", StringComparison.OrdinalIgnoreCase)) continue;
                var eff = ExtractEffortSuffix(mid!);
                if (eff is null) continue;
                rawVariants.Add((mid!, StripEffortSuffix(mid!), eff));
            }
            foreach (var grp in rawVariants.GroupBy(x => x.BaseId, StringComparer.OrdinalIgnoreCase))
            {
                if (grp.Count() <= 1) continue;
                var ordered = grp
                    .OrderBy(x => Array.IndexOf(EffortSortOrder, x.Effort))
                    .Select(x => new ReasoningEffortOption(x.Effort, FormatEffortLabel(x.Effort), null, x.ModelId))
                    .ToList();
                availableEffortsByBase[grp.Key] = ordered;
            }
        }

        var hasExplicitEffortConfig = false;
        if (root.TryGetProperty("configOptions", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            // Pass 1: Extract advertised effort/thinking options (`thought_level` in `agy acp` v1.5+, or `effort` / `thinking`)
            foreach (var opt in opts.EnumerateArray())
            {
                var cid = GetJsonString(opt, "configId") ?? GetJsonString(opt, "id") ?? string.Empty;
                var cat = GetJsonString(opt, "category") ?? cid;
                if (string.Equals(cat, "thought_level", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cat, "effort", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cat, "thinking", StringComparison.OrdinalIgnoreCase) ||
                    cid.Contains("thought", StringComparison.OrdinalIgnoreCase) ||
                    cid.Contains("effort", StringComparison.OrdinalIgnoreCase))
                {
                    hasExplicitEffortConfig = true;
                    currentEffort ??= GetJsonString(opt, "currentValue")?.ToLowerInvariant();
                    if (opt.TryGetProperty("options", out var effArr) && effArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var eItem in effArr.EnumerateArray())
                        {
                            var val = (GetJsonString(eItem, "value") ?? GetJsonString(eItem, "id"))?.ToLowerInvariant();
                            if (string.IsNullOrWhiteSpace(val)) continue;
                            var name = GetJsonString(eItem, "name") ?? FormatEffortLabel(val!);
                            var desc = GetJsonString(eItem, "description");
                            efforts.Add(new ReasoningEffortOption(val!, name, desc));
                        }
                    }
                }
            }

            if (efforts.Count == 0)
            {
                efforts.AddRange([
                    new("low", "Low", "快速轻量思考"),
                    new("medium", "Medium", "标准均衡推理"),
                    new("high", "High", "深度复杂逻辑推理"),
                    new("max", "Max", "最高深度推理"),
                ]);
            }

            // Pass 2: Extract advertised model options (supporting both flat model+effort configOptions and effort-suffixed model variants)
            foreach (var opt in opts.EnumerateArray())
            {
                var cid = GetJsonString(opt, "configId") ?? GetJsonString(opt, "id") ?? string.Empty;
                var cat = GetJsonString(opt, "category") ?? cid;
                if (!string.Equals(cat, "model", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(cid, "model", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                currentModel ??= GetJsonString(opt, "currentValue");
                if (opt.TryGetProperty("options", out var modArr) && modArr.ValueKind == JsonValueKind.Array)
                {
                    var rawItems = new List<(string Val, string Label, string? Desc, string BaseId, string? SuffixEffort)>();
                    foreach (var mItem in modArr.EnumerateArray())
                    {
                        var val = GetJsonString(mItem, "value") ?? GetJsonString(mItem, "id");
                        if (string.IsNullOrWhiteSpace(val)) continue;
                        var label = GetJsonString(mItem, "name") ?? val!;
                        var desc = GetJsonString(mItem, "description");
                        var suffixEff = !hasExplicitEffortConfig && !val!.Contains("orchestrator", StringComparison.OrdinalIgnoreCase)
                            ? ExtractEffortSuffix(val)
                            : null;
                        var baseId = suffixEff is not null ? StripEffortSuffix(val!) : val!;
                        rawItems.Add((val!, label, desc, baseId, suffixEff));
                    }

                    var baseCounts = rawItems
                        .Where(x => x.SuffixEffort is not null)
                        .GroupBy(x => x.BaseId, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

                    var emittedFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in rawItems)
                    {
                        if (item.SuffixEffort is not null && baseCounts.TryGetValue(item.BaseId, out var cnt) && cnt > 1)
                        {
                            if (!emittedFamilies.Add(item.BaseId)) continue;
                            var siblings = rawItems
                                .Where(x => string.Equals(x.BaseId, item.BaseId, StringComparison.OrdinalIgnoreCase) && x.SuffixEffort is not null)
                                .OrderBy(x => Array.IndexOf(EffortSortOrder, x.SuffixEffort!))
                                .ToArray();
                            var familyEfforts = siblings
                                .Select(s => new ReasoningEffortOption(s.SuffixEffort!, FormatEffortLabel(s.SuffixEffort!), s.Desc, s.Val))
                                .ToList();
                            var cleanLabel = StripTrailingEffortDisplayLabel(item.Label);
                            var matchedCurrent = siblings.FirstOrDefault(s => string.Equals(s.Val, currentModel, StringComparison.OrdinalIgnoreCase));
                            if (matchedCurrent.Val is not null)
                            {
                                currentModel = item.BaseId;
                                currentEffort ??= matchedCurrent.SuffixEffort;
                            }
                            var defEff = matchedCurrent.SuffixEffort
                                ?? (familyEfforts.Any(e => e.Id == "high") ? "high" : familyEfforts.FirstOrDefault()?.Id);
                            var isFlash = item.BaseId.Contains("flash", StringComparison.OrdinalIgnoreCase);
                            entries.Add(new ModelCatalogEntry(
                                item.BaseId,
                                cleanLabel,
                                CategorizeAntigravityGroup(cleanLabel),
                                item.Desc,
                                defEff,
                                familyEfforts,
                                [],
                                IsDefault: string.Equals(item.BaseId, currentModel, StringComparison.OrdinalIgnoreCase),
                                Badge: isFlash ? "⚡ Fast" : null));
                            continue;
                        }

                        var isFlashSingle = item.Val.Contains("flash", StringComparison.OrdinalIgnoreCase);
                        var isLite = item.Val.Contains("lite", StringComparison.OrdinalIgnoreCase);
                        var hasKnownFixedTier = ExtractEffortSuffix(item.Val) is not null && !availableEffortsByBase.ContainsKey(item.Val);
                        var noSubEfforts = isLite
                            || item.Val.Contains("orchestrator", StringComparison.OrdinalIgnoreCase)
                            || hasKnownFixedTier
                            || (availableEffortsByBase.Count > 0 && !availableEffortsByBase.ContainsKey(item.Val));
                        var modelEfforts = noSubEfforts
                            ? []
                            : (availableEffortsByBase.TryGetValue(item.Val, out var specificEfforts) ? specificEfforts : efforts);
                        var defModelEff = noSubEfforts
                            ? null
                            : (modelEfforts.Any(e => string.Equals(e.Id, currentEffort, StringComparison.OrdinalIgnoreCase))
                                ? currentEffort
                                : (modelEfforts.Any(e => e.Id == "high") ? "high" : modelEfforts.FirstOrDefault()?.Id));
                        entries.Add(new ModelCatalogEntry(
                            item.Val,
                            item.Label,
                            CategorizeAntigravityGroup(item.Label),
                            item.Desc,
                            defModelEff,
                            modelEfforts,
                            [],
                            IsDefault: string.Equals(item.Val, currentModel, StringComparison.OrdinalIgnoreCase),
                            Badge: isLite ? "⚡⚡ Lite" : (isFlashSingle ? "⚡ Fast" : null)));
                    }
                }
            }
        }

        currentModel ??= entries.FirstOrDefault()?.Id ?? "pro";
        return new WorkspaceModelCatalog(
            workspaceId,
            "antigravity",
            "live-acp",
            currentModel,
            currentEffort ?? "high",
            null,
            SupportsCustomModelInput: true,
            entries,
            DateTimeOffset.UtcNow);
    }

    private static string CategorizeAntigravityGroup(string label)
    {
        if (label.Contains("Claude", StringComparison.OrdinalIgnoreCase)) return "Anthropic Claude";
        if (label.Contains("Orchestrator", StringComparison.OrdinalIgnoreCase)) return "Orchestrator 多模编排";
        if (label.Contains("Gemini 4", StringComparison.OrdinalIgnoreCase)) return "Google Gemini 4";
        if (label.Contains("Gemini", StringComparison.OrdinalIgnoreCase)) return "Google Gemini";
        return "Open Models";
    }

    private static string StripTrailingEffortDisplayLabel(string label)
    {
        var trimmed = label.Trim();
        foreach (var eff in EffortSortOrder)
        {
            var paren = $" ({eff})";
            if (trimmed.EndsWith(paren, StringComparison.OrdinalIgnoreCase))
                return trimmed[..^paren.Length].TrimEnd();
            var space = $" {eff}";
            if (trimmed.EndsWith(space, StringComparison.OrdinalIgnoreCase))
                return trimmed[..^space.Length].TrimEnd();
        }
        return trimmed;
    }

    // =========================================================================
    // 3. Anthropic Claude Discovery (`settings.json` + `/v1/models` + Evergreen Aliases)
    // =========================================================================
    private async Task<WorkspaceModelCatalog> DiscoverClaudeCatalogAsync(
        string workspaceId,
        string? claudeHome,
        string? explicitApiKey,
        string? explicitBaseUrl,
        CancellationToken token)
    {
        var configDir = !string.IsNullOrWhiteSpace(claudeHome)
            ? claudeHome!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

        string? cfgModel = null, cfgEffort = null, apiKey = explicitApiKey, baseUrl = explicitBaseUrl;
        var fastMode = false;

        var settingsPath = Path.Combine(configDir, "settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(settingsPath, token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                cfgModel = GetJsonString(root, "model");
                cfgEffort = GetJsonString(root, "effortLevel")?.ToLowerInvariant();
                if (root.TryGetProperty("fastMode", out var fm) && fm.ValueKind == JsonValueKind.True)
                    fastMode = true;
                if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
                {
                    cfgModel ??= GetJsonString(env, "ANTHROPIC_MODEL");
                    apiKey ??= GetJsonString(env, "ANTHROPIC_API_KEY") ?? GetJsonString(env, "ANTHROPIC_AUTH_TOKEN");
                    baseUrl ??= GetJsonString(env, "ANTHROPIC_BASE_URL");
                }
            }
            catch
            {
            }
        }

        ReasoningEffortOption[] claudeEfforts =
        [
            new("low", "Low", "快速轻量思考"),
            new("medium", "Medium", "均衡标准推理"),
            new("high", "High", "深度复杂规划"),
            new("max", "Max", "极限全局深度思考"),
        ];
        SpeedTierOption[] claudeSpeeds =
        [
            new("fast", "Fast", "高速推理模式 (~2.5x OTPS)"),
        ];

        var entries = new List<ModelCatalogEntry>
        {
            new("sonnet", "Claude Sonnet (自动最新主力)", "Claude 官方通道", "自动映射账号当前最新 Sonnet 主力研发模型", "high", claudeEfforts, claudeSpeeds, IsDefault: string.IsNullOrWhiteSpace(cfgModel)),
            new("opus", "Claude Opus (自动最新旗舰)", "Claude 官方通道", "自动映射账号当前最强 Opus 深度架构推理模型", "high", claudeEfforts, claudeSpeeds, Badge: "旗舰"),
            new("haiku", "Claude Haiku (自动极速版)", "Claude 官方通道", "超低延迟高吞吐轻量级模型", "low", claudeEfforts, [], Badge: "⚡ 极速"),
        };

        var source = "local-cache";
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                var remote = await FetchAnthropicModelsListAsync(baseUrl ?? "https://api.anthropic.com", apiKey!, claudeEfforts, claudeSpeeds, token).ConfigureAwait(false);
                if (remote.Count > 0)
                {
                    entries.AddRange(remote);
                    source = "live-api";
                }
            }
            catch
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(cfgModel) && !entries.Any(e => string.Equals(e.Id, cfgModel, StringComparison.OrdinalIgnoreCase)))
        {
            entries.Insert(0, new ModelCatalogEntry(cfgModel!, cfgModel!, "当前配置", "来自 settings.json 配置", cfgEffort ?? "high", claudeEfforts, claudeSpeeds, IsDefault: true));
        }

        return new WorkspaceModelCatalog(
            workspaceId,
            "claude",
            source,
            cfgModel ?? "sonnet",
            cfgEffort ?? "high",
            fastMode ? "fast" : null,
            SupportsCustomModelInput: true,
            entries,
            DateTimeOffset.UtcNow);
    }

    // =========================================================================
    // 4. Direct API Live Remote Discovery (`/v1/models`, `/v1beta/models`, `/api/tags`)
    // =========================================================================
    private async Task<WorkspaceModelCatalog> DiscoverDirectApiCatalogAsync(CodexWorkspace workspace, CancellationToken token)
    {
        var record = _agentProviders.FindById(workspace.ProviderId ?? workspace.Id);
        if (record is null)
            return BuildFallbackCodexCatalog(workspace.Id, workspace.DefaultModel, null, null);

        var apiKey = DpapiSecretProtector.Unprotect(record.EncryptedApiKey);
        return await FetchRemoteApiModelsAsync(
            workspace.Id,
            record.PresetId,
            record.Protocol,
            record.BaseUrl,
            record.Model,
            apiKey,
            token).ConfigureAwait(false);
    }

    private async Task<WorkspaceModelCatalog> FetchRemoteApiModelsAsync(
        string workspaceId,
        string presetId,
        ApiProtocolKind protocol,
        string baseUrl,
        string? configuredModel,
        string? apiKey,
        CancellationToken token)
    {
        var preset = UniversalAgentCatalog.GetApiPreset(presetId);
        var cleanBase = (string.IsNullOrWhiteSpace(baseUrl) ? preset.DefaultBaseUrl : baseUrl).Trim().TrimEnd('/');

        return protocol switch
        {
            ApiProtocolKind.AnthropicMessages => await FetchAnthropicDirectCatalogAsync(workspaceId, preset, cleanBase, configuredModel, apiKey, token).ConfigureAwait(false),
            ApiProtocolKind.GeminiNative => await FetchGeminiDirectCatalogAsync(workspaceId, preset, cleanBase, configuredModel, apiKey, token).ConfigureAwait(false),
            _ => await FetchOpenAiCompatibleCatalogAsync(workspaceId, preset, cleanBase, configuredModel, apiKey, token).ConfigureAwait(false),
        };
    }

    private async Task<WorkspaceModelCatalog> FetchOpenAiCompatibleCatalogAsync(
        string workspaceId,
        BuiltInApiPreset preset,
        string cleanBaseUrl,
        string? configuredModel,
        string? apiKey,
        CancellationToken token)
    {
        var entries = new List<ModelCatalogEntry>();
        var source = "preset-fallback";
        var isOllama = cleanBaseUrl.Contains("11434", StringComparison.OrdinalIgnoreCase)
            || string.Equals(preset.Id, "ollama", StringComparison.OrdinalIgnoreCase);

        // 1. Try native Ollama `/api/tags` first when targeting local Ollama so we get parameter sizes & quantization details!
        if (isOllama)
        {
            try
            {
                var rootOrigin = cleanBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                    ? cleanBaseUrl[..^3]
                    : cleanBaseUrl;
                using var req = new HttpRequestMessage(HttpMethod.Get, rootOrigin + "/api/tags");
                using var resp = await _http.SendAsync(req, token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var raw = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    entries.AddRange(ParseOllamaTagsJson(raw));
                    if (entries.Count > 0) source = "live-api";
                }
            }
            catch
            {
            }
        }

        // 2. Standard OpenAI / OpenRouter / DeepSeek / SiliconFlow / xAI / Groq `GET {base}/models`
        if (entries.Count == 0)
        {
            var modelsEndpoint = BuildOpenAiModelsEndpoint(cleanBaseUrl);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, modelsEndpoint);
                if (!string.IsNullOrWhiteSpace(apiKey))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var resp = await _http.SendAsync(req, token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var raw = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    entries.AddRange(ParseOpenAiModelsListJson(raw, cleanBaseUrl, preset.Id));
                    if (entries.Count > 0) source = "live-api";
                }
            }
            catch
            {
            }
        }

        // 3. Fallback to preset suggested models if offline or endpoint doesn't expose `/models`
        if (entries.Count == 0)
        {
            foreach (var m in preset.SuggestedModels)
                entries.Add(BuildOpenAiModelEntry(m, m, null, cleanBaseUrl, hasExplicitReasoningParam: false, preset.Id));
        }

        EnsureConfiguredModelPresent(entries, configuredModel, id => BuildOpenAiModelEntry(id, id, "当前已选模型", cleanBaseUrl, false, preset.Id));
        var defId = !string.IsNullOrWhiteSpace(configuredModel) ? configuredModel : entries.FirstOrDefault()?.Id;
        var defEntry = entries.FirstOrDefault(e => string.Equals(e.Id, defId, StringComparison.OrdinalIgnoreCase));

        return new WorkspaceModelCatalog(
            workspaceId,
            isOllama ? "ollama" : "openai-api",
            source,
            defId,
            defEntry?.DefaultEffort,
            null,
            SupportsCustomModelInput: true,
            entries,
            DateTimeOffset.UtcNow);
    }

    internal static List<ModelCatalogEntry> ParseOpenAiModelsListJson(string rawJson, string baseUrl, string? presetId = null)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var dataArr = root.ValueKind == JsonValueKind.Array
            ? root
            : (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d : default);

        var results = new List<ModelCatalogEntry>();
        if (dataArr.ValueKind != JsonValueKind.Array)
            return results;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in dataArr.EnumerateArray())
        {
            var id = GetJsonString(el, "id");
            if (string.IsNullOrWhiteSpace(id) || id!.Any(char.IsWhiteSpace)) continue;
            if (IsNonChatModelId(id!)) continue;
            if (!seen.Add(id!)) continue;

            var name = GetJsonString(el, "name") ?? id!;
            var desc = GetJsonString(el, "description");
            var ownedBy = GetJsonString(el, "owned_by");
            var hasReasoningParam = false;
            if (el.TryGetProperty("supported_parameters", out var sp) && sp.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in sp.EnumerateArray())
                {
                    var pv = p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                    if (pv is "reasoning" or "include_reasoning" or "reasoning_effort")
                        hasReasoningParam = true;
                }
            }

            results.Add(BuildOpenAiModelEntry(id!, name, desc, baseUrl, hasReasoningParam, presetId, ownedBy));
            if (results.Count >= 180) break; // Keep UI dropdown snappy even on OpenRouter's 300+ catalog
        }

        return results;
    }

    private static ModelCatalogEntry BuildOpenAiModelEntry(
        string id,
        string displayName,
        string? desc,
        string baseUrl,
        bool hasExplicitReasoningParam,
        string? presetId = null,
        string? ownedBy = null)
    {
        var lower = id.ToLowerInvariant();
        var isOpenAiOfficial = baseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(presetId, "openai", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ownedBy, "openai", StringComparison.OrdinalIgnoreCase);
        var supportsEffort = hasExplicitReasoningParam
            || lower.StartsWith("gpt-5", StringComparison.Ordinal)
            || lower.StartsWith("gpt-6", StringComparison.Ordinal)
            || lower.StartsWith("o1", StringComparison.Ordinal)
            || lower.StartsWith("o3", StringComparison.Ordinal)
            || lower.StartsWith("o4", StringComparison.Ordinal)
            || lower.Contains("codex", StringComparison.Ordinal)
            || lower.Contains("grok-3-mini", StringComparison.Ordinal)
            || lower.Contains("grok-4", StringComparison.Ordinal);

        List<ReasoningEffortOption> efforts = supportsEffort
            ? [
                new("minimal", "Minimal", "最低延迟极简思考"),
                new("low", "Low", "快速轻量思考"),
                new("medium", "Medium", "均衡深度与速度"),
                new("high", "High", "深度复杂逻辑推理"),
                new("xhigh", "XHigh", "超高强度深度推理"),
            ]
            : [];

        List<SpeedTierOption> speeds = isOpenAiOfficial
            ? [new("priority", "Fast", "OpenAI Priority 高优加速通道")]
            : [];

        string? badge = null;
        if (lower.Contains("flash", StringComparison.Ordinal) || lower.Contains("mini", StringComparison.Ordinal) || lower.Contains("nano", StringComparison.Ordinal) || lower == "deepseek-chat")
            badge = "⚡ Fast";
        else if (lower.Contains("reasoner", StringComparison.Ordinal) || lower.Contains("-r1", StringComparison.Ordinal) || lower.StartsWith("o3", StringComparison.Ordinal))
            badge = "🧠 Reasoning";

        return new ModelCatalogEntry(
            id,
            displayName,
            CategorizeOpenAiGroup(id, desc),
            desc,
            supportsEffort ? "medium" : null,
            efforts,
            speeds,
            Badge: badge);
    }

    internal static List<ModelCatalogEntry> ParseOllamaTagsJson(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var results = new List<ModelCatalogEntry>();
        if (!root.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var el in arr.EnumerateArray())
        {
            var name = GetJsonString(el, "name") ?? GetJsonString(el, "model");
            if (string.IsNullOrWhiteSpace(name) || name!.Any(char.IsWhiteSpace)) continue;

            string? paramSize = null, quant = null;
            if (el.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.Object)
            {
                paramSize = GetJsonString(det, "parameter_size");
                quant = GetJsonString(det, "quantization_level");
            }
            var desc = string.Join(" · ", new[] { paramSize, quant }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var isThink = name.Contains("r1", StringComparison.OrdinalIgnoreCase)
                || name.Contains("qwq", StringComparison.OrdinalIgnoreCase)
                || name.Contains("think", StringComparison.OrdinalIgnoreCase);

            results.Add(new ModelCatalogEntry(
                name!,
                name!,
                "本机 Ollama 模型",
                string.IsNullOrWhiteSpace(desc) ? "本地离线推理模型" : desc,
                null,
                [],
                [],
                Badge: paramSize ?? (isThink ? "🧠 Think" : "Local")));
        }
        return results;
    }

    private async Task<WorkspaceModelCatalog> FetchAnthropicDirectCatalogAsync(
        string workspaceId,
        BuiltInApiPreset preset,
        string cleanBaseUrl,
        string? configuredModel,
        string? apiKey,
        CancellationToken token)
    {
        ReasoningEffortOption[] efforts =
        [
            new("low", "Low", "2K 思考 Token 预算"),
            new("medium", "Medium", "8K 思考 Token 预算"),
            new("high", "High", "16K 深度思考预算"),
            new("max", "Max", "24K 极限思考预算"),
        ];
        SpeedTierOption[] speeds = [new("fast", "Fast", "Anthropic 高速推理通道 (speed=fast)")];

        var entries = new List<ModelCatalogEntry>();
        var source = "preset-fallback";

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                entries = await FetchAnthropicModelsListAsync(cleanBaseUrl, apiKey!, efforts, speeds, token).ConfigureAwait(false);
                if (entries.Count > 0) source = "live-api";
            }
            catch
            {
            }
        }

        if (entries.Count == 0)
        {
            foreach (var m in preset.SuggestedModels)
            {
                var isHaiku = m.Contains("haiku", StringComparison.OrdinalIgnoreCase);
                entries.Add(new ModelCatalogEntry(m, m, "Anthropic Claude", null, isHaiku ? null : "medium", efforts, isHaiku ? [] : speeds, Badge: isHaiku ? "⚡ Fast" : null));
            }
        }

        EnsureConfiguredModelPresent(entries, configuredModel, id => new ModelCatalogEntry(id, id, "Anthropic Claude", null, "medium", efforts, speeds));
        var defId = !string.IsNullOrWhiteSpace(configuredModel) ? configuredModel : entries.FirstOrDefault()?.Id;

        return new WorkspaceModelCatalog(
            workspaceId,
            "anthropic-api",
            source,
            defId,
            "medium",
            null,
            SupportsCustomModelInput: true,
            entries,
            DateTimeOffset.UtcNow);
    }

    private async Task<List<ModelCatalogEntry>> FetchAnthropicModelsListAsync(
        string cleanBaseUrl,
        string apiKey,
        IReadOnlyList<ReasoningEffortOption> efforts,
        IReadOnlyList<SpeedTierOption> speeds,
        CancellationToken token)
    {
        var endpoint = cleanBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? cleanBaseUrl + "/models"
            : cleanBaseUrl + "/v1/models";

        using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
        req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        using var resp = await _http.SendAsync(req, token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];

        var raw = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return ParseAnthropicModelsListJson(raw, efforts, speeds);
    }

    internal static List<ModelCatalogEntry> ParseAnthropicModelsListJson(
        string rawJson,
        IReadOnlyList<ReasoningEffortOption> efforts,
        IReadOnlyList<SpeedTierOption> speeds)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var list = new List<ModelCatalogEntry>();
        if (!root.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var el in arr.EnumerateArray())
        {
            var id = GetJsonString(el, "id");
            if (string.IsNullOrWhiteSpace(id) || id!.Any(char.IsWhiteSpace)) continue;
            var display = GetJsonString(el, "display_name") ?? id!;
            var isHaiku = id!.Contains("haiku", StringComparison.OrdinalIgnoreCase);
            list.Add(new ModelCatalogEntry(
                id!,
                display,
                "Anthropic Claude",
                null,
                isHaiku ? null : "medium",
                efforts,
                isHaiku ? [] : speeds,
                Badge: isHaiku ? "⚡ Fast" : null));
        }
        return list;
    }

    private async Task<WorkspaceModelCatalog> FetchGeminiDirectCatalogAsync(
        string workspaceId,
        BuiltInApiPreset preset,
        string cleanBaseUrl,
        string? configuredModel,
        string? apiKey,
        CancellationToken token)
    {
        var entries = new List<ModelCatalogEntry>();
        var source = "preset-fallback";

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                var endpoint = cleanBaseUrl.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase)
                    ? cleanBaseUrl + "/models?pageSize=100"
                    : cleanBaseUrl + "/v1beta/models?pageSize=100";
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                using var resp = await _http.SendAsync(req, token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var raw = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    entries = ParseGeminiModelsListJson(raw);
                    if (entries.Count > 0) source = "live-api";
                }
            }
            catch
            {
            }
        }

        if (entries.Count == 0)
        {
            foreach (var m in preset.SuggestedModels)
                entries.Add(BuildGeminiModelEntry(m, m, null, supportsThinking: true));
        }

        EnsureConfiguredModelPresent(entries, configuredModel, id => BuildGeminiModelEntry(id, id, null, supportsThinking: true));
        var defId = !string.IsNullOrWhiteSpace(configuredModel) ? configuredModel : entries.FirstOrDefault()?.Id;
        var defEntry = entries.FirstOrDefault(e => string.Equals(e.Id, defId, StringComparison.OrdinalIgnoreCase));

        return new WorkspaceModelCatalog(
            workspaceId,
            "gemini-api",
            source,
            defId,
            defEntry?.DefaultEffort,
            null,
            SupportsCustomModelInput: true,
            entries,
            DateTimeOffset.UtcNow);
    }

    internal static List<ModelCatalogEntry> ParseGeminiModelsListJson(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var list = new List<ModelCatalogEntry>();
        if (!root.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var el in arr.EnumerateArray())
        {
            var rawName = GetJsonString(el, "name");
            if (string.IsNullOrWhiteSpace(rawName)) continue;
            var id = rawName!.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? rawName[7..] : rawName;
            if (id.Any(char.IsWhiteSpace) || IsNonChatModelId(id)) continue;

            var canGenerate = false;
            if (el.TryGetProperty("supportedGenerationMethods", out var methods) && methods.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in methods.EnumerateArray())
                {
                    if (m.ValueKind == JsonValueKind.String && m.GetString() == "generateContent")
                        canGenerate = true;
                }
            }
            if (!canGenerate) continue;

            var display = GetJsonString(el, "displayName") ?? id;
            var desc = GetJsonString(el, "description");
            var thinking = (el.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.True)
                || id.Contains("2.5", StringComparison.OrdinalIgnoreCase)
                || id.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)
                || id.Contains("gemini-4", StringComparison.OrdinalIgnoreCase);

            list.Add(BuildGeminiModelEntry(id, display, desc, thinking));
        }

        return list
            .OrderByDescending(m => m.Id.StartsWith("gemini-4", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => m.Id.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => m.Id.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static ModelCatalogEntry BuildGeminiModelEntry(string id, string displayName, string? desc, bool supportsThinking)
    {
        var isFlash = id.Contains("flash", StringComparison.OrdinalIgnoreCase);
        List<ReasoningEffortOption> efforts = supportsThinking
            ? [
                new("minimal", "Minimal", "极速低延迟响应"),
                new("low", "Low", "轻量级思考"),
                new("medium", "Medium", "标准均衡思考"),
                new("high", "High", "深度多步推理"),
            ]
            : [];

        return new ModelCatalogEntry(
            id,
            displayName,
            "Google Gemini",
            desc,
            supportsThinking ? (isFlash ? "low" : "high") : null,
            efforts,
            [],
            Badge: isFlash ? "⚡ Flash" : null);
    }

    // =========================================================================
    // 5. Market CLI Discovery (`codex-cli`, `claude-code`, `antigravity-cli`, `gemini-cli`, etc.)
    // =========================================================================
    private async Task<WorkspaceModelCatalog> DiscoverMarketCliCatalogAsync(
        CodexWorkspace workspace,
        bool forceRefresh,
        bool wakeDaemonIfNeeded,
        CancellationToken token)
    {
        var record = _agentProviders.FindById(workspace.ProviderId ?? workspace.Id);
        if (record is null)
            return BuildFallbackCodexCatalog(workspace.Id, workspace.DefaultModel, null, null);

        var home = _agentProviders.ResolveHomeDirectory(record);
        var apiKey = DpapiSecretProtector.Unprotect(record.EncryptedApiKey);

        // Route Antigravity CLI accounts (including migrated Antigravity homes) to official `agy acp` catalog discovery
        if (AntigravityAgentRunner.IsAntigravityBackedCliRecord(record, home))
        {
            return await DiscoverAntigravityCatalogAsync(workspace.Id, home, wakeDaemonIfNeeded, token).ConfigureAwait(false);
        }

        switch (record.PresetId)
        {
            case "codex-cli":
            {
                var codexHome = Directory.Exists(Path.Combine(home, ".codex"))
                    ? Path.Combine(home, ".codex")
                    : (Directory.Exists(Path.Combine(home, "codex-home")) ? Path.Combine(home, "codex-home") : home);
                var paths = new WorkspaceDataPaths(home, codexHome, home, ClientKind.Codex);
                return await DiscoverCodexCatalogAsync(workspace.Id, paths, forceRefresh, token).ConfigureAwait(false);
            }

            case "claude-code":
            {
                var claudeDir = Directory.Exists(Path.Combine(home, ".claude")) ? Path.Combine(home, ".claude") : home;
                return await DiscoverClaudeCatalogAsync(workspace.Id, claudeDir, apiKey, record.BaseUrl, token).ConfigureAwait(false);
            }

            case "gemini-cli":
            {
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    return await FetchGeminiDirectCatalogAsync(
                        workspace.Id,
                        UniversalAgentCatalog.GetApiPreset("gemini"),
                        string.IsNullOrWhiteSpace(record.BaseUrl) ? "https://generativelanguage.googleapis.com" : record.BaseUrl,
                        record.Model,
                        apiKey,
                        token).ConfigureAwait(false);
                }

                ModelCatalogEntry[] gemModels =
                [
                    BuildGeminiModelEntry("gemini-2.5-pro", "Gemini 2.5 Pro", "旗舰复杂代码推理", true),
                    BuildGeminiModelEntry("gemini-2.5-flash", "Gemini 2.5 Flash", "高速低延迟日常研发", true),
                    BuildGeminiModelEntry("gemini-3-pro-preview", "Gemini 3 Pro Preview", "新一代深度推理预览", true),
                    BuildGeminiModelEntry("gemini-3-flash-preview", "Gemini 3 Flash Preview", "新一代极速模型预览", true),
                ];
                EnsureConfiguredModelPresent(gemModels.ToList(), record.Model, id => BuildGeminiModelEntry(id, id, null, true));
                return new WorkspaceModelCatalog(workspace.Id, "cli", "local-cache", record.Model, null, null, true, gemModels, DateTimeOffset.UtcNow);
            }

            default:
            {
                var preset = UniversalAgentCatalog.GetCliPreset(record.PresetId);
                var models = new List<ModelCatalogEntry>();
                if (!string.IsNullOrWhiteSpace(record.Model))
                    models.Add(new ModelCatalogEntry(record.Model, record.Model, preset.Badge, "当前绑定模型", null, [], [], IsDefault: true));
                if (!string.IsNullOrWhiteSpace(preset.DefaultModel) && !models.Any(m => m.Id == preset.DefaultModel))
                    models.Add(new ModelCatalogEntry(preset.DefaultModel, preset.DefaultModel, preset.Badge, "预设默认模型", null, [], []));

                return new WorkspaceModelCatalog(
                    workspace.Id,
                    "cli",
                    "local-cache",
                    record.Model,
                    null,
                    null,
                    SupportsCustomModelInput: true,
                    models,
                    DateTimeOffset.UtcNow);
            }
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================
    private static void EnsureConfiguredModelPresent(List<ModelCatalogEntry> entries, string? configuredModel, Func<string, ModelCatalogEntry> factory)
    {
        if (string.IsNullOrWhiteSpace(configuredModel)) return;
        var trimmed = configuredModel.Trim();
        if (trimmed.Any(char.IsWhiteSpace)) return;
        if (!entries.Any(e => string.Equals(e.Id, trimmed, StringComparison.OrdinalIgnoreCase)))
            entries.Insert(0, factory(trimmed) with { IsDefault = true });
    }

    private static string BuildOpenAiModelsEndpoint(string cleanBaseUrl)
    {
        if (cleanBaseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            cleanBaseUrl = cleanBaseUrl[..^"/chat/completions".Length];
        if (cleanBaseUrl.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            return cleanBaseUrl;
        if (cleanBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
            cleanBaseUrl.Contains("/v1/", StringComparison.OrdinalIgnoreCase) ||
            cleanBaseUrl.Contains("api.deepseek.com", StringComparison.OrdinalIgnoreCase))
        {
            return cleanBaseUrl + "/models";
        }
        return cleanBaseUrl + "/v1/models";
    }

    private static bool IsNonChatModelId(string id)
    {
        var l = id.ToLowerInvariant();
        return l.Contains("embedding")
            || l.Contains("whisper")
            || l.Contains("tts-")
            || l.Contains("-tts")
            || l.Contains("dall-e")
            || l.Contains("imagen")
            || l.Contains("veo-")
            || l.Contains("moderation")
            || l.Contains("rerank")
            || l.Contains("babbage")
            || l.Contains("davinci")
            || l.StartsWith("aqa", StringComparison.Ordinal);
    }

    private static string CategorizeOpenAiGroup(string id, string? desc)
    {
        var l = id.ToLowerInvariant();
        if (desc?.Contains("Legacy", StringComparison.OrdinalIgnoreCase) == true) return "Legacy Models";
        if (l.StartsWith("gpt-6", StringComparison.Ordinal)) return "GPT-6 Series";
        if (l.StartsWith("gpt-5", StringComparison.Ordinal)) return "GPT-5 Series";
        if (l.StartsWith("gpt-", StringComparison.Ordinal) || l.StartsWith("o1", StringComparison.Ordinal) || l.StartsWith("o3", StringComparison.Ordinal) || l.StartsWith("o4", StringComparison.Ordinal) || l.Contains("codex"))
            return "OpenAI / Reasoning";
        if (l.Contains("claude")) return "Anthropic Claude";
        if (l.Contains("gemini")) return "Google Gemini";
        if (l.Contains("deepseek")) return "DeepSeek";
        if (l.Contains("qwen")) return "Qwen";
        if (id.Contains('/')) return id.Split('/')[0];
        return "Models";
    }

    public static string FormatEffortLabel(string effort) => effort.ToLowerInvariant() switch
    {
        "none" => "None",
        "minimal" => "Minimal",
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "XHigh",
        "max" => "Max",
        "ultra" => "Ultra",
        _ => effort,
    };

    private static string StripEffortSuffix(string modelId)
    {
        foreach (var suffix in EffortSortOrder)
        {
            if (modelId.EndsWith("-" + suffix, StringComparison.OrdinalIgnoreCase) && modelId.Length > suffix.Length + 2)
                return modelId[..^(suffix.Length + 1)];
        }
        return modelId;
    }

    private static string? ExtractEffortSuffix(string modelId)
    {
        foreach (var suffix in EffortSortOrder)
        {
            if (modelId.EndsWith("-" + suffix, StringComparison.OrdinalIgnoreCase))
                return suffix;
        }
        return null;
    }

    private static string? GetJsonString(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;
}
