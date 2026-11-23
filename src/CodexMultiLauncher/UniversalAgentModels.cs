using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexMultiLauncher;

[JsonConverter(typeof(JsonStringEnumConverter<ApiProtocolKind>))]
public enum ApiProtocolKind
{
    OpenAiCompatible,
    AnthropicMessages,
    GeminiNative,
}

[JsonConverter(typeof(JsonStringEnumConverter<UniversalAgentKind>))]
public enum UniversalAgentKind
{
    DirectApi,
    MarketCli,
}

public sealed record BuiltInApiPreset(
    string Id,
    string Name,
    string Badge,
    ApiProtocolKind Protocol,
    string DefaultBaseUrl,
    string DefaultModel,
    IReadOnlyList<string> SuggestedModels,
    bool RequiresApiKey,
    string Description);

public sealed record MarketCliPreset(
    string Id,
    string Name,
    string Badge,
    string BinaryName,
    string InstallCommand,
    string LoginHint,
    string DefaultModel,
    string? ApiKeyEnvVar,
    string? BaseUrlEnvVar,
    string Description);

public sealed record DetectedCliEngine(
    string PresetId,
    string Name,
    string Badge,
    bool Installed,
    string? ExecutablePath,
    string? VersionHint,
    string InstallCommand,
    string LoginHint,
    string DefaultModel,
    string? ApiKeyEnvVar,
    string? BaseUrlEnvVar,
    string Description);

public sealed record UniversalAgentRecord(
    string Id,
    string DisplayName,
    UniversalAgentKind AgentKind,
    string PresetId,
    ApiProtocolKind Protocol,
    string BaseUrl,
    string Model,
    string? EncryptedApiKey,
    string? MaskedApiKey,
    string? CustomCliExecutable,
    string? CustomCliArgsTemplate,
    long? TokenBudgetLimit,
    DateTimeOffset CreatedAtUtc,
    string? CustomHomeDirectory = null);

public sealed record UniversalAgentUpsertRequest(
    string? Id,
    string DisplayName,
    UniversalAgentKind AgentKind,
    string PresetId,
    ApiProtocolKind Protocol,
    string? BaseUrl,
    string? Model,
    string? ApiKeyPlaintext,
    bool ClearApiKey = false,
    string? CustomCliExecutable = null,
    string? CustomCliArgsTemplate = null,
    long? TokenBudgetLimit = null,
    string? CustomHomeDirectory = null);

/// <summary>
/// Encrypts and decrypts user-supplied API keys at rest using Windows DPAPI (CurrentUser scope)
/// so no plaintext API keys are ever persisted in JSON files.
/// </summary>
public static class DpapiSecretProtector
{
    private const string Prefix = "dpapi:";
    private const uint CryptProtectUiForbidden = 0x1;

    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return null;
        var inputBytes = Encoding.UTF8.GetBytes(plaintext.Trim());
        var inBlob = AllocBlob(inputBytes);
        var outBlob = new DataBlob();
        try
        {
            if (!CryptProtectData(ref inBlob, "CodexMultiLauncher.UniversalAgentKey", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob))
                throw new InvalidOperationException($"Windows DPAPI 密钥加密失败（错误码 {Marshal.GetLastWin32Error()}）。");
            var cipher = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, cipher, 0, outBlob.cbData);
            return Prefix + Convert.ToBase64String(cipher);
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return null;
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("不支持未受 DPAPI 保护的凭据格式。");

