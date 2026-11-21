using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CodexMultiLauncher;

internal static class AdeFeaturesTests
{
    public static async Task<bool> TryRunAcpMockChildAsync(string[] args)
    {
        if (!args.Contains("--acp-mock-child")) return false;
        await using var stdIn = Console.OpenStandardInput();
        await using var stdOut = Console.OpenStandardOutput();

        async Task WriteJsonAsync(object obj) =>
            await AntigravityAgentRunner.WriteJsonRpcMessageAsync(stdOut, JsonSerializer.Serialize(obj), CancellationToken.None);

        while (await AntigravityAgentRunner.ReadNextJsonRpcMessageAsync(stdIn, CancellationToken.None) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var mProp)) continue;
            var method = mProp.GetString();
            var id = root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var iv) ? iv : 0;

            if (method == "initialize")
            {
                await WriteJsonAsync(new { jsonrpc = "2.0", id, result = new { protocolVersion = 1, agentCapabilities = new { } } });
            }
            else if (method == "session/new")
            {
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        sessionId = "sess_mock_1",
                        configOptions = new object[]
                        {
                            new { configId = "mode", category = "mode", type = "select", currentValue = "ask", options = new[] { new { value = "ask", name = "Ask" }, new { value = "code", name = "Code" } } },
                            new { configId = "model", category = "model", type = "select", currentValue = "pro", options = new[] { new { value = "pro", name = "Pro" }, new { value = "flash", name = "Flash" } } },
                            new { configId = "effort", category = "effort", type = "select", currentValue = "medium", options = new[] { new { value = "medium", name = "Medium" }, new { value = "max", name = "Max" } } },
                        },
                    },
                });
            }
            else if (method == "session/set_config_option")
            {
                await WriteJsonAsync(new { jsonrpc = "2.0", id, result = new { configOptions = Array.Empty<object>() } });
            }
            else if (method == "session/prompt")
            {
                // 1. Stream thought chunk
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0",
                    method = "session/update",
                    @params = new { sessionId = "sess_mock_1", update = new { sessionUpdate = "agent_thought_chunk", content = new { type = "text", text = "Planning via ACP protocol..." } } },
                });

                // 2. Stream tool call start
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0",
                    method = "session/update",
                    @params = new { sessionId = "sess_mock_1", update = new { sessionUpdate = "tool_call", toolCallId = "tc_1", title = "Write acp-hello.txt", kind = "edit", status = "running" } },
                });

                // 3. Request permission from client
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0",
                    id = 9001,
                    method = "session/request_permission",
                    @params = new
                    {
                        sessionId = "sess_mock_1",
                        toolCall = new { toolCallId = "tc_1" },
                        options = new[]
                        {
                            new { optionId = "allow-once", name = "Allow once", kind = "allow_once" },
                            new { optionId = "reject-once", name = "Reject", kind = "reject_once" },
                        },
                    },
                });
                var permReply = await AntigravityAgentRunner.ReadNextJsonRpcMessageAsync(stdIn, CancellationToken.None);
                if (permReply is null || !permReply.Contains("allow-once", StringComparison.Ordinal))
                    throw new InvalidOperationException("ACP client did not grant expected WorkspaceWrite permission.");

                // 4. Delegate fs/write_text_file to client
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0",
                    id = 9002,
                    method = "fs/write_text_file",
                    @params = new { path = "acp-hello.txt", content = "Hello from official ACP!" },
                });
                var writeReply = await AntigravityAgentRunner.ReadNextJsonRpcMessageAsync(stdIn, CancellationToken.None);
                if (writeReply is null || writeReply.Contains("\"error\"", StringComparison.Ordinal))
                    throw new InvalidOperationException("ACP client rejected contained fs/write_text_file.");

                // 5. Mark tool call completed & stream final message chunk
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0",
                    method = "session/update",
                    @params = new { sessionId = "sess_mock_1", update = new { sessionUpdate = "tool_call_update", toolCallId = "tc_1", title = "Write acp-hello.txt", kind = "edit", status = "completed" } },
                });
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0",
                    method = "session/update",
                    @params = new { sessionId = "sess_mock_1", update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "ACP_TURN_COMPLETE: Created acp-hello.txt" } } },
                });

                await WriteJsonAsync(new { jsonrpc = "2.0", id, result = new { stopReason = "end_turn" } });
                return true;
            }
        }
        return true;
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.AdeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            VerifyUiSettingsAndProfileLifecycle(root);
            VerifyUnifiedDiffParserAndReviewPrompt();
            VerifyClaudeStartInfoIsolation(root);
            await VerifyUniversalDirectApiAndCliAgentsAsync(root);
            await VerifyDynamicModelCatalogAndRunnerFlagsAsync(root);
            await VerifyAccountPoolFailoverAsync(root);
            await VerifyGitWorktreeAndArenaMergeAsync(root);
            await VerifyConPtyIsolationAsync(root);
            await VerifyV002BackendUpgradesAsync(root);
            Console.WriteLine("ADE multi-style settings, dynamic model/effort/speed catalogs, zero-download Direct API & Market CLI engines, DPAPI encryption, account-pool auto-failover, Git worktrees, Arena merge, Claude/Antigravity isolation, and native ConPTY contracts passed.");
        }
        finally
        {
            var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.AdeTests"));
            var target = Path.GetFullPath(root);
            if (target.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
            {
                try { Directory.Delete(target, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static void VerifyUiSettingsAndProfileLifecycle(string root)
    {
        var subRoot = Path.Combine(root, "settings-profiles");
        var store = new UiSettingsStore(subRoot);
        var saved = store.Save(new UiSettings("matrix", true, 88, "workspace-write", subRoot, "http://localhost:5173", true, ["original-claude", "original-claude"]));
        var loaded = store.Load();
        Check(saved.Theme == "matrix" && loaded.Theme == "matrix" && loaded.FailoverThresholdPercent == 88, "UI theme and failover threshold must round-trip.");
        Check(loaded.HiddenWorkspaceIds is { Count: 1 } && loaded.HiddenWorkspaceIds[0] == "original-claude", "HiddenWorkspaceIds must deduplicate and persist.");
        Check(store.Save(new UiSettings("invalid-theme", false, 150, Language: "EN")).Language == "en", "Language setting must normalize and round-trip.");
        Check(store.Save(new UiSettings("invalid-theme", false, 150, Language: "fr")) is { Theme: "obsidian", Language: "zh" }, "Unsupported theme and language must normalize safely.");

        var profiles = new ProfileService(subRoot);
        var initial = profiles.LoadOrCreateDefaults();
        var extra = profiles.CreateProfile("备用账号", ClientKind.Claude);
        var renamed = profiles.RenameProfile(extra.Id, "Claude 主力号");
        Check(renamed.DisplayName == "Claude 主力号", "Profile rename must persist normalized display name.");

        var activeList = new List<CodexWorkspace>
        {
            new(initial[0].Id, "账号 1", CodexWorkspaceKind.Managed, "a1", "a1", Client: ClientKind.Codex),
            new(extra.Id, "Claude 主力号", CodexWorkspaceKind.Managed, "c1", "c1", Client: ClientKind.Claude),
        };
        var coord = new GroupChatCoordinator(new GroupChatStore(subRoot), () => activeList);
        var room = coord.CreateRoom(subRoot, "测试账号清理");
        Check(room.Agents.Count == 2, "Room should initially bind both active accounts.");

        activeList.RemoveAt(1);
        profiles.DeleteProfile(extra.Id, deleteStorageDirectory: true);
        Check(!Directory.Exists(profiles.GetPaths(extra).ProfileDirectory), "DeleteProfile with deleteStorageDirectory must remove contained folder.");

        coord.SyncRosterWithWorkspaces();
        Check(room.Agents.Count == 1 && room.Agents[0].WorkspaceId == initial[0].Id, "Deleting an account must immediately prune it from room @ rosters.");

        profiles.DeleteProfile(initial[0].Id);
        profiles.DeleteProfile(initial[1].Id);
        Check(profiles.LoadOrCreateDefaults().Count == 0, "Users must be allowed to remove all managed desktop profiles cleanly.");
    }

    private static void VerifyUnifiedDiffParserAndReviewPrompt()
    {
        const string samplePatch = """
            diff --git a/src/Service.cs b/src/Service.cs
            index 1111111..2222222 100644
            --- a/src/Service.cs
            +++ b/src/Service.cs
            @@ -10,3 +10,4 @@ public class Service
                 public void Execute()
                 {
            -        OldCall();
            +        NewCall();
            +        AuditLog();
                 }
            """;

        var parsed = GitWorktreeService.ParseUnifiedDiff(samplePatch);
        Check(parsed.Count == 1 && parsed[0].FilePath == "src/Service.cs", "Diff parser must extract modified file path.");
        Check(parsed[0].Additions == 2 && parsed[0].Deletions == 1, "Diff parser must count additions and deletions accurately.");
        Check(parsed[0].Hunks.Count == 1 && parsed[0].Hunks[0].Lines.Any(l => l.Kind == "add" && l.NewLine == 12), "Diff parser must track exact new line numbers.");

        var reviewPrompt = GitWorktreeService.BuildInlineReviewPrompt(
            "GPT1",
            "请补充异常保护",
            [new DiffLineComment("src/Service.cs", 12, "new", "NewCall();", "此处需要传入 CancellationToken")]);
        Check(reviewPrompt.StartsWith("@GPT1 ", StringComparison.Ordinal)
            && reviewPrompt.Contains("src/Service.cs", StringComparison.Ordinal)
            && reviewPrompt.Contains("CancellationToken", StringComparison.Ordinal), "Inline review builder must address target agent with exact file/line comment.");
    }

    private static void VerifyClaudeStartInfoIsolation(string root)
    {
        var subRoot = Path.Combine(root, "claude-cli");
        var profiles = new ProfileService(subRoot);
        profiles.LoadOrCreateDefaults();
        var claudeProfile = profiles.CreateProfile("Claude 协同号", ClientKind.Claude);
        var catalog = new WorkspaceCatalog(subRoot);
        catalog.Reload(profiles.LoadOrCreateDefaults());
        var ws = catalog.FindById(claudeProfile.Id)!;

        var agent = new GroupChatAgent(Guid.NewGuid().ToString("N"), "Claude1", ws.Id, "claude-sonnet-4-5");
        var run = new GroupChatRun(agent, ws, subRoot, "hello", ChatSandboxMode.WorkspaceWrite);
        var psi = ClaudeCliRunner.CreateStartInfo(Path.Combine(subRoot, "claude.exe"), run);
        Check(psi.Environment["CLAUDE_CONFIG_DIR"] == ws.ClaudeHome, "Claude CLI runner must isolate CLAUDE_CONFIG_DIR.");
        Check(!psi.Environment.ContainsKey("ANTHROPIC_API_KEY"), "Inherited ANTHROPIC_API_KEY must be stripped.");
        Check(psi.ArgumentList.Contains("acceptEdits"), "WorkspaceWrite mode must map to acceptEdits permission mode.");
    }

    private static async Task VerifyUniversalDirectApiAndCliAgentsAsync(string root)
    {
        var subRoot = Path.Combine(root, "universal-agents");
        var workDir = Path.Combine(subRoot, "workspace");
        Directory.CreateDirectory(workDir);

        // 1. Verify DPAPI encryption & zero-plaintext persistence on disk
        const string sampleSecret = "mock-secret-key-fixture-987654";
        var store = new AgentProviderStore(subRoot);
        var deepseek = store.Upsert(new UniversalAgentUpsertRequest(
            Id: null,
            DisplayName: "DeepSeek直连",
            AgentKind: UniversalAgentKind.DirectApi,
            PresetId: "deepseek",
            Protocol: ApiProtocolKind.OpenAiCompatible,
            BaseUrl: "https://mock.deepseek.local/v1",
            Model: "deepseek-chat",
            ApiKeyPlaintext: sampleSecret,
            TokenBudgetLimit: 100_000));

        var gemini = store.Upsert(new UniversalAgentUpsertRequest(
            Id: null,
            DisplayName: "Gemini防串号",
            AgentKind: UniversalAgentKind.DirectApi,
            PresetId: "gemini",
            Protocol: ApiProtocolKind.GeminiNative,
            BaseUrl: "https://mock.gemini.local",
            Model: "gemini-2.5-pro",
            ApiKeyPlaintext: "mock-gemini-key-fixture-00012345"));

        var rawStoreJson = File.ReadAllText(Path.Combine(subRoot, "data", "agent-providers.json"), Encoding.UTF8);
        Check(!rawStoreJson.Contains(sampleSecret, StringComparison.Ordinal) && rawStoreJson.Contains("dpapi:", StringComparison.Ordinal),
            "API keys on disk must be DPAPI-encrypted and never stored in plaintext.");
        Check(DpapiSecretProtector.Unprotect(deepseek.EncryptedApiKey) == sampleSecret, "DPAPI must round-trip API secret accurately.");

        // 2. Verify BuiltInApiAgentRunner autonomous workspace tool loop (OpenAI-compatible + Gemini Native + Sandbox guard)
        var pool = new AccountPoolService(agentProviders: store);
        var callCounter = 0;
        using var http = new HttpClient(new DelegateHttpHandler(async req =>
        {
            var step = Interlocked.Increment(ref callCounter);
            var body = await req.Content!.ReadAsStringAsync();

            if (req.RequestUri!.Host == "mock.gemini.local")
            {
                Check(req.Headers.Contains("x-goog-api-key"), "Gemini native protocol must send x-goog-api-key header instead of leaking key in URL.");
                if (!body.Contains("functionResponse", StringComparison.Ordinal))
                {
                    return JsonHttp("""
                        {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"list_files","args":{"path":"."}}}]}}],"usageMetadata":{"promptTokenCount":120,"candidatesTokenCount":30}}
                        """);
                }
                return JsonHttp("""
                    {"candidates":[{"content":{"role":"model","parts":[{"text":"Gemini 已免客户端完成目录扫描！"}]}}],"usageMetadata":{"promptTokenCount":180,"candidatesTokenCount":45}}
                    """);
            }

            if (body.Contains("escape_test", StringComparison.Ordinal) && !body.Contains("\"role\":\"tool\"", StringComparison.Ordinal))
            {
                return JsonHttp("""
                    {"choices":[{"message":{"role":"assistant","content":"","tool_calls":[{"id":"c_esc","type":"function","function":{"name":"write_file","arguments":"{\"path\":\"../escaped.txt\",\"content\":\"bad\"}"}}]}}],"usage":{"prompt_tokens":50,"completion_tokens":20}}
                    """);
            }
            if (body.Contains("readonly_test", StringComparison.Ordinal) && !body.Contains("\"role\":\"tool\"", StringComparison.Ordinal))
            {
                return JsonHttp("""
                    {"choices":[{"message":{"role":"assistant","content":"","tool_calls":[{"id":"c_ro","type":"function","function":{"name":"write_file","arguments":"{\"path\":\"blocked.txt\",\"content\":\"no\"}"}}]}}],"usage":{"prompt_tokens":50,"completion_tokens":20}}
                    """);
            }
            if (!body.Contains("\"role\":\"tool\"", StringComparison.Ordinal))
            {
                return JsonHttp("""
                    {"choices":[{"message":{"role":"assistant","content":"","tool_calls":[{"id":"call_1","type":"function","function":{"name":"write_file","arguments":"{\"path\":\"hello_direct.txt\",\"content\":\"Created without any desktop client!\"}"}}]}}],"usage":{"prompt_tokens":400,"completion_tokens":100}}
                    """);
            }
            return JsonHttp("""
                {"choices":[{"message":{"role":"assistant","content":"已通过内置 C# Agent 工具链成功写入 hello_direct.txt！"}}],"usage":{"prompt_tokens":500,"completion_tokens":150}}
                """);
        }));

        var apiRunner = new BuiltInApiAgentRunner(store, pool.RecordTokenUsage, http);
        var builtWorkspaces = store.BuildWorkspaces();
        var dsWs = builtWorkspaces.First(w => w.ProviderId == deepseek.Id);
        var gemWs = builtWorkspaces.First(w => w.ProviderId == gemini.Id);

        // WorkspaceWrite creates file via built-in tool loop
        var writeReply = await apiRunner.RunAsync(
            new GroupChatRun(new GroupChatAgent("a1", "DS", dsWs.Id), dsWs, workDir, "请写入文件", ChatSandboxMode.WorkspaceWrite),
            new Progress<GroupChatUpdate>(),
            CancellationToken.None);
        Check(writeReply.Contains("hello_direct.txt", StringComparison.Ordinal)
            && File.Exists(Path.Combine(workDir, "hello_direct.txt")), "Zero-download Direct API agent must execute write_file in WorkspaceWrite mode.");

        // ReadOnly blocks write_file
        await apiRunner.RunAsync(
            new GroupChatRun(new GroupChatAgent("a1", "DS", dsWs.Id), dsWs, workDir, "readonly_test", ChatSandboxMode.ReadOnly),
            new Progress<GroupChatUpdate>(),
            CancellationToken.None);
        Check(!File.Exists(Path.Combine(workDir, "blocked.txt")), "ReadOnly mode must block write_file in BuiltInApiAgentRunner.");

        // Path containment blocks ../escaped.txt
        await apiRunner.RunAsync(
            new GroupChatRun(new GroupChatAgent("a1", "DS", dsWs.Id), dsWs, workDir, "escape_test", ChatSandboxMode.WorkspaceWrite),
            new Progress<GroupChatUpdate>(),
            CancellationToken.None);
        Check(!File.Exists(Path.Combine(subRoot, "escaped.txt")), "Workspace containment must block '../' path traversal in BuiltInApiAgentRunner.");

        // Gemini Native protocol tool loop works end-to-end
        var gemReply = await apiRunner.RunAsync(
            new GroupChatRun(new GroupChatAgent("a2", "Gem", gemWs.Id), gemWs, workDir, "扫描目录", ChatSandboxMode.ReadOnly),
            new Progress<GroupChatUpdate>(),
            CancellationToken.None);
        Check(gemReply.Contains("Gemini 已免客户端完成目录扫描", StringComparison.Ordinal), "Gemini native tool loop must complete functionCall -> functionResponse.");

        // Token budget battery telemetry recorded accurately
        var summary = pool.BuildSummary(builtWorkspaces, 92);
        var dsEntry = summary.Entries.First(e => e.WorkspaceId == dsWs.Id);
        Check(dsEntry.SessionInputTokens > 0 && dsEntry.PrimaryRemainingPercent < 100m, "Direct API token budget remaining percentage must update after turns.");

        // 2b. Verify real-time SSE streaming (reasoning_content + content chunks) & Antigravity daemon hub_*.json discovery
        var sseUpdates = new List<GroupChatUpdate>();
        using var sseHttp = new HttpClient(new DelegateHttpHandler(_ =>
        {
            const string ssePayload =
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"先检查目录结构…\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"再确认入口函数。\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"流式回复\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"已完成！\"}}],\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":12}}\n\n" +
                "data: [DONE]\n\n";
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream"),
            };
            return Task.FromResult(resp);
        }));
        var sseRunner = new BuiltInApiAgentRunner(store, null, sseHttp);
        var streamedAnswer = await sseRunner.RunAsync(
            new GroupChatRun(new GroupChatAgent("s1", "DS", dsWs.Id), dsWs, workDir, "测试流式思考", ChatSandboxMode.ReadOnly),
            new SynchronousProgress(u => sseUpdates.Add(u)),
            CancellationToken.None);
        Check(streamedAnswer == "流式回复已完成！"
            && sseUpdates.Any(u => u.Thinking is not null && u.Thinking.Contains("先检查目录结构", StringComparison.Ordinal)),
            "Direct API SSE runner must stream real-time reasoning_content thinking and incremental answer chunks.");

        // 3. Verify Market CLI headless isolated StartInfo construction
        var fakeCliExe = Path.Combine(subRoot, "mock-gemini.cmd");
        File.WriteAllText(fakeCliExe, "@echo off\r\necho CLI_OK\r\n");
        var cliRecord = store.Upsert(new UniversalAgentUpsertRequest(
            Id: null,
            DisplayName: "GeminiCLI一号",
            AgentKind: UniversalAgentKind.MarketCli,
            PresetId: "gemini-cli",
            Protocol: ApiProtocolKind.OpenAiCompatible,
            BaseUrl: null,
            Model: "gemini-2.5-flash",
            ApiKeyPlaintext: "AIzaCliIsolatedKey999",
            CustomCliExecutable: fakeCliExe));
        var cliWs = store.BuildWorkspaces().First(w => w.ProviderId == cliRecord.Id);
        var (cliPsi, _) = GenericCliAgentRunner.BuildCliStartInfo(
            cliRecord,
            store,
            new GroupChatRun(new GroupChatAgent("c1", "GemCLI", cliWs.Id), cliWs, workDir, "test prompt", ChatSandboxMode.WorkspaceWrite));
        Check(cliPsi.CreateNoWindow
            && cliPsi.Environment["GEMINI_API_KEY"] == "AIzaCliIsolatedKey999"
            && cliPsi.Environment["USERPROFILE"] == store.GetIsolatedHomeDirectory(cliRecord.Id)
            && cliPsi.Environment["GEMINI_FORCE_FILE_STORAGE"] == "true"
            && cliPsi.ArgumentList.Contains("--yolo"),
            "Market CLI runner must run headlessly with isolated USERPROFILE/APPDATA, GEMINI_FORCE_FILE_STORAGE=true, and decrypted GEMINI_API_KEY.");

        // 4. Verify migrating an existing Antigravity directory to clientless preserves CustomHomeDirectory, recognizes Antigravity auth state, and never deletes it on record removal
        var preservedAntigravityHome = Path.Combine(subRoot, "profile-abc123", "antigravity-home");
        var preservedAgySub = Path.Combine(preservedAntigravityHome, ".gemini", "antigravity");
        Directory.CreateDirectory(preservedAgySub);
        File.WriteAllText(Path.Combine(preservedAntigravityHome, ".gemini", "marker.txt"), "keep-me");
        File.WriteAllText(Path.Combine(preservedAgySub, "antigravity_state.pbtxt"), new string('x', 64));
        var migrated = store.Upsert(new UniversalAgentUpsertRequest(
            Id: null,
            DisplayName: "反重力免客户端迁移号",
            AgentKind: UniversalAgentKind.MarketCli,
            PresetId: "gemini-cli",
            Protocol: ApiProtocolKind.OpenAiCompatible,
            BaseUrl: null,
            Model: null,
            ApiKeyPlaintext: null,
            CustomCliExecutable: fakeCliExe,
            CustomHomeDirectory: preservedAntigravityHome));
        var migratedEnv = store.CreateIsolatedEnvironment(migrated);
        Check(migratedEnv["USERPROFILE"] == Path.GetFullPath(preservedAntigravityHome)
            && migratedEnv["GEMINI_CLI_HOME"] == Path.GetFullPath(preservedAntigravityHome)
            && migratedEnv["ANTIGRAVITY_CREDENTIAL_SCOPE"] == "codexmultilauncher-profile-abc123",
            "Clientless migration must bind USERPROFILE, GEMINI_CLI_HOME, and matching profile credential scope.");

        var migratedSummary = pool.BuildSummary(store.BuildWorkspaces(), 92);
        var migratedEntry = migratedSummary.Entries.First(e => e.WorkspaceId == AgentProviderStore.ToWorkspaceId(migrated));
        Check(migratedEntry.AuthReady
            && migratedEntry.Health == AccountQuotaHealth.Healthy
            && migratedEntry.Plan == "AGY ACP",
            "Migrated Antigravity home must detect as AuthReady and AGY ACP.");

        store.Delete(migrated.Id, deleteIsolatedHome: true);
        Check(File.Exists(Path.Combine(preservedAntigravityHome, ".gemini", "marker.txt")),
            "Deleting a migrated clientless agent must NEVER delete the user's preserved CustomHomeDirectory.");
    }

    private static HttpResponseMessage JsonHttp(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static async Task VerifyAccountPoolFailoverAsync(string root)
    {
        var subRoot = Path.Combine(root, "failover");
        Directory.CreateDirectory(subRoot);
        var profiles = new ProfileService(subRoot).LoadOrCreateDefaults();
        var catalog = new WorkspaceCatalog(subRoot);
        catalog.Reload(profiles);

        var pool = new AccountPoolService();
        var now = DateTimeOffset.UtcNow;
        // Idle authenticated account with null PrimaryUsedPercent must report 100% remaining (never unknown)
        pool.RecordSnapshot(profiles[0].Id, new CodexUsageSnapshot(CodexUsageState.Available, "plus", "a@test.local", "Plus", null, now.AddHours(3), null, now.AddDays(3), null, false, false, null, 1000m, now));
        pool.RecordSnapshot(profiles[1].Id, new CodexUsageSnapshot(CodexUsageState.Available, "pro", "b@test.local", "Pro", 12m, now.AddHours(4), 15m, now.AddDays(4), null, false, false, null, 2000m, now));
        var initialSummary = pool.BuildSummary(catalog.Workspaces, 90);
        Check(initialSummary.Entries.First(e => e.WorkspaceId == profiles[0].Id).PrimaryRemainingPercent == 100m, "Idle authenticated Codex account must report 100% remaining quota.");

        var attemptedWorkspaces = new List<string>();
        var runner = new DelegateRunner((req, _, _) =>
        {
            attemptedWorkspaces.Add(req.Workspace.Id);
            if (req.Workspace.Id == profiles[0].Id)
                throw new InvalidOperationException("触发配额或限流限制（429）：rate_limit_exceeded");
            return Task.FromResult("已由备用账号无缝完成任务");
        });

        var coordinator = new GroupChatCoordinator(
            new GroupChatStore(subRoot),
            () => catalog.Workspaces,
            runner,
            pool,
            () => new UiSettings(AutoFailoverEnabled: true, FailoverThresholdPercent: 90));

        var room = coordinator.CreateRoom(subRoot, "故障转移测试", ChatSandboxMode.WorkspaceWrite);
        await coordinator.SendAsync(room, "@GPT1 测试 429 自动接力");

        var reply = room.Messages.Last();
        Check(attemptedWorkspaces.SequenceEqual([profiles[0].Id, profiles[1].Id]), "Coordinator must retry on the healthiest fallback account after 429.");
        Check(reply.State == ChatMessageState.Completed
            && reply.WorkspaceId == profiles[1].Id
            && !string.IsNullOrWhiteSpace(reply.FailoverNotice), "Reply must record fallback workspace identity and failover notice.");

        // Verify SyncRosterWithWorkspaces auto-enrolls newly added Claude & Antigravity accounts
        var svc = new ProfileService(subRoot);
        var claudeProf = svc.CreateProfile("claude3", ClientKind.Claude);
        var agyProf = svc.CreateProfile("新账号", ClientKind.Antigravity);
        catalog.Reload(svc.LoadOrCreateDefaults());
        var legacyRoom = coordinator.CreateRoom(subRoot, "我的群聊", ChatSandboxMode.ReadOnly);
        coordinator.SyncRosterWithWorkspaces(upgradeEmptyDefaultRoomToWrite: true);
        Check(legacyRoom.SandboxMode == ChatSandboxMode.WorkspaceWrite, "Empty legacy room must auto-upgrade to full Agent WorkspaceWrite mode.");
        Check(legacyRoom.Agents.Any(a => a.WorkspaceId == claudeProf.Id) && legacyRoom.Agents.Any(a => a.WorkspaceId == agyProf.Id),
            "All configured Claude and Antigravity accounts must auto-enroll into @ roster.");
    }

    private static async Task VerifyGitWorktreeAndArenaMergeAsync(string root)
    {
        var repoDir = Path.Combine(root, "arena-repo");
        Directory.CreateDirectory(repoDir);
        var gitExe = GitWorktreeService.FindGitExecutable();
        RunGitSync(gitExe, repoDir, "init");
        File.WriteAllText(Path.Combine(repoDir, "README.md"), "# Demo Repo\n", new UTF8Encoding(false));
        RunGitSync(gitExe, repoDir, "add", "-A");
        RunGitSync(gitExe, repoDir, "-c", "user.name=Test", "-c", "user.email=test@local", "commit", "-m", "Initial commit");

        var launcherRoot = Path.Combine(root, "arena-launcher");
        Directory.CreateDirectory(launcherRoot);
        var profiles = new ProfileService(launcherRoot).LoadOrCreateDefaults();
        var catalog = new WorkspaceCatalog(launcherRoot);
        catalog.Reload(profiles);

        var gitService = new GitWorktreeService(launcherRoot);
        var runner = new DelegateRunner((req, _, _) =>
        {
            var fileName = req.Workspace.Id == profiles[0].Id ? "SolutionA.txt" : "SolutionB.txt";
            File.WriteAllText(Path.Combine(req.WorkingDirectory, fileName), $"Implemented by {req.Workspace.DisplayName}\n", new UTF8Encoding(false));
            return Task.FromResult($"已完成方案：{fileName}");
        });

        var arena = new ArenaService(launcherRoot, gitService, () => catalog.Workspaces, runner);
        var session = await arena.StartRaceAsync(
            "并行实现方案对比",
            repoDir,
            "请新增实现文件",
            [new ArenaContestantSpec("选手 A", profiles[0].Id), new ArenaContestantSpec("选手 B", profiles[1].Id)],
            verificationCommand: null);

        var deadline = Stopwatch.StartNew();
        while (arena.IsRunning(session.Id) && deadline.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(80);

        Check(!arena.IsRunning(session.Id), "Arena background execution must finish within timeout.");
        Check(session.Contestants.All(c => c.State == ArenaRunState.Completed && c.ChangedFiles == 1 && c.Additions == 1), "Each Arena contestant must produce isolated worktree diff stats.");
        Check(!File.Exists(Path.Combine(repoDir, "SolutionA.txt")) && !File.Exists(Path.Combine(repoDir, "SolutionB.txt")), "Main repository must remain untouched before winner merge.");

        await arena.MergeWinnerAsync(session.Id, session.Contestants[1].Id, CancellationToken.None);
        Check(File.Exists(Path.Combine(repoDir, "SolutionB.txt")) && !File.Exists(Path.Combine(repoDir, "SolutionA.txt")), "Merging winner B must bring only SolutionB.txt into main repository.");
    }

    private static async Task VerifyConPtyIsolationAsync(string root)
    {
        var subRoot = Path.Combine(root, "conpty");
        var profiles = new ProfileService(subRoot).LoadOrCreateDefaults();
        var catalog = new WorkspaceCatalog(subRoot);
        catalog.Reload(profiles);
        var ws = catalog.FindById(profiles[0].Id)!;

        using var pty = new ConPtyTerminalService();
        var output = new StringBuilder();
        var gotExpected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pty.OutputReceived += (_, chunk) =>
        {
            lock (output)
            {
                output.Append(chunk);
                if (output.ToString().Contains(ws.CodexHome!, StringComparison.OrdinalIgnoreCase))
                    gotExpected.TrySetResult();
            }
        };

        var session = pty.CreateSession("PTY 测试", ws, subRoot, "cmd", 100, 25);
        pty.Resize(session.Id, 110, 30);
        pty.WriteInput(session.Id, "echo %CODEX_HOME%\r\n");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await gotExpected.Task.WaitAsync(cts.Token);
        pty.CloseSession(session.Id);
        Check(pty.ListSessions().Count == 0, "Closed ConPTY session must be removed cleanly.");
    }

    private static async Task VerifyDynamicModelCatalogAndRunnerFlagsAsync(string root)
    {
        var subRoot = Path.Combine(root, "model-catalog");
        var codexHome = Path.Combine(subRoot, "codex-home");
        var workDir = Path.Combine(subRoot, "workspace");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(workDir);

        // 1. Codex app-server `model/list` + config.toml + runner CLI flags (-c model_reasoning_effort & -c service_tier)
        using var codexRpcDoc = JsonDocument.Parse("""
            {
              "result": {
                "data": [
                  {
                    "id": "gpt-6.2-sol",
                    "displayName": "GPT-6.2-Sol",
                    "description": "Fast, go-to model for coding and everyday work.",
                    "hidden": false,
                    "defaultReasoningEffort": "low",
                    "supportedReasoningEfforts": [
                      { "reasoningEffort": "low" },
                      { "reasoningEffort": "medium" },
                      { "reasoningEffort": "high" },
                      { "reasoningEffort": "xhigh" }
                    ],
                    "serviceTiers": [
                      { "id": "priority", "name": "Fast" },
                      { "id": "ultrafast", "name": "Ultrafast" }
                    ],
                    "isDefault": true
                  },
                  {
                    "id": "codex-auto-review",
                    "displayName": "Internal Hidden",
                    "hidden": true
                  }
                ]
              }
            }
            """);
        var codexCat = ModelCatalogService.ParseCodexAppServerModelList("ws-codex", codexRpcDoc.RootElement, "gpt-6.2-sol", "xhigh", "priority");
        Check(codexCat.Models.Count == 1
            && codexCat.Models[0].Id == "gpt-6.2-sol"
            && codexCat.Models[0].ReasoningEfforts.Count == 4
            && codexCat.Models[0].SpeedTiers.Count == 2
            && codexCat.DefaultEffort == "xhigh"
            && codexCat.DefaultSpeedTier == "priority",
            "Codex model/list parser must filter hidden models and extract supportedReasoningEfforts + serviceTiers.");

        var codexWs = new CodexWorkspace("ws-codex", "CodexPro", CodexWorkspaceKind.Managed, subRoot, codexHome, subRoot, ClientKind.Codex);
        var codexPsi = CodexGroupChatRunner.CreateStartInfo(
            "codex.exe",
            new GroupChatRun(
                new GroupChatAgent(Guid.NewGuid().ToString("N"), "GPT1", codexWs.Id, "gpt-6.2-sol", "xhigh", "ultrafast"),
                codexWs,
                workDir,
                "hello",
                ChatSandboxMode.WorkspaceWrite));
        Check(codexPsi.ArgumentList.Contains("--model")
            && codexPsi.ArgumentList.Contains("gpt-6.2-sol")
            && codexPsi.ArgumentList.Contains("model_reasoning_effort=\"xhigh\"")
            && codexPsi.ArgumentList.Contains("service_tier=\"ultrafast\""),
            "CodexGroupChatRunner must inject --model, -c model_reasoning_effort, and -c service_tier.");

        // 2. Official Antigravity ACP (`agy acp`) `session/new` `configOptions` discovery + full ACP JSON-RPC 2.0 session turn
        const string acpSessionNewJson = """
            {
              "sessionId": "sess_acp_001",
              "configOptions": [
                {
                  "configId": "mode",
                  "category": "mode",
                  "type": "select",
                  "currentValue": "code",
                  "options": [
                    { "value": "ask", "name": "Ask" },
                    { "value": "code", "name": "Code" }
                  ]
                },
                {
                  "configId": "model",
                  "category": "model",
                  "type": "select",
                  "currentValue": "pro",
                  "options": [
                    { "value": "pro", "name": "Gemini Pro (ACP)", "description": "Flagship coding model" },
                    { "value": "flash", "name": "Gemini Flash (ACP)", "description": "Fast low-latency model" },
                    { "value": "flash_lite", "name": "Gemini Flash Lite", "description": "Lightweight model" }
                  ]
                },
                {
                  "configId": "effort",
                  "category": "effort",
                  "type": "select",
                  "currentValue": "high",
                  "options": [
                    { "value": "low", "name": "Low" },
                    { "value": "medium", "name": "Medium" },
                    { "value": "high", "name": "High" },
                    { "value": "max", "name": "Max" }
                  ]
                }
              ]
            }
            """;
        var agyCat = ModelCatalogService.ParseAntigravityAcpSessionNewJson("ws-agy", acpSessionNewJson);
        Check(agyCat.Source == "live-acp"
            && agyCat.Models.Count == 3
            && agyCat.DefaultModelId == "pro"
            && agyCat.DefaultEffort == "high"
            && agyCat.Models[0].ReasoningEfforts.Any(e => e.Id == "max"),
            "Antigravity ACP catalog parser must extract models and effort options from official session/new configOptions.");

        // Verify effort-suffixed ACP model variants collapse cleanly into family entries (`gemini-4-carbon`, `orchestrator-neon`, `gpt-oss-120b-medium`)
        var acpVariantSessionJson = """
            {
              "sessionId": "sess-agy-variants",
              "configOptions": [
                {
                  "configId": "model",
                  "category": "model",
                  "currentValue": "gemini-4-carbon-high",
                  "options": [
                    { "value": "gemini-4-carbon-low", "name": "Gemini 4 Carbon Low" },
                    { "value": "gemini-4-carbon-medium", "name": "Gemini 4 Carbon Medium" },
                    { "value": "gemini-4-carbon-high", "name": "Gemini 4 Carbon High" },
                    { "value": "orchestrator-neon", "name": "Orchestrator (Neon)" },
                    { "value": "gpt-oss-120b-medium", "name": "GPT-OSS 120B (Medium)" }
                  ]
                }
              ]
            }
            """;
        var agyVarCat = ModelCatalogService.ParseAntigravityAcpSessionNewJson("ws-agy-var", acpVariantSessionJson);
        Check(agyVarCat.DefaultModelId == "gemini-4-carbon"
            && agyVarCat.DefaultEffort == "high"
            && agyVarCat.Models.Count == 3
            && agyVarCat.Models[0].DisplayName == "Gemini 4 Carbon"
            && agyVarCat.Models[0].ReasoningEfforts.Count == 3
            && agyVarCat.Models[1].ReasoningEfforts.Count == 0
            && agyVarCat.Models[2].ReasoningEfforts.Count == 0,
            "Effort-suffixed ACP model options must group multi-effort families cleanly while preserving standalone orchestrator/single-tier entries.");

        // Verify legacy migrated `gemini-cli` Antigravity records resolve to the 11-model Antigravity lineup and NEVER report `CreditsUnlimited == true`
        var migRoot = Path.Combine(subRoot, "migrated-agy-check");
        var migHome = Path.Combine(migRoot, "custom-agy-home");
        Directory.CreateDirectory(Path.Combine(migHome, ".gemini", "antigravity"));
        File.WriteAllText(Path.Combine(migHome, ".gemini", "antigravity", "state.json"), "{}");
        var migStore = new AgentProviderStore(migRoot);
        var migRec = migStore.Upsert(new UniversalAgentUpsertRequest(
            Id: "agy1legacy",
            DisplayName: "MigratedAgy",
            AgentKind: UniversalAgentKind.MarketCli,
            PresetId: "gemini-cli",
            Protocol: ApiProtocolKind.OpenAiCompatible,
            BaseUrl: null,
            Model: "gemini-2.5-pro",
            ApiKeyPlaintext: null,
            CustomHomeDirectory: migHome));
        Check(AntigravityAgentRunner.IsAntigravityBackedCliRecord(migRec, migHome), "Migrated gemini-cli record with Antigravity home must be recognized as Antigravity-backed.");
        var migWs = migStore.BuildWorkspaces().Single();
        Check(migWs.DefaultModel == "gemini-4-carbon", "Migrated Antigravity workspace must normalize legacy gemini-2.5-pro default model to gemini-4-carbon.");
        var migCatSvc = new ModelCatalogService(migStore);
        var migCat = await migCatSvc.GetCatalogAsync(migWs, forceRefresh: true, wakeDaemonIfNeeded: false, CancellationToken.None);
        Check(migCat.ProviderKind == "antigravity"
            && migCat.Models.Any(m => m.Id == "gemini-4-carbon")
            && migCat.Models.Any(m => m.Id == "gemini-4-neon")
            && migCat.Models.Any(m => m.Id == "gemini-pro-orchestrator-neon")
            && migCat.Models.Any(m => m.Id == "gemini-3.8-flash")
            && migCat.Models.Any(m => m.Id == "claude-opus-5-5")
            && migCat.Models.Any(m => m.Id == "gpt-oss-120b-medium"),
            "Migrated Antigravity workspace catalog must expose the real Antigravity Gemini 4 / Orchestrator / Claude / GPT-OSS lineup.");

        var migPool = new AccountPoolService(null, migStore);
        var migEntryBeforeUsage = migPool.BuildSummary([migWs]).Entries.Single();
        Check(migEntryBeforeUsage.CreditsUnlimited == false && migEntryBeforeUsage.PrimaryRemainingPercent is null,
            "Antigravity-backed accounts must NEVER report CreditsUnlimited=true or fake 100% remaining before official CLI usage is synced.");

        var parsedAgyUsage = AntigravityAgentRunner.ParseCliUsageJson("""
            {
              "conversation_id": "",
              "status": "SUCCESS",
              "command": {
                "name": "usage",
                "data": {
                  "groups": [
                    {
                      "name": "Gemini Models",
                      "buckets": [
                        { "id": "gemini-weekly", "window": "weekly", "remaining_fraction": 0.60, "reset_time": "2099-01-05T00:00:00Z" },
                        { "id": "gemini-5h", "window": "5h", "remaining_fraction": 0.72, "reset_time": "2099-01-01T00:00:00Z" }
                      ]
                    }
                  ]
                }
              }
            }
            """, DateTimeOffset.UtcNow);
        Check(parsedAgyUsage is not null && parsedAgyUsage.PrimaryUsedPercent == 28m && parsedAgyUsage.SecondaryUsedPercent == 40m,
            "Official `agy -p /usage --output-format json` parser must extract 5-hour and weekly quota buckets.");
        migPool.RecordAntigravityUsage(migWs.Id, parsedAgyUsage!);
        var migEntryAfterUsage = migPool.BuildSummary([migWs]).Entries.Single();
        Check(migEntryAfterUsage.PrimaryRemainingPercent == 72m && migEntryAfterUsage.SecondaryRemainingPercent == 60m && migEntryAfterUsage.CreditsUnlimited == false,
            "AccountPoolService must reflect official `agy /usage` 5h and 7d remaining quota percentages accurately.");

        // Verify full stdio ACP JSON-RPC 2.0 lifecycle (`initialize` -> `session/new` -> `session/set_config_option` -> `session/prompt` -> `session/update` + `session/request_permission` + `fs/write_text_file`)
        var selfDll = typeof(AdeFeaturesTests).Assembly.Location;
        var acpWorkDir = Path.Combine(subRoot, "acp-workspace");
        Directory.CreateDirectory(acpWorkDir);
        var acpWs = new CodexWorkspace(
            "ws-agy-acp",
            "AntigravityACP",
            CodexWorkspaceKind.Managed,
            Path.Combine(subRoot, "electron-user-data"),
            null,
            null,
            ClientKind.Antigravity,
            null,
            Path.Combine(subRoot, "antigravity-home"));
        string? latestThink = null;
        var acpSteps = new List<ChatExecutionStep>();
        var acpRunner = new AntigravityAgentRunner(run =>
        {
            var p = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = run.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            p.ArgumentList.Add(selfDll);
            p.ArgumentList.Add("--acp-mock-child");
            return p;
        });
        var acpReply = await acpRunner.RunAsync(
            new GroupChatRun(
                new GroupChatAgent("agy1", "AGY", acpWs.Id, "pro", "max"),
                acpWs,
                acpWorkDir,
                "Create acp-hello.txt via ACP protocol",
                ChatSandboxMode.WorkspaceWrite),
            new SynchronousProgress(u =>
            {
                if (!string.IsNullOrEmpty(u.Thinking)) latestThink = u.Thinking;
                if (u.Steps is { Count: > 0 }) acpSteps = u.Steps.ToList();
            }),
            CancellationToken.None);
        Check(acpReply.Contains("ACP_TURN_COMPLETE", StringComparison.Ordinal)
            && latestThink?.Contains("Planning via ACP", StringComparison.Ordinal) == true
            && acpSteps.Any(s => s.Title.Contains("Write acp-hello.txt", StringComparison.Ordinal) && s.Status == "completed")
            && File.ReadAllText(Path.Combine(acpWorkDir, "acp-hello.txt")) == "Hello from official ACP!",
            "AntigravityAgentRunner must execute full ACP JSON-RPC 2.0 handshake, configOptions, streaming thoughts/steps, permission grant, and contained fs/write_text_file.");

        // 3. Claude CLI --model, --effort, and fastMode flags
        var claudeWs = new CodexWorkspace("ws-claude", "ClaudePro", CodexWorkspaceKind.Managed, subRoot, null, subRoot, ClientKind.Claude, Path.Combine(subRoot, "claude-home"));
        var claudePsi = ClaudeCliRunner.CreateStartInfo(
            "claude.exe",
            new GroupChatRun(
                new GroupChatAgent(Guid.NewGuid().ToString("N"), "Claude1", claudeWs.Id, "opus", "xhigh", "fast"),
                claudeWs,
                workDir,
                "review",
                ChatSandboxMode.ReadOnly));
        Check(claudePsi.ArgumentList.Contains("--model")
            && claudePsi.ArgumentList.Contains("opus")
            && claudePsi.ArgumentList.Contains("--effort")
            && claudePsi.ArgumentList.Contains("max")
            && claudePsi.ArgumentList.Contains("--settings")
            && claudePsi.ArgumentList.Contains("{\"fastMode\":true}"),
            "ClaudeCliRunner must pass --model, normalized --effort, and fastMode settings.");

        // 4. Direct API live `/v1/models` discovery + reasoning_effort & service_tier payload injection
        var apiStore = new AgentProviderStore(subRoot);
        var oaiRecord = apiStore.Upsert(new UniversalAgentUpsertRequest(
            Id: null,
            DisplayName: "OpenAIDirect",
            AgentKind: UniversalAgentKind.DirectApi,
            PresetId: "openai",
            Protocol: ApiProtocolKind.OpenAiCompatible,
            BaseUrl: "https://api.openai.com/v1",
            Model: "gpt-5.4",
            ApiKeyPlaintext: "mock-openai-key-fixture-12345678"));
        string? capturedChatBody = null;
        using var mockHttp = new HttpClient(new DelegateHttpHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.EndsWith("/v1/models", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"gpt-5.4"},{"id":"o4-mini"},{"id":"text-embedding-3-large"}]}""", Encoding.UTF8, "application/json"),
                };
            }
            capturedChatBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"FAST_OK"}}]}""", Encoding.UTF8, "application/json"),
            };
        }));

        var catService = new ModelCatalogService(apiStore, mockHttp);
        var oaiWs = apiStore.BuildWorkspaces().First(w => w.ProviderId == oaiRecord.Id);
        var liveApiCat = await catService.GetCatalogAsync(oaiWs, forceRefresh: true);
        Check(liveApiCat.Source == "live-api"
            && liveApiCat.Models.Any(m => m.Id == "gpt-5.4")
            && liveApiCat.Models.Any(m => m.Id == "o4-mini")
            && !liveApiCat.Models.Any(m => m.Id.Contains("embedding", StringComparison.Ordinal)),
            "Direct API catalog discovery must fetch live /v1/models and filter out embedding models.");

        var directRunner = new BuiltInApiAgentRunner(apiStore, null, mockHttp);
        await directRunner.RunAsync(
            new GroupChatRun(new GroupChatAgent("d1", "OAI", oaiWs.Id, "gpt-5.4", "high", "priority"), oaiWs, workDir, "hi", ChatSandboxMode.ReadOnly),
            new Progress<GroupChatUpdate>(),
            CancellationToken.None);
        Check(capturedChatBody is not null
            && capturedChatBody.Contains("\"reasoning_effort\":\"high\"", StringComparison.Ordinal)
            && capturedChatBody.Contains("\"service_tier\":\"priority\"", StringComparison.Ordinal)
            && !capturedChatBody.Contains("\"temperature\"", StringComparison.Ordinal),
            "BuiltInApiAgentRunner must inject reasoning_effort and service_tier while omitting temperature on reasoning requests.");
    }

    private static async Task VerifyV002BackendUpgradesAsync(string root)
    {
        var subRoot = Path.Combine(root, "v002-upgrades");
        var workDir = Path.Combine(subRoot, "workspace");
        Directory.CreateDirectory(workDir);

        // 1. Verify GroupChatRouting incremental delta prompt & sliding-window history compaction
        var agentA = new GroupChatAgent("ag-a", "ClaudeA", "ws-a");
        var history = new List<GroupChatMessage>
        {
            new() { Role = ChatMessageRole.User, Sender = "你", State = ChatMessageState.Completed, Text = "第一轮超长背景上下文 OLD_TURN_1" },
            new() { Role = ChatMessageRole.Agent, AgentId = "ag-a", Sender = "ClaudeA", State = ChatMessageState.Completed, Text = "第一轮回复 OLD_REPLY_1" },
            new() { Role = ChatMessageRole.Agent, AgentId = "ag-b", Sender = "GPTB", State = ChatMessageState.Completed, Text = "队友补充 INTERVENING_PEER" },
        };
        var inc = GroupChatRouting.BuildIncrementalPrompt(agentA, history, "请继续第二轮");
        Check(!inc.Contains("OLD_TURN_1", StringComparison.Ordinal)
            && !inc.Contains("OLD_REPLY_1", StringComparison.Ordinal)
            && inc.Contains("INTERVENING_PEER", StringComparison.Ordinal)
            && inc.Contains("请继续第二轮", StringComparison.Ordinal),
            "BuildIncrementalPrompt must exclude turns already inside recipient's resumed session while including intervening peer messages.");

        var giantHistory = Enumerable.Range(0, 18)
            .Select(i => new GroupChatMessage
            {
                Role = i % 2 == 0 ? ChatMessageRole.User : ChatMessageRole.Agent,
                Sender = i % 2 == 0 ? "你" : "ClaudeA",
                State = ChatMessageState.Completed,
                Text = $"Turn-{i}: " + new string('A', 12_000),
            })
            .ToList();
        var compacted = GroupChatRouting.BuildPrompt(agentA, giantHistory, "最新问题", ChatSandboxMode.WorkspaceWrite);
        Check(compacted.Length <= GroupChatRouting.MaximumContextCharacters
            && compacted.Contains("滑动折叠", StringComparison.Ordinal)
            && compacted.Contains("最新问题", StringComparison.Ordinal),
            "BuildPrompt must compact oldest turns via sliding window instead of failing when history exceeds context budget.");

        // 2. Verify AccountPoolService unconfigured DirectApi guard & exact HTTP 429 boundary regex
        Check(AccountPoolService.IsQuotaOrRateLimitFailure("HTTP 429 Too Many Requests"), "HTTP 429 must trigger rate-limit failover.");
        Check(!AccountPoolService.IsQuotaOrRateLimitFailure("Connection refused at http://127.0.0.1:4290/v1"), "Port :4290 must NOT falsely trigger 429 rate-limit cooldown!");

        var store = new AgentProviderStore(subRoot);
        var configuredOai = store.Upsert(new UniversalAgentUpsertRequest(
            Id: null,
            DisplayName: "已填Key号",
            AgentKind: UniversalAgentKind.DirectApi,
            PresetId: "openai",
            Protocol: ApiProtocolKind.OpenAiCompatible,
            BaseUrl: "https://mock.oai.local/v1",
            Model: "gpt-5.4",
            ApiKeyPlaintext: "sk-valid-key-999"));
        var anthropicRec = store.Upsert(new UniversalAgentUpsertRequest(
            Id: null,
            DisplayName: "Claude直连",
            AgentKind: UniversalAgentKind.DirectApi,
            PresetId: "anthropic",
            Protocol: ApiProtocolKind.AnthropicMessages,
            BaseUrl: "https://mock.anthropic.local",
            Model: "claude-sonnet-4-5",
            ApiKeyPlaintext: "sk-ant-valid-999"));

        // Simulate a DirectApi record migrated from another PC / missing valid DPAPI key in agent-providers.json
        var migratedId = Guid.NewGuid().ToString("N");
        var storeJsonPath = Path.Combine(subRoot, "data", "agent-providers.json");
        var storeJsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var existingRecords = JsonSerializer.Deserialize<List<UniversalAgentRecord>>(File.ReadAllText(storeJsonPath), storeJsonOpts)!;
        existingRecords.Insert(0, new UniversalAgentRecord(
            Id: migratedId,
            DisplayName: "未填Key号",
            AgentKind: UniversalAgentKind.DirectApi,
            PresetId: "openai",
            Protocol: ApiProtocolKind.OpenAiCompatible,
            BaseUrl: "https://mock.oai.local/v1",
            Model: "gpt-5.4",
            EncryptedApiKey: "corrupted-or-other-machine-dpapi",
            MaskedApiKey: null,
            CustomCliExecutable: null,
            CustomCliArgsTemplate: null,
            TokenBudgetLimit: null,
            CreatedAtUtc: DateTimeOffset.UtcNow));
        File.WriteAllText(storeJsonPath, JsonSerializer.Serialize(existingRecords, storeJsonOpts));
        store.Reload();
        var unconfiguredOai = store.FindById(migratedId)!;

        using var pool = new AccountPoolService(agentProviders: store);
        var wsList = store.BuildWorkspaces();
        var unconfigWs = wsList.First(w => w.ProviderId == unconfiguredOai.Id);
        var configWs = wsList.First(w => w.ProviderId == configuredOai.Id);
        var anthWs = wsList.First(w => w.ProviderId == anthropicRec.Id);

        var sum = pool.BuildSummary(wsList, 92);
        Check(!sum.Entries.First(e => e.WorkspaceId == unconfigWs.Id).AuthReady, "DirectApi requiring key with null EncryptedApiKey must report AuthReady == false.");
        Check(sum.Entries.First(e => e.WorkspaceId == configWs.Id).AuthReady, "DirectApi with valid EncryptedApiKey must report AuthReady == true.");
        var fallback = pool.SelectFailoverWorkspace(wsList, anthWs.Id, null, 92);
        Check(fallback?.Id == configWs.Id, "SelectFailoverWorkspace must skip unconfigured DirectApi accounts and pick healthy configured DirectApi.");

        // 3. Verify BuiltInApiAgentRunner: CRLF-agnostic edit_file, multi-match protection, search_files, and Multimodal Vision (OpenAI + Anthropic)!
        File.WriteAllText(Path.Combine(workDir, "crlf.cs"), "line1\r\nvar x = 1;\r\nvar y = 2;\r\nvar dup = 0;\r\nvar dup = 0;\r\n", Encoding.ASCII);

        var stepIdx = 0;
        string? capturedOaiVisionBody = null;
        string? capturedAnthVisionBody = null;
        string? ambiguityToolResult = null;
        string? searchToolResult = null;

        using var v002Http = new HttpClient(new DelegateHttpHandler(async req =>
        {
            var body = await req.Content!.ReadAsStringAsync();
            if (req.RequestUri!.Host == "mock.anthropic.local")
            {
                capturedAnthVisionBody = body;
                return JsonHttp("""{"content":[{"type":"text","text":"ANTHROPIC_VISION_OK"}],"usage":{"input_tokens":50,"output_tokens":10}}""");
            }

            if (body.Contains("VISION_CHECK", StringComparison.Ordinal))
            {
                capturedOaiVisionBody = body;
                return JsonHttp("""{"choices":[{"message":{"role":"assistant","content":"OPENAI_VISION_OK"}}],"usage":{"prompt_tokens":40,"completion_tokens":8}}""");
            }

            var cur = Interlocked.Increment(ref stepIdx);
            if (cur == 1)
            {
                // LF target against CRLF file + ambiguous multi-match edit + search_files
                return JsonHttp("""
                    {"choices":[{"message":{"role":"assistant","content":"","tool_calls":[
                      {"id":"t1","type":"function","function":{"name":"edit_file","arguments":"{\"path\":\"crlf.cs\",\"target\":\"var x = 1;\\nvar y = 2;\",\"replacement\":\"var x = 42;\\nvar y = 99;\"}"}},
                      {"id":"t2","type":"function","function":{"name":"edit_file","arguments":"{\"path\":\"crlf.cs\",\"target\":\"var dup = 0;\",\"replacement\":\"var dup = 1;\"}"}},
                      {"id":"t3","type":"function","function":{"name":"search_files","arguments":"{\"query\":\"var x = 42\"}"}}
                    ]}}],"usage":{"prompt_tokens":100,"completion_tokens":40}}
                    """);
            }

            using var doc = JsonDocument.Parse(body);
            foreach (var msg in doc.RootElement.GetProperty("messages").EnumerateArray())
            {
                if (msg.GetProperty("role").GetString() == "tool")
                {
                    var tcId = msg.GetProperty("tool_call_id").GetString();
                    var content = msg.GetProperty("content").GetString();
                    if (tcId == "t2") ambiguityToolResult = content;
                    if (tcId == "t3") searchToolResult = content;
                }
            }
            return JsonHttp("""{"choices":[{"message":{"role":"assistant","content":"TOOLS_VERIFIED"}}],"usage":{"prompt_tokens":140,"completion_tokens":20}}""");
        }));

        var runner = new BuiltInApiAgentRunner(store, null, v002Http);
        var toolRes = await runner.RunAsync(
            new GroupChatRun(new GroupChatAgent("a1", "OAI", configWs.Id), configWs, workDir, "测试工具升级", ChatSandboxMode.WorkspaceWrite),
            new Progress<GroupChatUpdate>(),
            CancellationToken.None);
        var editedRaw = File.ReadAllText(Path.Combine(workDir, "crlf.cs"));
        Check(toolRes == "TOOLS_VERIFIED"
            && editedRaw.Contains("var x = 42;\r\nvar y = 99;\r\n", StringComparison.Ordinal),
            "edit_file must match LF old_text against CRLF files while preserving original CRLF line endings.");
        Check(ambiguityToolResult is not null && ambiguityToolResult.Contains("出现多处", StringComparison.Ordinal),
            "edit_file must reject ambiguous multi-occurrence matches unless replace_all is true.");
        Check(searchToolResult is not null && searchToolResult.Contains("crlf.cs", StringComparison.Ordinal),
            "search_files tool must locate matching lines across workspace files.");

        // Multimodal vision check on OpenAI + Anthropic
        var sampleImg = new[] { new ChatImageAttachment("image/png", "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==", "Shot1") };
        await runner.RunAsync(
            new GroupChatRun(new GroupChatAgent("a1", "OAI", configWs.Id), configWs, workDir, "VISION_CHECK", ChatSandboxMode.ReadOnly, Attachments: sampleImg),
            new Progress<GroupChatUpdate>(),
            CancellationToken.None);
        Check(capturedOaiVisionBody is not null && capturedOaiVisionBody.Contains("\"image_url\"", StringComparison.Ordinal),
            "OpenAI protocol must serialize ChatImageAttachment as multimodal image_url block.");

        await runner.RunAsync(
            new GroupChatRun(new GroupChatAgent("a2", "Anth", anthWs.Id), anthWs, workDir, "VISION_CHECK", ChatSandboxMode.ReadOnly, Attachments: sampleImg),
            new Progress<GroupChatUpdate>(),
            CancellationToken.None);
        Check(capturedAnthVisionBody is not null && capturedAnthVisionBody.Contains("\"type\":\"image\"", StringComparison.Ordinal),
            "Anthropic protocol must serialize ChatImageAttachment as base64 image block.");
    }

    private static void RunGitSync(string gitExe, string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo(gitExe) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"Git fixture setup failed: {p.StandardError.ReadToEnd()}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class DelegateRunner(Func<GroupChatRun, IProgress<GroupChatUpdate>, CancellationToken, Task<string>> handler) : IGroupChatRunner
    {
        public Task<string> RunAsync(GroupChatRun request, IProgress<GroupChatUpdate> progress, CancellationToken token) =>
            handler(request, progress, token);
    }

    private sealed class DelegateHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }

    private sealed class SynchronousProgress(Action<GroupChatUpdate> onReport) : IProgress<GroupChatUpdate>
    {
        public void Report(GroupChatUpdate value) => onReport(value);
    }
}
