using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CodexMultiLauncher;

internal static class WindowLayoutCheck
{
    public static void Run()
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            DispatcherFrame? frame = null;
            try
            {
                var application = Application.Current ?? new App();
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                if (Application.Current == application)
                {
                    try { ((App)application).InitializeComponent(); } catch { }
                }

                frame = new DispatcherFrame();
                _ = ExecuteLayoutAndScreenshotChecksAsync(application).ContinueWith(t =>
                {
                    if (t.IsFaulted) failure = t.Exception?.InnerException ?? t.Exception;
                    frame.Continue = false;
                    completed.Set();
                    try { application.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); } catch { }
                }, TaskScheduler.Default);

                Dispatcher.PushFrame(frame);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!completed.Wait(TimeSpan.FromSeconds(45)))
            throw new TimeoutException("WebView2 ADE layout render did not finish within 45 seconds.");
        if (failure is not null)
            throw new InvalidOperationException("ADE WebView2 layout acceptance failed.", failure);
        Console.WriteLine("Codex, Claude, Antigravity, Direct API, Market CLI and multi-theme ADE WebView2 layouts rendered.");
    }

    private static async Task ExecuteLayoutAndScreenshotChecksAsync(Application application)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.Layout", Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        try
        {
            await application.Dispatcher.InvokeAsync(async () =>
            {
                var profiles = new ProfileService(root);
                var defaults = profiles.LoadOrCreateDefaults();
                profiles.CreateProfile("claude3", ClientKind.Claude);
                profiles.CreateProfile("反重力主力号", ClientKind.Antigravity);
                profiles.CreateProfile("反重力二号", ClientKind.Antigravity);

                var universal = new AgentProviderStore(root);
                var gemRec = universal.Upsert(new UniversalAgentUpsertRequest(
                    Id: null,
                    DisplayName: "GeminiPro直连",
                    AgentKind: UniversalAgentKind.DirectApi,
                    PresetId: "gemini",
                    Protocol: ApiProtocolKind.GeminiNative,
                    BaseUrl: "https://generativelanguage.googleapis.com",
                    Model: "gemini-2.5-pro",
                    ApiKeyPlaintext: "demo-gemini-fixture-key-5678",
                    TokenBudgetLimit: 2_000_000));
                var dsRec = universal.Upsert(new UniversalAgentUpsertRequest(
                    Id: null,
                    DisplayName: "DeepSeek架构师",
                    AgentKind: UniversalAgentKind.DirectApi,
                    PresetId: "deepseek",
                    Protocol: ApiProtocolKind.OpenAiCompatible,
                    BaseUrl: "https://api.deepseek.com/v1",
                    Model: "deepseek-chat",
                    ApiKeyPlaintext: "demo-deepseek-fixture-9988"));
                universal.Upsert(new UniversalAgentUpsertRequest(
                    Id: null,
                    DisplayName: "OpenCode终端",
                    AgentKind: UniversalAgentKind.MarketCli,
                    PresetId: "opencode",
                    Protocol: ApiProtocolKind.OpenAiCompatible,
                    BaseUrl: null,
                    Model: "claude-sonnet-4",
                    ApiKeyPlaintext: null));

                window = new MainWindow(root, enableAutoStealth: false)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false,
                    Left = -2600,
                    Top = 80,
                    Width = 1380,
                    Height = 900,
                };

                var now = DateTimeOffset.UtcNow;
                window.AccountPool.RecordSnapshot(defaults[0].Id, new CodexUsageSnapshot(
                    CodexUsageState.Available, "pro", "lead@team.dev", "Pro", 24m, now.AddHours(4), 38m, now.AddDays(4), 120m, true, false, null, 84520m, now));
                window.AccountPool.RecordSnapshot(defaults[1].Id, new CodexUsageSnapshot(
                    CodexUsageState.Available, "plus", "backup@team.dev", "Plus", 84m, now.AddHours(1), 62m, now.AddDays(2), null, false, false, null, 41200m, now));
                window.AccountPool.RecordTokenUsage(AgentProviderStore.ToWorkspaceId(gemRec), 185_000, 42_000);
                window.AccountPool.RecordTurnStarted(AgentProviderStore.ToWorkspaceId(gemRec));
                window.AccountPool.RecordTurnFinished(AgentProviderStore.ToWorkspaceId(gemRec), true);
                window.AccountPool.RecordTokenUsage(AgentProviderStore.ToWorkspaceId(dsRec), 64_000, 19_200);
                window.AccountPool.RecordTurnStarted(AgentProviderStore.ToWorkspaceId(dsRec));
                window.AccountPool.RecordTurnFinished(AgentProviderStore.ToWorkspaceId(dsRec), true);

                var studioRoom = window.Coordinator.CreateRoom(
                    root,
                    "GeminiPro直连 · 独立架构重构",
                    ChatSandboxMode.WorkspaceWrite,
                    root,
                    "ade/studio-feature",
                    AgentProviderStore.ToWorkspaceId(gemRec));
                studioRoom.Messages.Add(new GroupChatMessage
                {
                    Role = ChatMessageRole.User,
                    Sender = "你",
                    Text = "请重构配额池调度器并实时汇报执行步骤。",
                });
                studioRoom.Messages.Add(new GroupChatMessage
                {
                    Role = ChatMessageRole.Agent,
                    Sender = "GeminiPro直连",
                    WorkspaceId = AgentProviderStore.ToWorkspaceId(gemRec),
                    WorkspaceName = "GeminiPro直连",
                    State = ChatMessageState.Running,
                    Steps =
                    [
                        new ChatExecutionStep("st1", "reasoning", "读取 src/AccountPoolService.cs 分析并发状态机", "completed"),
                        new ChatExecutionStep("st2", "file_change", "编辑 AccountPoolService.cs 注入实时工作状态指标", "completed"),
                        new ChatExecutionStep("st3", "command_execution", "运行验证：dotnet build -c Release --no-restore", "running"),
                    ],
                    Text = "已完成配额状态机实时指标注入，正在执行增量构建验证…",
                });
                window.AccountPool.RecordTurnStarted(AgentProviderStore.ToWorkspaceId(gemRec), "运行验证：dotnet build -c Release --no-restore");

                var demoProjA = Path.Combine(root, "ai-multi-launcher");
                var demoProjB = Path.Combine(root, "saas-api-gateway");
                Directory.CreateDirectory(demoProjA);
                Directory.CreateDirectory(demoProjB);
                window.Coordinator.CreateRoom(demoProjB, "主对话", ChatSandboxMode.WorkspaceWrite, demoProjB, null, null);

                var room = window.Coordinator.Rooms[0];
                room.Title = "重构算力池自动接力与 Worktree 隔离";
                room.SandboxMode = ChatSandboxMode.WorkspaceWrite;
                room.WorkingDirectory = demoProjA;
                room.WorktreeBranch = "ade/quota-failover-f8a12c";
                room.RepositoryRoot = demoProjA;
                room.PrimaryWorkspaceId = AgentProviderStore.ToWorkspaceId(gemRec);
                room.Messages.Add(new GroupChatMessage
                {
                    Role = ChatMessageRole.User,
                    Sender = "你",
                    Text = "@GPT1 @GPT2 请并行实现多账号限流自动故障转移，并检查 Git Worktree 合并边界。",
                });
                room.Messages.Add(new GroupChatMessage
                {
                    Role = ChatMessageRole.Agent,
                    Sender = "GPT1",
                    WorkspaceName = "账号 1",
                    ModelSummary = "GPT-6.2-Sol · XHigh · ⚡Fast",
                    ThinkingText = "Inspecting AccountPoolService quota health thresholds and cooldown backoff windows before editing...",
                    Steps =
                    [
                        new ChatExecutionStep("s1", "read", "Analyzed AccountPoolService.cs", "completed", "AccountPoolService.cs", "#L38-96", "src/AccountPoolService.cs"),
                        new ChatExecutionStep("s2", "read", "Analyzed GroupChatCoordinator.cs", "completed", "GroupChatCoordinator.cs", "#L462-538", "src/GroupChatCoordinator.cs"),
                        new ChatExecutionStep("s3", "edit", "Edited AccountPoolService.cs", "completed", "AccountPoolService.cs", "+14 -3", "src/AccountPoolService.cs"),
                    ],
                    Text = "已完成 `AccountPoolService` 配额评分与冷却降级逻辑：\n```csharp\npublic CodexWorkspace? SelectFailoverWorkspace(IReadOnlyList<CodexWorkspace> pool)\n{\n    return pool.FirstOrDefault(w => w.CanReadUsage);\n}\n```",
                });
                room.Messages.Add(new GroupChatMessage
                {
                    Role = ChatMessageRole.Agent,
                    Sender = "GPT2",
                    WorkspaceName = "账号 2",
                    ModelSummary = "Gemini 4 Neon · High",
                    FailoverNotice = "触发限流保护：账号 3 (429) → 已自动接力至 账号 2，上下文与 Worktree 保持不变",
                    Steps = [new ChatExecutionStep("s4", "command", "dotnet test --no-restore", "completed", "dotnet test --no-restore")],
                    Text = "我已验证 `git merge --no-ff` 在主工作树存在未提交改动时的安全中止保护，全部契约测试通过。",
                });

                window.Show();
                await window.EnsureWebViewReadyAsync();
                window.BroadcastFullState();
            }).Task.Unwrap();

            // Wait for JS bootstrap to finish
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var ready = await application.Dispatcher.InvokeAsync(() => window!.ExecuteShellScriptAsync("window.__adeTestApi && window.__adeTestApi.isReady()")).Task.Unwrap();
                if (ready == "true") break;
                await Task.Delay(80);
            }

            var outputDir = Path.Combine(RootLocator.FindProjectRoot(), "artifacts", "acceptance");
            Directory.CreateDirectory(outputDir);

            var sampleDiffJson = JsonSerializer.Serialize(new GitDiffSnapshot(
                true,
                root,
                "ade/quota-failover-f8a12c",
                2,
                14,
                3,
                GitWorktreeService.ParseUnifiedDiff("""
                    diff --git a/src/AccountPoolService.cs b/src/AccountPoolService.cs
                    index 1000001..1000002 100644
                    --- a/src/AccountPoolService.cs
                    +++ b/src/AccountPoolService.cs
                    @@ -42,4 +42,7 @@ public sealed class AccountPoolService
                         public bool IsHealthy(decimal usedPercent)
                         {
                    -        return usedPercent < 100m;
                    +        if (usedPercent >= 92m) return false;
                    +        return true;
                         }
                    """)), WebUiBridge.JsonOptions);

            await application.Dispatcher.InvokeAsync(async () =>
            {
                await window!.ExecuteShellScriptAsync($"window.__adeTestApi.setTheme('obsidian'); window.__adeTestApi.switchView('command'); window.__adeTestApi.openMentionPreview(); window.__adeTestApi.openDiffWithSample({sampleDiffJson});");
                await Task.Delay(180);
                var renderedCards = await window.ExecuteShellScriptAsync("document.querySelectorAll('.msg-card').length >= 3 && document.querySelectorAll('.ag-tl-row').length >= 4 && !!document.getElementById('composer-model-select') && document.querySelectorAll('.msg-model-badge').length >= 2");
                if (renderedCards != "true")
                    throw new InvalidOperationException("Chat stream, model picker, or native activity timeline failed to render expected elements.");

                // Regression check: opening Model or Primary Account dropdown MUST stay open across repeated BroadcastFullState() refreshes!
                await window.ExecuteShellScriptAsync("document.getElementById('composer-model-select').click();");
                window.BroadcastFullState();
                window.BroadcastFullState();
                await Task.Delay(80);
                var modelPopoverAlive = await window.ExecuteShellScriptAsync("!!document.getElementById('composer-picker-popover')");
                if (modelPopoverAlive != "true")
                    throw new InvalidOperationException("Composer model dropdown unexpectedly closed during background state refresh!");

                await window.ExecuteShellScriptAsync("document.getElementById('composer-primary-select').click();");
                window.BroadcastFullState();
                await Task.Delay(60);
                var primaryPopoverAlive = await window.ExecuteShellScriptAsync("!!document.getElementById('composer-picker-popover')");
                if (primaryPopoverAlive != "true")
                    throw new InvalidOperationException("Composer primary account dropdown unexpectedly closed during background state refresh!");
                await window.ExecuteShellScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' })); window.__adeTestApi.openMentionPreview();");
                await Task.Delay(50);

                await window.CaptureShellPreviewPngAsync(Path.Combine(outputDir, "ade-command-obsidian.png"));

                var room = window.Coordinator.Rooms[0];
                var origUserMsg = room.Messages[0].Text;
                var origReply1 = room.Messages[1].Text;
                var origReply2 = room.Messages[2].Text;
                room.Messages[0].Text = "@GPT1 @GPT2 Implement automatic multi-account 429 failover and verify Git Worktree merge safety.";
                room.Messages[1].Text = "Implemented quota health scoring and cooldown routing in `AccountPoolService`:\n```csharp\npublic CodexWorkspace? SelectFailoverWorkspace(IReadOnlyList<CodexWorkspace> pool)\n{\n    return pool.FirstOrDefault(w => w.CanReadUsage);\n}\n```";
                room.Messages[2].Text = "Verified `git merge --no-ff` dirty-tree guard and isolation boundaries—all contract tests passed.";
                window.BroadcastFullState();
                await window.ExecuteShellScriptAsync($"window.__adeTestApi.setLanguage('en'); window.__adeTestApi.openMentionPreview(); window.__adeTestApi.openDiffWithSample({sampleDiffJson});");
                await Task.Delay(120);
                await AssertZeroEnglishCjkLeaksAsync(window, "Project Workspace + Mention + Diff");
                await window.CaptureShellPreviewPngAsync(Path.Combine(outputDir, "ade-command-en-obsidian.png"));
                room.Messages[0].Text = origUserMsg;
                room.Messages[1].Text = origReply1;
                room.Messages[2].Text = origReply2;
                window.BroadcastFullState();
                await window.ExecuteShellScriptAsync($"window.__adeTestApi.setLanguage('zh'); window.__adeTestApi.openMentionPreview(); window.__adeTestApi.openDiffWithSample({sampleDiffJson});");
                await Task.Delay(80);

                await window.ExecuteShellScriptAsync("window.__adeTestApi.setTheme('matrix');");
                await Task.Delay(120);
                await window.CaptureShellPreviewPngAsync(Path.Combine(outputDir, "ade-command-matrix.png"));

                await window.ExecuteShellScriptAsync("window.__adeTestApi.setTheme('daylight');");
                await Task.Delay(120);
                await window.CaptureShellPreviewPngAsync(Path.Combine(outputDir, "ade-command-daylight.png"));

                await window.ExecuteShellScriptAsync("window.__adeTestApi.previewCleanEmptyCommand();");
                await Task.Delay(120);
                await window.CaptureShellPreviewPngAsync(Path.Combine(outputDir, "ade-command-empty-daylight.png"));

                await window.ExecuteShellScriptAsync($"window.__adeTestApi.openStudioPreview({sampleDiffJson});");
                await Task.Delay(150);
                await window.CaptureShellPreviewPngAsync(Path.Combine(outputDir, "ade-studio-daylight.png"));

                await window.ExecuteShellScriptAsync("window.__adeTestApi.setTheme('aurora'); window.__adeTestApi.switchView('arena'); window.__adeTestApi.prepareArenaPreview();");
                await Task.Delay(120);
                await window.CaptureShellPreviewPngAsync(Path.Combine(outputDir, "ade-arena-aurora.png"));

                await window.ExecuteShellScriptAsync("window.__adeTestApi.setTheme('obsidian'); window.__adeTestApi.switchView('fleet');");
                await Task.Delay(150);
                await window.CaptureShellPreviewPngAsync(Path.Combine(outputDir, "ade-fleet-obsidian.png"));

                await window.ExecuteShellScriptAsync("window.__adeTestApi.openOnboardingModal('api');");
                await Task.Delay(150);
                await window.CaptureShellPreviewPngAsync(Path.Combine(outputDir, "ade-onboarding-modal.png"));
                await window.ExecuteShellScriptAsync("window.__adeTestApi.closeModal();");

                // Capture English UI showcase screenshots & assert ZERO untranslated CJK UI leaks across all views/modals!
                static async Task AssertZeroEnglishCjkLeaksAsync(MainWindow win, string contextName)
                {
                    var raw = await win.ExecuteShellScriptAsync("window.__adeTestApi.findUntranslatedCjk()");
                    var leaks = JsonSerializer.Deserialize<string[]>(raw) ?? [];
                    if (leaks.Length > 0)
                        throw new InvalidOperationException($"Untranslated Chinese UI strings found in English mode ({contextName}): " + string.Join(" | ", leaks));
                }

                await window.ExecuteShellScriptAsync("window.__adeTestApi.setLanguage('en'); window.__adeTestApi.setTheme('obsidian'); window.__adeTestApi.switchView('fleet');");
                await Task.Delay(150);
                await AssertZeroEnglishCjkLeaksAsync(window, "Accounts / Fleet View");
                await window.CaptureShellPreviewPngAsync(Path.Combine(outputDir, "ade-fleet-en-obsidian.png"));

                await window.ExecuteShellScriptAsync("window.__adeTestApi.openOnboardingModal('api');");
                await Task.Delay(120);
                await AssertZeroEnglishCjkLeaksAsync(window, "Onboarding Modal - Direct API");
                await window.CaptureShellPreviewPngAsync(Path.Combine(outputDir, "ade-onboarding-en-obsidian.png"));

                await window.ExecuteShellScriptAsync("window.__adeTestApi.openOnboardingModal('cli');");
                await Task.Delay(100);
                await AssertZeroEnglishCjkLeaksAsync(window, "Onboarding Modal - CLI");

                await window.ExecuteShellScriptAsync("window.__adeTestApi.openOnboardingModal('desktop');");
                await Task.Delay(100);
                await AssertZeroEnglishCjkLeaksAsync(window, "Onboarding Modal - Desktop");
                await window.ExecuteShellScriptAsync("window.__adeTestApi.closeModal();");

                await window.ExecuteShellScriptAsync("window.__adeTestApi.switchView('arena'); window.__adeTestApi.prepareArenaPreview();");
                await Task.Delay(120);
                await AssertZeroEnglishCjkLeaksAsync(window, "Arena View");
                await window.CaptureShellPreviewPngAsync(Path.Combine(outputDir, "ade-arena-en-obsidian.png"));

                await window.ExecuteShellScriptAsync("window.__adeTestApi.switchView('command'); window.__adeTestApi.previewCleanEmptyCommand();");
                await Task.Delay(120);
                await AssertZeroEnglishCjkLeaksAsync(window, "Project Workspace Empty Hero");
                await window.CaptureShellPreviewPngAsync(Path.Combine(outputDir, "ade-command-empty-en-obsidian.png"));

                await window.ExecuteShellScriptAsync("window.__adeTestApi.openCreateProjectModal();");
                await Task.Delay(100);
                await AssertZeroEnglishCjkLeaksAsync(window, "New Project Modal");
                await window.ExecuteShellScriptAsync("window.__adeTestApi.closeModal();");

                await window.ExecuteShellScriptAsync("window.__adeTestApi.setLanguage('zh');");
                window.Hide();
            }).Task.Unwrap();
        }
        finally
        {
            var target = Path.GetFullPath(root);
            var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.Layout"));
            if (target.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    try { Directory.Delete(target, recursive: true); } catch { }
                });
            }
        }
    }
}