        var cipher = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        var inBlob = AllocBlob(cipher);
        var outBlob = new DataBlob();
        try
        {
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob))
                throw new InvalidOperationException("无法解密此账号保存的 API Key（可能更换了 Windows 用户）。请重新输入密钥。");
            var plainBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    public static bool CanUnprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue) || !protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        try
        {
            return !string.IsNullOrWhiteSpace(Unprotect(protectedValue));
        }
        catch
        {
            return false;
        }
    }

    public static string? Mask(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return null;
        var trimmed = plaintext.Trim();
        if (trimmed.Length <= 8) return new string('•', trimmed.Length);
        return $"{trimmed[..Math.Min(4, trimmed.Length)]}••••{trimmed[^4..]}";
    }

    private static DataBlob AllocBlob(byte[] bytes)
    {
        var ptr = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        if (bytes.Length > 0) Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return new DataBlob { cbData = bytes.Length, pbData = ptr };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

public static class UniversalAgentCatalog
{
    public static readonly IReadOnlyList<BuiltInApiPreset> ApiPresets =
    [
        new(
            "gemini",
            "Google Gemini 直连（防串号推荐）",
            "Gemini API",
            ApiProtocolKind.GeminiNative,
            "https://generativelanguage.googleapis.com",
            "gemini-2.5-pro",
            ["gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.5-flash-lite"],
            RequiresApiKey: true,
            "免装 Antigravity 客户端，每账号独立 API Key 物理隔离，彻底杜绝多开 Google 串号，内置完整读写与终端工具链。"),
        new(
            "deepseek",
            "DeepSeek 深度求索",
            "DeepSeek",
            ApiProtocolKind.OpenAiCompatible,
            "https://api.deepseek.com/v1",
            "deepseek-chat",
            ["deepseek-chat", "deepseek-reasoner"],
            RequiresApiKey: true,
            "零下载直连 DeepSeek V3 / R1，自动赋予本地文件编辑、搜索与 PowerShell 终端实操能力。"),
        new(
            "openai",
            "OpenAI 官方直连",
            "OpenAI API",
            ApiProtocolKind.OpenAiCompatible,
            "https://api.openai.com/v1",
            "gpt-4.1",
            ["gpt-4.1", "o4-mini", "gpt-4o", "o3"],
            RequiresApiKey: true,
            "免装 Microsoft Store 版 Codex 客户端，纯内置 C# 工具循环直连 OpenAI 模型。"),
        new(
            "anthropic",
            "Anthropic Claude 直连",
            "Claude API",
            ApiProtocolKind.AnthropicMessages,
            "https://api.anthropic.com",
            "claude-sonnet-4-20250514",
            ["claude-sonnet-4-20250514", "claude-opus-4-20250514", "claude-3-7-sonnet-latest"],
            RequiresApiKey: true,
            "免装 Claude MSIX 桌面端，原生支持 Anthropic Messages 工具调用协议与多账号并发。"),
        new(
            "qwen",
            "阿里云百炼 · 通义千问 Qwen",
            "Qwen Coder",
            ApiProtocolKind.OpenAiCompatible,
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "qwen3-coder-plus",
            ["qwen3-coder-plus", "qwen-max", "qwen-plus"],
            RequiresApiKey: true,
            "直连阿里云 DashScope Qwen3-Coder 编程大模型，免装任何外部插件。"),
        new(
            "kimi",
            "Moonshot Kimi 编程智能体",
            "Kimi K2",
            ApiProtocolKind.OpenAiCompatible,
            "https://api.moonshot.cn/v1",
            "kimi-k2-0711-preview",
            ["kimi-k2-0711-preview", "moonshot-v1-128k"],
            RequiresApiKey: true,
            "直连月之暗面 Kimi K2 模型，内置本地仓库读写与命令行工具链。"),
        new(
            "glm",
            "智谱清言 · GLM Coding",
            "GLM-4.5",
            ApiProtocolKind.OpenAiCompatible,
            "https://open.bigmodel.cn/api/paas/v4",
            "glm-4.5",
            ["glm-4.5", "glm-4-plus", "glm-4-air"],
            RequiresApiKey: true,
            "直连智谱 BigModel 开放平台，免客户端即刻接入多智能体协同。"),
        new(
            "openrouter",
            "OpenRouter 全市场模型聚合",
            "OpenRouter",
            ApiProtocolKind.OpenAiCompatible,
            "https://openrouter.ai/api/v1",
            "anthropic/claude-sonnet-4",
            ["anthropic/claude-sonnet-4", "google/gemini-2.5-pro", "deepseek/deepseek-chat-v3-0324", "openai/gpt-4.1"],
            RequiresApiKey: true,
            "一把 Key 畅联全球 200+ 主流编程模型，支持在赛马竞技场同台跨模型竞技。"),
        new(
            "ollama",
            "Ollama / LM Studio 本地离线模型",
            "Local LLM",
            ApiProtocolKind.OpenAiCompatible,
            "http://localhost:11434/v1",
            "qwen2.5-coder:14b",
            ["qwen2.5-coder:14b", "qwen2.5-coder:32b", "deepseek-r1:14b", "llama3.3"],
            RequiresApiKey: false,
            "零 API 费用、100% 内网离线隐私安全，将本地 Ollama / LM Studio 秒变为全功能代码 Agent。"),
        new(
            "custom-openai",
            "自定义 OpenAI 兼容网关 / 企业中转",
            "Custom API",
            ApiProtocolKind.OpenAiCompatible,
            "https://api.openai.com/v1",
            "gpt-4.1",
            ["gpt-4.1", "claude-sonnet-4-20250514", "gemini-2.5-pro", "deepseek-chat"],
            RequiresApiKey: false,
            "适配任意 NewAPI / OneAPI / LiteLLM / vLLM / Azure / 企业内网大模型网关。"),
    ];

    public static readonly IReadOnlyList<MarketCliPreset> CliPresets =
    [
        new(
            "codex-cli",
            "OpenAI Codex CLI（免装商店客户端）",
            "Codex CLI",
            "codex",
            "npm.cmd install -g @openai/codex",
            "可在分屏终端执行 `codex.cmd login` 或直接配置独立 OPENAI_API_KEY",
            "",
            "OPENAI_API_KEY",
            "OPENAI_BASE_URL",
            "仅 ~15MB 轻量命令行引擎，无需安装臃肿 Windows Store 桌面客户端，独立 CODEX_HOME 多开。"),
        new(
            "claude-code",
            "Anthropic Claude Code CLI",
            "Claude Code",
            "claude",
            "npm.cmd install -g @anthropic-ai/claude-code",
            "可在分屏终端执行 `claude.cmd auth login` 或配置 ANTHROPIC_API_KEY",
            "",
            "ANTHROPIC_API_KEY",
            "ANTHROPIC_BASE_URL",
            "官方原版 Claude Code 引擎，独立 CLAUDE_CONFIG_DIR 隔离多账号，无需安装 Claude 桌面端。"),
        new(
            "gemini-cli",
            "Google Gemini CLI（独立目录防串号）",
            "Gemini CLI",
            "gemini",
            "npm.cmd install -g @google/gemini-cli",
            "支持配置独立 GEMINI_API_KEY 或在隔离 HOME 目录登录独立 Google 账号",
            "gemini-2.5-pro",
            "GEMINI_API_KEY",
            null,
            "Google 官方轻量级编程 CLI，每个实例分配独立 HOME 与 .gemini 目录，不依赖反重力桌面端。"),
        new(
            "antigravity-cli",
            "Antigravity CLI (官方 agy acp)",
            "AGY ACP",
            "agy",
            AntigravityAgentRunner.OfficialInstallCommand,
            "在内置终端执行 `agy acp --login` 即可通过官方标准 ACP 协议驱动（零封号风险）",
            "gemini-4-carbon",
            "GEMINI_API_KEY",
            null,
            "采用 Google 官方公开 Agent Client Protocol (`agy acp` JSON-RPC 2.0) 通信，与 Zed / JetBrains 官方集成同源。"),
        new(
            "opencode",
            "OpenCode AI CLI",
            "OpenCode",
            "opencode",
            "npm install -g opencode-ai",
            "可在分屏终端运行 `opencode auth login` 或配置 API Key",
            "",
            "OPENAI_API_KEY",
            "OPENAI_BASE_URL",
            "开源高人气多模型终端编码智能体（`opencode run --format json`），免桌面客户端。"),
        new(
            "aider",
            "Aider AI Pair Programmer",
            "Aider",
            "aider",
            "python -m pip install -U aider-chat",
            "填入任意模型与对应 API Key 即可后台自动改代码（`--message --yes-always`）",
            "deepseek/deepseek-chat",
            "OPENAI_API_KEY",
            "OPENAI_API_BASE",
            "经典 Git 感知型 AI 结对编程工具，自动精细打补丁。"),
        new(
            "qwen-code",
            "Alibaba Qwen Code CLI",
            "Qwen CLI",
            "qwen",
            "npm install -g @qwen-code/qwen-code",
            "支持配置通义百炼 OPENAI_API_KEY 或终端 OAuth 登录",
            "qwen3-coder-plus",
            "OPENAI_API_KEY",
            "OPENAI_BASE_URL",
            "阿里开源 Qwen Code 命令行智能体，轻量高速。"),
        new(
            "goose",
            "Block Goose Autonomous CLI",
            "Goose",
            "goose",
            "pipx install goose-ai",
            "通过 `goose run --text` 无头执行工程任务",
            "",
            "OPENAI_API_KEY",
            "OPENAI_HOST",
            "Block 开源自主工程 Agent CLI。"),
        new(
            "custom-cli",
            "自定义任意市场 CLI 智能体",
            "Custom CLI",
            "custom",
            "echo 请在配置中填写自定义 CLI 可执行路径与参数模板",
            "支持任意命令行 Agent（用 {prompt}、{model}、{cwd} 占位符或标准输入）",
            "",
            "OPENAI_API_KEY",
            "OPENAI_BASE_URL",
            "万能 CLI 适配器：接入 Cursor Agent、Cline CLI、内部自研 CLI 等任何命令行工具。"),
    ];

    public static BuiltInApiPreset GetApiPreset(string? id) =>
        ApiPresets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? ApiPresets[0];

    public static MarketCliPreset GetCliPreset(string? id) =>
        CliPresets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? CliPresets[0];

    public static IReadOnlyList<DetectedCliEngine> DetectInstalledClis(IEnumerable<CodexWorkspace>? desktopWorkspaces = null)
    {
        var list = new List<DetectedCliEngine>(CliPresets.Count);
        foreach (var preset in CliPresets)
        {
            if (preset.Id == "custom-cli")
            {
                list.Add(new DetectedCliEngine(
                    preset.Id, preset.Name, preset.Badge, true, null, "通用模板",
                    preset.InstallCommand, preset.LoginHint, preset.DefaultModel,
                    preset.ApiKeyEnvVar, preset.BaseUrlEnvVar, preset.Description));
                continue;
            }

            var resolved = TryFindCliBinary(preset.Id, preset.BinaryName, desktopWorkspaces);
            list.Add(new DetectedCliEngine(
                preset.Id,
                preset.Name,
                preset.Badge,
                resolved is not null,
                resolved,
                resolved is not null ? Path.GetFileName(resolved) : null,
                preset.InstallCommand,
                preset.LoginHint,
                preset.DefaultModel,
                preset.ApiKeyEnvVar,
                preset.BaseUrlEnvVar,
                preset.Description));
        }
        return list;
    }

    public static string? TryFindCliBinary(string presetId, string binaryName, IEnumerable<CodexWorkspace>? desktopWorkspaces = null)
    {
        if (presetId == "claude-code")
        {
            var bundledClaude = ClaudeCliRunner.TryFindClaudeCliExecutable(null, desktopWorkspaces);
            if (bundledClaude is not null) return bundledClaude;
        }

        if (presetId == "codex-cli")
        {
            // Prefer standalone npm codex first, or fall back to official bundled codex.exe if present
            var npmCodex = SearchStandardLocations("codex");
            if (npmCodex is not null) return npmCodex;
            try
            {
                return CodexExecutableLocator.FindCurrentCandidateOrThrow();
            }
            catch { }
        }

        if (presetId == "antigravity-cli")
        {
            return AntigravityAgentRunner.TryFindAgyCliExecutable();
        }

        return SearchStandardLocations(binaryName);
    }

    private static string? SearchStandardLocations(string binaryName)
    {
        if (string.IsNullOrWhiteSpace(binaryName)) return null;
        var candidates = new List<string>();

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var envRoaming = Environment.GetEnvironmentVariable("APPDATA");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var envLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var siblingRoaming = !string.IsNullOrWhiteSpace(envLocal) && Directory.GetParent(envLocal) is { } appDataParent
            ? Path.Combine(appDataParent.FullName, "Roaming")
            : null;

        foreach (var npmRoot in new[] { roaming, envRoaming, siblingRoaming }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(npmRoot!, "npm", binaryName + ".cmd"));
            candidates.Add(Path.Combine(npmRoot!, "npm", binaryName + ".exe"));
        }

        foreach (var home in new[] { userProfile, Environment.GetEnvironmentVariable("USERPROFILE") }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(home!, ".local", "bin", binaryName + ".exe"));
            candidates.Add(Path.Combine(home!, ".local", "bin", binaryName + ".cmd"));
            candidates.Add(Path.Combine(home!, ".cargo", "bin", binaryName + ".exe"));
        }

        foreach (var loc in new[] { local, envLocal }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(loc!, "Programs", binaryName, binaryName + ".exe"));
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Path.IsPathFullyQualified(dir)) continue;
                candidates.Add(Path.Combine(dir, binaryName + ".exe"));
                candidates.Add(Path.Combine(dir, binaryName + ".cmd"));
                candidates.Add(Path.Combine(dir, binaryName + ".bat"));
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
            catch
            {
            }
        }

        return null;
    }
}

/// <summary>
/// Persists zero-download Direct API agents and lightweight Market CLI agents in `data/agent-providers.json`
/// with DPAPI-encrypted credentials and per-agent isolated home directories under `data/universal-agents/`.
/// </summary>
public sealed class AgentProviderStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _projectRoot;
    private readonly string _storePath;
    private readonly string _agentsRoot;
    private readonly object _gate = new();
    private List<UniversalAgentRecord> _records = [];

    public AgentProviderStore(string projectRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        var dataDir = ProfilePathSafety.GetContainedFullPath(_projectRoot, "data");
        _storePath = ProfilePathSafety.GetContainedFullPath(dataDir, "agent-providers.json");
        _agentsRoot = ProfilePathSafety.GetContainedFullPath(dataDir, "universal-agents");
        Reload();
    }

    public IReadOnlyList<UniversalAgentRecord> List()
    {
        lock (_gate) return _records.ToArray();
    }

    public UniversalAgentRecord? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var normalized = StripWorkspacePrefix(id);
        lock (_gate) return _records.FirstOrDefault(r => string.Equals(r.Id, normalized, StringComparison.Ordinal));
    }

    public static string ToWorkspaceId(UniversalAgentRecord record) =>
        (record.AgentKind == UniversalAgentKind.DirectApi ? "api-" : "cli-") + record.Id;

    public static string StripWorkspacePrefix(string id)
    {
        if (id.StartsWith("api-", StringComparison.Ordinal)) return id[4..];
        if (id.StartsWith("cli-", StringComparison.Ordinal)) return id[4..];
        return id;
    }

    public string GetIsolatedHomeDirectory(string recordId)
    {
        ValidateRecordId(recordId);
        var home = ProfilePathSafety.GetContainedFullPath(_agentsRoot, recordId, "home");
        ProfilePathSafety.ThrowIfReparsePath(home);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(Path.Combine(home, "AppData", "Roaming"));
        Directory.CreateDirectory(Path.Combine(home, ".config"));
        Directory.CreateDirectory(Path.Combine(home, ".gemini"));
        return home;
    }

    public string ResolveHomeDirectory(UniversalAgentRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.CustomHomeDirectory))
        {
            var custom = Path.GetFullPath(record.CustomHomeDirectory.Trim());
            ProfilePathSafety.ThrowIfReparsePath(custom);
            Directory.CreateDirectory(custom);
            Directory.CreateDirectory(Path.Combine(custom, ".gemini"));
            return custom;
        }
        return GetIsolatedHomeDirectory(record.Id);
    }

    public void Reload()
    {
        lock (_gate)
        {
            if (!File.Exists(_storePath))
            {
                _records = [];
                return;
            }
            try
            {
                ProfilePathSafety.ThrowIfReparsePath(_storePath);
                var json = File.ReadAllText(_storePath, Encoding.UTF8);
                _records = JsonSerializer.Deserialize<List<UniversalAgentRecord>>(json, SerializerOptions) ?? [];
            }
            catch
            {
                _records = [];
            }
        }
    }

    public UniversalAgentRecord Upsert(UniversalAgentUpsertRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var displayName = ProfileStore.ValidateDisplayName(request.DisplayName);

        lock (_gate)
        {
            UniversalAgentRecord? existing = null;
            string id;
            if (!string.IsNullOrWhiteSpace(request.Id))
            {
                id = StripWorkspacePrefix(request.Id.Trim());
                ValidateRecordId(id);
                existing = _records.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
            }
            else
            {
                id = Guid.NewGuid().ToString("N");
            }

            string baseUrl;
            string model;
            ApiProtocolKind protocol = request.Protocol;
            if (request.AgentKind == UniversalAgentKind.DirectApi)
            {
                var preset = UniversalAgentCatalog.GetApiPreset(request.PresetId);
                protocol = request.Protocol;
                baseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? preset.DefaultBaseUrl : request.BaseUrl.Trim().TrimEnd('/');
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new InvalidOperationException("请输入有效的 HTTP/HTTPS API Base URL。");
                model = string.IsNullOrWhiteSpace(request.Model) ? preset.DefaultModel : request.Model.Trim();
                if (string.IsNullOrWhiteSpace(model))
                    throw new InvalidOperationException("请填写要调用的模型名称（如 gemini-2.5-pro、deepseek-chat、gpt-4.1）。");
            }
            else
            {
                var cliPreset = UniversalAgentCatalog.GetCliPreset(request.PresetId);
                baseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? string.Empty : request.BaseUrl.Trim();
                model = string.IsNullOrWhiteSpace(request.Model) ? cliPreset.DefaultModel : request.Model.Trim();
                if (cliPreset.Id == "custom-cli" && string.IsNullOrWhiteSpace(request.CustomCliExecutable))
                    throw new InvalidOperationException("自定义 CLI 智能体必须指定可执行程序路径或命令名。");
            }

            var encryptedKey = existing?.EncryptedApiKey;
            var maskedKey = existing?.MaskedApiKey;
            if (request.ClearApiKey)
            {
                encryptedKey = null;
                maskedKey = null;
            }
            else if (!string.IsNullOrWhiteSpace(request.ApiKeyPlaintext))
            {
                encryptedKey = DpapiSecretProtector.Protect(request.ApiKeyPlaintext);
                maskedKey = DpapiSecretProtector.Mask(request.ApiKeyPlaintext);
            }

            if (request.AgentKind == UniversalAgentKind.DirectApi)
            {
                var preset = UniversalAgentCatalog.GetApiPreset(request.PresetId);
                if (preset.RequiresApiKey && string.IsNullOrWhiteSpace(encryptedKey))
                    throw new InvalidOperationException($"“{preset.Name}”需要填写 API Key（密钥仅使用 Windows DPAPI 在本机加密存储）。");
            }

            string? customHome = null;
            if (!string.IsNullOrWhiteSpace(request.CustomHomeDirectory))
            {
                var candidateHome = Path.GetFullPath(request.CustomHomeDirectory.Trim());
                ProfilePathSafety.ThrowIfReparsePath(candidateHome);
                customHome = candidateHome;
            }
            else if (existing?.CustomHomeDirectory is not null && request.CustomHomeDirectory is null)
            {
                customHome = existing.CustomHomeDirectory;
            }

            var updated = new UniversalAgentRecord(
                Id: id,
                DisplayName: displayName,
                AgentKind: request.AgentKind,
                PresetId: request.PresetId.Trim(),
                Protocol: protocol,
                BaseUrl: baseUrl,
                Model: model,
                EncryptedApiKey: encryptedKey,
                MaskedApiKey: maskedKey,
                CustomCliExecutable: string.IsNullOrWhiteSpace(request.CustomCliExecutable) ? null : request.CustomCliExecutable.Trim(),
                CustomCliArgsTemplate: string.IsNullOrWhiteSpace(request.CustomCliArgsTemplate) ? null : request.CustomCliArgsTemplate.Trim(),
                TokenBudgetLimit: request.TokenBudgetLimit is > 0 ? request.TokenBudgetLimit : null,
                CreatedAtUtc: existing?.CreatedAtUtc ?? DateTimeOffset.UtcNow,
                CustomHomeDirectory: customHome);

            ResolveHomeDirectory(updated);

            var idx = _records.FindIndex(r => string.Equals(r.Id, id, StringComparison.Ordinal));
            if (idx >= 0) _records[idx] = updated;
            else _records.Add(updated);

            SaveLocked();
            return updated;
        }
    }

    public void Delete(string idOrWorkspaceId, bool deleteIsolatedHome = true)
    {
        var id = StripWorkspacePrefix(idOrWorkspaceId);
        ValidateRecordId(id);
        lock (_gate)
        {
            var removed = _records.RemoveAll(r => string.Equals(r.Id, id, StringComparison.Ordinal));
            if (removed == 0) throw new InvalidOperationException("未找到指定的智能体配置。");
            SaveLocked();
            if (deleteIsolatedHome)
            {
                var dir = ProfilePathSafety.GetContainedFullPath(_agentsRoot, id);
                ProfilePathSafety.ThrowIfReparsePath(dir);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }

    public IReadOnlyList<CodexWorkspace> BuildWorkspaces()
    {
        var records = List();
        var list = new List<CodexWorkspace>(records.Count);
        foreach (var rec in records)
        {
            var home = ResolveHomeDirectory(rec);
            var wsId = ToWorkspaceId(rec);
            if (rec.AgentKind == UniversalAgentKind.DirectApi)
            {
                var preset = UniversalAgentCatalog.GetApiPreset(rec.PresetId);
                var subtitle = $"{preset.Badge} · 免客户端直连 ({rec.Model})";
                list.Add(new CodexWorkspace(
                    Id: wsId,
                    DisplayName: rec.DisplayName,
                    Kind: CodexWorkspaceKind.Managed,
                    ElectronUserData: home,
                    CodexHome: null,
                    Client: ClientKind.DirectApi,
                    SubtitleOverride: subtitle,
                    ProviderId: rec.Id,
                    DefaultModel: rec.Model));
            }
            else
            {
                var isAgyBacked = AntigravityAgentRunner.IsAntigravityBackedCliRecord(rec, home);
                var effectivePresetId = isAgyBacked ? "antigravity-cli" : rec.PresetId;
                var preset = UniversalAgentCatalog.GetCliPreset(effectivePresetId);
                var effectiveModel = isAgyBacked
                    && (string.IsNullOrWhiteSpace(rec.Model)
                        || string.Equals(rec.Model, "gemini-2.5-pro", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(rec.Model, "pro", StringComparison.OrdinalIgnoreCase))
                    ? "gemini-4-carbon"
                    : rec.Model;
                var modelPart = string.IsNullOrWhiteSpace(effectiveModel) ? "默认模型" : effectiveModel;
                var dirNote = !string.IsNullOrWhiteSpace(rec.CustomHomeDirectory) ? "免客户端 · 保留原目录" : "免客户端 · 独立环境";
                var subtitle = $"{preset.Badge} · {dirNote} ({modelPart})";
                list.Add(new CodexWorkspace(
                    Id: wsId,
                    DisplayName: rec.DisplayName,
                    Kind: CodexWorkspaceKind.Managed,
                    ElectronUserData: home,
                    CodexHome: preset.Id == "codex-cli" ? Path.Combine(home, ".codex") : null,
                    Client: ClientKind.MarketCli,
                    ClaudeHome: preset.Id == "claude-code" ? Path.Combine(home, ".claude") : null,
                    AntigravityHome: preset.Id is "gemini-cli" or "antigravity-cli" ? home : null,
                    SubtitleOverride: subtitle,
                    ProviderId: rec.Id,
                    DefaultModel: string.IsNullOrWhiteSpace(effectiveModel) ? null : effectiveModel));
            }
        }
        return list;
    }

    public IReadOnlyDictionary<string, string> CreateIsolatedEnvironment(UniversalAgentRecord record)
    {
        var home = ResolveHomeDirectory(record);
        var roamingDir = Path.Combine(home, "AppData", "Roaming");
        var xdgDir = Path.Combine(home, ".config");
        Directory.CreateDirectory(roamingDir);
        Directory.CreateDirectory(xdgDir);

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["USERPROFILE"] = home,
            ["HOME"] = home,
            ["APPDATA"] = roamingDir,
            ["XDG_CONFIG_HOME"] = xdgDir,
        };

        var plainKey = DpapiSecretProtector.Unprotect(record.EncryptedApiKey);
        if (record.AgentKind == UniversalAgentKind.MarketCli)
        {
            var preset = UniversalAgentCatalog.GetCliPreset(record.PresetId);
            if (preset.Id == "codex-cli")
            {
                var codexHome = Path.Combine(home, ".codex");
                Directory.CreateDirectory(codexHome);
                env["CODEX_HOME"] = codexHome;
            }
            else if (preset.Id == "claude-code")
            {
                var claudeDir = Path.Combine(home, ".claude");
                Directory.CreateDirectory(claudeDir);
                env["CLAUDE_CONFIG_DIR"] = claudeDir;
            }
            else if (preset.Id is "gemini-cli" or "antigravity-cli")
            {
                var geminiDir = Path.Combine(home, ".gemini");
                Directory.CreateDirectory(geminiDir);
                Directory.CreateDirectory(Path.Combine(geminiDir, "antigravity"));
                env["ANTIGRAVITY_GEMINI_DIR"] = geminiDir;
                env["GEMINI_CLI_HOME"] = home;
                env["GEMINI_FORCE_FILE_STORAGE"] = "true";

                var defaultUserHome = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                if (PathIdentity.Equals(home, defaultUserHome))
                {
                    env.Remove("ANTIGRAVITY_CREDENTIAL_SCOPE");
                    env.Remove("WSL_INTEROP");
                }
                else
                {
                    var localAppData = Path.Combine(home, "AppData", "Local");
                    Directory.CreateDirectory(localAppData);
                    var parentDirName = Path.GetFileName(Path.GetDirectoryName(home) ?? string.Empty);
                    var scopeSuffix = !string.IsNullOrWhiteSpace(parentDirName) && home.EndsWith("antigravity-home", StringComparison.OrdinalIgnoreCase)
                        ? parentDirName
                        : "cli-" + record.Id;
                    env["LOCALAPPDATA"] = localAppData;
                    env["ANTIGRAVITY_APP_DATA_DIR"] = "antigravity";
                    env["ANTIGRAVITY_CREDENTIAL_SCOPE"] = "codexmultilauncher-" + scopeSuffix;
                    env["WSL_INTEROP"] = "1";
                }
            }

            if (!string.IsNullOrWhiteSpace(plainKey) && !string.IsNullOrWhiteSpace(preset.ApiKeyEnvVar))
                env[preset.ApiKeyEnvVar!] = plainKey;
            if (!string.IsNullOrWhiteSpace(record.BaseUrl) && !string.IsNullOrWhiteSpace(preset.BaseUrlEnvVar))
                env[preset.BaseUrlEnvVar!] = record.BaseUrl;
        }

        return env;
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        ProfilePathSafety.ThrowIfReparsePath(_storePath);
        var tmp = _storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(_records, SerializerOptions);
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, _storePath, overwrite: true);
    }

    private static void ValidateRecordId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !id.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'))
            throw new InvalidOperationException("智能体标识无效。");
    }
}
