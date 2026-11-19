using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using CodexMultiLauncher;

internal static class RealUserSimulationTests
{
    public static void RunIsolatedProcess()
    {
        var selfDll = typeof(RealUserSimulationTests).Assembly.Location;
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(selfDll);
        psi.ArgumentList.Add("--user-sim");
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch isolated --user-sim process.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(95_000))
        {
            proc.Kill(entireProcessTree: true);
            throw new TimeoutException("Isolated --user-sim process timed out.");
        }
        if (!string.IsNullOrWhiteSpace(stdout))
            Console.WriteLine(stdout.Trim());
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"RealUserSimulationTests failed (exit {proc.ExitCode}):\n{stderr}\n{stdout}");
    }

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
                _ = ExecuteFullUserJourneyAsync(application).ContinueWith(t =>
                {
                    if (t.IsFaulted) failure = t.Exception?.InnerException ?? t.Exception;
                    frame.Continue = false;
                    completed.Set();
                }, TaskScheduler.Default);

                Dispatcher.PushFrame(frame);
            }
            catch (Exception ex)
            {
                failure = ex;
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

        if (!completed.Wait(TimeSpan.FromSeconds(90)))
            throw new TimeoutException("Real-user E2E simulation timed out after 90 seconds.");
        if (failure is not null)
            throw new InvalidOperationException($"Real-user E2E simulation failed: {failure.Message}", failure);

        Console.WriteLine("Real-user end-to-end UI simulation (cold boot, onboarding, project creation, model/effort/speed popovers in ZH/EN, live tool-calling agent chat, @mention + mid-turn stop, Git worktree + inline diff review + merge, ConPTY terminal, Arena race + merge, Fleet lifecycle, and clean shutdown) passed.");
    }

    private static async Task ExecuteFullUserJourneyAsync(Application app)
    {
        var root = Path.Combine(Path.GetTempPath(), "Hyperion.UserSim", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var mockServer = new LocalOpenAiGatewayMock();
        mockServer.Start();

        MainWindow? window = null;
        try
        {
            // 1. Cold boot on brand-new empty directory
            await app.Dispatcher.InvokeAsync(async () =>
            {
                window = new MainWindow(root, enableAutoStealth: false)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false,
                    Left = -2600,
                    Top = 80,
                    Width = 1400,
                    Height = 900,
                };
                window.Show();
                await window.EnsureWebViewReadyAsync();
                window.BroadcastFullState();
            }).Task.Unwrap();

            async Task<string> JsAsync(string script) =>
                await app.Dispatcher.InvokeAsync(() => window!.ExecuteShellScriptAsync(script)).Task.Unwrap();

            async Task WaitUntilAsync(string label, Func<Task<bool>> predicate, int timeoutMs = 15000)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (await predicate()) return;
                    await Task.Delay(80);
                }
                var jsErrs = await JsAsync("JSON.stringify(window.__simErrors || [])");
                var toastErrs = await JsAsync("JSON.stringify(window.__simErrorToasts || [])");
                var domDiag = await JsAsync("JSON.stringify({ cards: document.querySelectorAll('.msg-card').length, lastCard: Array.from(document.querySelectorAll('.msg-card')).pop()?.textContent?.slice(0, 180), sendDisabled: document.getElementById('btn-send-msg')?.disabled, stopDisp: document.getElementById('btn-stop-room')?.style.display })");
                throw new InvalidOperationException($"Timed out waiting for [{label}]. JS errors: {jsErrs} | Error toasts: {toastErrs} | DOM: {domDiag}");
            }

            await WaitUntilAsync("WebView2 app bootstrap", async () =>
                await JsAsync("window.__adeTestApi && window.__adeTestApi.isReady()") == "true");

            // Install runtime JS error & unexpected error-toast trap
            await JsAsync("""
                (() => {
                  window.__simErrors = [];
                  window.__simErrorToasts = [];
                  window.__allowNextErrorToast = false;
                  window.addEventListener('error', e => window.__simErrors.push(String(e.message || e.error || e)));
                  window.addEventListener('unhandledrejection', e => window.__simErrors.push('UnhandledRejection: ' + String(e.reason?.message || e.reason || e)));
                  const toastRoot = document.getElementById('toast-root');
                  if (toastRoot) {
                    new MutationObserver(muts => {
                      for (const m of muts) {
                        for (const n of m.addedNodes) {
                          if (n.nodeType === 1 && n.classList.contains('error') && !window.__allowNextErrorToast) {
                            window.__simErrorToasts.push(n.textContent.trim());
                          }
                        }
                      }
                    }).observe(toastRoot, { childList: true });
                  }
                })();
                """);

            async Task AssertNoFrontendErrorsAsync(string stepName)
            {
                var rawErrs = await JsAsync("window.__simErrors");
                var errs = JsonSerializer.Deserialize<string[]>(rawErrs) ?? [];
                if (errs.Length > 0)
                    throw new InvalidOperationException($"Uncaught JS error during [{stepName}]: {string.Join(" | ", errs)}");

                var rawToasts = await JsAsync("window.__simErrorToasts");
                var toasts = JsonSerializer.Deserialize<string[]>(rawToasts) ?? [];
                if (toasts.Length > 0)
                    throw new InvalidOperationException($"Unexpected error toast shown to user during [{stepName}]: {string.Join(" | ", toasts)}");
            }

            // 2. Journey 1: User clicks Primary Account dropdown -> "+ 接入新账号" -> probes live models -> tests connection -> saves 2 Direct API agents!
            var baseUrlJson = JsonSerializer.Serialize(mockServer.BaseUrl);
            await JsAsync("document.getElementById('composer-primary-select').click();");
            await WaitUntilAsync("Primary account popover open", async () =>
                await JsAsync("!!document.getElementById('cpp-add-account')") == "true");
            await JsAsync("document.getElementById('cpp-add-account').click();");
            await WaitUntilAsync("Onboarding modal open", async () =>
                await JsAsync("!!document.getElementById('ua-save-btn')") == "true");

            // Select OpenAI preset card, fill URL + API key, click "探测可用模型"
            await JsAsync($$"""
                (() => {
                  const openaiCard = document.querySelector('[data-pick-api="openai"]');
                  if (openaiCard) openaiCard.click();
                  document.getElementById('ua-name').value = 'OpenAI主力号';
                  document.getElementById('ua-url').value = {{baseUrlJson}};
                  document.getElementById('ua-key').value = 'sk-sim-secret-key-alpha-12345';
                  document.getElementById('ua-fetch-models-btn').click();
                })();
                """);

            await WaitUntilAsync("Live model probe chips rendered", async () =>
                await JsAsync("document.querySelectorAll('#ua-model-chips-row [data-pick-model]').length >= 3") == "true");

            // Pick gpt-5.4 chip, click Test Connection, then click Save
            await JsAsync("""
                document.querySelector('[data-pick-model="gpt-5.4"]').click();
                document.getElementById('ua-test-btn').click();
                """);
            await Task.Delay(350);
            await JsAsync("document.getElementById('ua-save-btn').click();");
            await WaitUntilAsync("First Direct API agent saved & modal closed", async () =>
                await JsAsync("!document.getElementById('modal-backdrop')") == "true");

            // Add a 2nd Direct API agent ("架构评审员") for @mention & Arena racing
            await JsAsync("window.__adeTestApi.openOnboardingModal('api');");
            await WaitUntilAsync("Second onboarding modal open", async () =>
                await JsAsync("!!document.getElementById('ua-save-btn')") == "true");
            await JsAsync($$"""
                (() => {
                  const openaiCard = document.querySelector('[data-pick-api="openai"]');
                  if (openaiCard) openaiCard.click();
                  document.getElementById('ua-name').value = '架构评审员';
                  document.getElementById('ua-url').value = {{baseUrlJson}};
                  document.getElementById('ua-model').value = 'o3-pro';
                  document.getElementById('ua-key').value = 'sk-sim-secret-key-beta-67890';
                  document.getElementById('ua-save-btn').click();
                })();
                """);
            await WaitUntilAsync("Second Direct API agent saved", async () =>
                await JsAsync("!document.getElementById('modal-backdrop')") == "true");

            // Verify DPAPI secret storage on disk (no plaintext key leakage)
            var providersJson = await File.ReadAllTextAsync(Path.Combine(root, "data", "agent-providers.json"));
            if (providersJson.Contains("sk-sim-secret-key", StringComparison.Ordinal))
                throw new InvalidOperationException("Plaintext API key leaked into data/agent-providers.json!");
            await AssertNoFrontendErrorsAsync("Journey 1: Onboarding Direct API Agents");

            // 3. Journey 2: User clicks "+ 新建项目" -> creates Git project "my-sim-project"
            var projectsParent = Path.Combine(root, "user-projects");
            Directory.CreateDirectory(projectsParent);
            var projectsParentJson = JsonSerializer.Serialize(projectsParent);

            await JsAsync("document.getElementById('btn-new-project').click();");
            await WaitUntilAsync("Create project modal open", async () =>
                await JsAsync("!!document.getElementById('m-confirm-proj')") == "true");
            await JsAsync($$"""
                (() => {
                  const nameEl = document.getElementById('m-proj-name');
                  const parentEl = document.getElementById('m-proj-parent');
                  nameEl.value = 'my-sim-project';
                  nameEl.dispatchEvent(new Event('input'));
                  parentEl.value = {{projectsParentJson}};
                  parentEl.dispatchEvent(new Event('input'));
                  document.getElementById('m-confirm-proj').click();
                })();
                """);
            await WaitUntilAsync("Project created & modal closed", async () =>
                await JsAsync("!document.getElementById('modal-backdrop')") == "true");

            var simProjectDir = Path.Combine(projectsParent, "my-sim-project");
            if (!Directory.Exists(Path.Combine(simProjectDir, ".git")))
                throw new InvalidOperationException("New project did not initialize .git directory!");
            await AssertNoFrontendErrorsAsync("Journey 2: Create Project");

            // 4. Journey 3: Select Primary Account, Model, Effort, Speed via Composer Popovers + check EN localization on ALL dynamic popovers/menus!
            await JsAsync("document.getElementById('composer-primary-select').click();");
            await WaitUntilAsync("Primary picker items", async () =>
                await JsAsync("document.querySelectorAll('[data-cpp-ws]').length >= 2") == "true");
            await JsAsync("""
                (() => {
                  const rows = Array.from(document.querySelectorAll('[data-cpp-ws]'));
                  const target = rows.find(r => r.textContent.includes('OpenAI主力号')) || rows[0];
                  target.click();
                })();
                """);
            await Task.Delay(250);

            await JsAsync("document.getElementById('composer-model-select').click();");
            await WaitUntilAsync("Model picker items loaded from mock /v1/models", async () =>
                await JsAsync("!!document.querySelector('[data-cpp-model=\"gpt-5.4\"]')") == "true");
            await JsAsync("document.querySelector('[data-cpp-model=\"gpt-5.4\"]').click();");
            await Task.Delay(150);

            await WaitUntilAsync("Effort & Fast-mode controls visible for gpt-5.4", async () =>
                await JsAsync("!!document.getElementById('composer-effort-select') && (!!document.getElementById('btn-toggle-fast-tier') || !!document.getElementById('composer-speed-select'))") == "true");

            await JsAsync("document.getElementById('composer-effort-select').click();");
            await WaitUntilAsync("Effort options open", async () =>
                await JsAsync("!!document.querySelector('[data-cpp-effort=\"xhigh\"]')") == "true");
            await JsAsync("document.querySelector('[data-cpp-effort=\"xhigh\"]').click();");
            await Task.Delay(120);

            await JsAsync("(document.getElementById('btn-toggle-fast-tier') || document.getElementById('composer-speed-select')).click();");
            await Task.Delay(120);

            // Switch to English (EN) and inspect dynamic popovers/modals that a real EN user opens!
            var cjkIssues = new List<string>();
            async Task CheckEnSurfaceAsync(string surfaceName, string openJs, string closeJs)
            {
                await JsAsync(openJs);
                await Task.Delay(140);
                var rawLeaks = await JsAsync("window.__adeTestApi.findUntranslatedCjk()");
                var leaks = (JsonSerializer.Deserialize<string[]>(rawLeaks) ?? [])
                    .Where(s => !s.Contains("OpenAI主力号", StringComparison.Ordinal)
                             && !s.Contains("架构评审员", StringComparison.Ordinal)
                             && !s.StartsWith("VAL[ms-title]:", StringComparison.Ordinal))
                    .ToArray();
                if (leaks.Length > 0)
                    cjkIssues.Add($"{surfaceName}: [{string.Join(", ", leaks)}]");
                if (!string.IsNullOrEmpty(closeJs))
                {
                    await JsAsync(closeJs);
                    await Task.Delay(60);
                }
            }

            await JsAsync("document.getElementById('btn-toggle-lang').click();");
            await Task.Delay(120);

            await CheckEnSurfaceAsync("EN Primary Picker Popover",
                "document.getElementById('composer-primary-select').click();",
                "document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));");
            await CheckEnSurfaceAsync("EN Model Picker Popover",
                "document.getElementById('composer-model-select').click();",
                "document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));");
            await CheckEnSurfaceAsync("EN Effort Picker Popover",
                "document.getElementById('composer-effort-select').click();",
                "document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));");
            await CheckEnSurfaceAsync("EN Custom Model Modal",
                "document.getElementById('composer-model-select').click(); setTimeout(() => document.getElementById('cpp-custom-model-btn')?.click(), 30);",
                "window.__adeTestApi.closeModal();");
            await CheckEnSurfaceAsync("EN Room Roster Popover",
                "document.getElementById('btn-roster-popover')?.click();",
                "document.body.click();");
            await CheckEnSurfaceAsync("EN Room Settings Modal",
                "document.getElementById('btn-room-settings')?.click();",
                "window.__adeTestApi.closeModal();");
            await CheckEnSurfaceAsync("EN Fleet Card Context Menu",
                "window.__adeTestApi.switchView('fleet'); setTimeout(() => document.querySelector('[data-card-more]')?.click(), 30);",
                "document.body.click(); window.__adeTestApi.switchView('command');");

            // Switch back to Chinese
            await JsAsync("document.getElementById('btn-toggle-lang').click();");
            await Task.Delay(100);
            await AssertNoFrontendErrorsAsync("Journey 3: Composer Pickers & EN Dynamic Surfaces");

            // 5. Journey 4: User types a coding prompt in #composer-input and presses Enter -> Agent streams thinking + executes real write_file tool!
            await JsAsync("""
                (() => {
                  const inp = document.getElementById('composer-input');
                  inp.value = '[WRITE_MAIN_ONCE] 请创建 hello.txt';
                  inp.dispatchEvent(new Event('input', { bubbles: true }));
                  inp.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
                })();
                """);

            var helloPath = Path.Combine(simProjectDir, "hello.txt");
            await WaitUntilAsync("Agent write_file tool created hello.txt & completed reply", async () =>
            {
                if (!File.Exists(helloPath)) return false;
                var done = await JsAsync("document.querySelectorAll('.msg-card').length >= 2 && !document.getElementById('btn-stop-room').offsetParent");
                return done == "true";
            });
            // Clean untracked hello.txt on main branch so later Worktree & Arena merges land on a clean main tree
            File.Delete(helloPath);
            await AssertNoFrontendErrorsAsync("Journey 4: 1-on-1 Agent Tool Execution");

            // 6. Journey 5: @Mention multi-agent collaboration + Mid-turn Stop button (#btn-stop-room)
            await JsAsync("""
                (() => {
                  const inp = document.getElementById('composer-input');
                  inp.value = '@';
                  inp.selectionStart = inp.selectionEnd = 1;
                  inp.dispatchEvent(new Event('input', { bubbles: true }));
                })();
                """);
            await WaitUntilAsync("@mention popup visible", async () =>
                await JsAsync("document.getElementById('mention-popup')?.style.display !== 'none'") == "true");

            await JsAsync("""
                (() => {
                  const items = Array.from(document.querySelectorAll('#mention-popup [data-mention-idx], #mention-popup .mention-item'));
                  const pick1 = items.find(el => el.textContent.includes('OpenAI主力号'));
                  if (pick1) pick1.click();
                  const inp = document.getElementById('composer-input');
                  const a1 = (window.__adeStateDebug?.agents || []).map(x => x.alias);
                  inp.value = '@OpenAI主力号 @架构评审员 请同步检查当前项目';
                  document.getElementById('btn-send-msg').click();
                })();
                """);
            await WaitUntilAsync("Multi-agent @mention turn completed", async () =>
                await JsAsync("document.querySelectorAll('.msg-card').length >= 4 && document.getElementById('btn-stop-room').style.display === 'none'") == "true");

            // Now test mid-stream cancellation via Stop button!
            await JsAsync("""
                (() => {
                  const inp = document.getElementById('composer-input');
                  inp.value = '[SLOW] 请生成超长架构分析报告';
                  document.getElementById('btn-send-msg').click();
                })();
                """);
            await WaitUntilAsync("Stop button appears while streaming", async () =>
                await JsAsync("document.getElementById('btn-stop-room').style.display !== 'none'") == "true");
            await JsAsync("document.getElementById('btn-stop-room').click();");
            await WaitUntilAsync("Room stopped cleanly", async () =>
                await JsAsync("document.getElementById('btn-stop-room').style.display === 'none'") == "true");
            await AssertNoFrontendErrorsAsync("Journey 5: @Mention & Mid-Turn Stop");

            // 7. Journey 6: Spawn isolated Git Worktree on newly created project -> edit file -> open Diff drawer -> add inline comment -> submit review -> merge worktree!
            await JsAsync("document.getElementById('btn-room-spawn-wt').click();");
            await WaitUntilAsync("Worktree spawned & merge button visible", async () =>
                await JsAsync("!!document.getElementById('btn-room-merge')") == "true");

            // Have agent write a new file inside the isolated worktree
            var preWtCards = int.Parse(await JsAsync("document.querySelectorAll('.msg-card').length"), System.Globalization.CultureInfo.InvariantCulture);
            await JsAsync("""
                (() => {
                  const inp = document.getElementById('composer-input');
                  inp.value = '[WRITE_WT_ONCE] 在独立 Worktree 分支写入修改';
                  document.getElementById('btn-send-msg').click();
                })();
                """);
            await WaitUntilAsync("Worktree agent turn finished", async () =>
            {
                var curCards = int.Parse(await JsAsync("document.querySelectorAll('.msg-card').length"), System.Globalization.CultureInfo.InvariantCulture);
                var stopped = await JsAsync("document.getElementById('btn-stop-room').style.display === 'none'");
                var lastDone = await JsAsync("Array.from(document.querySelectorAll('.msg-card')).pop()?.textContent.includes('OK — Task completed') || false");
                return curCards >= preWtCards + 2 && stopped == "true" && lastDone == "true";
            });
            await Task.Delay(200);

            // Open Diff drawer, switch to Split mode, add inline comment, and submit review!
            await JsAsync("document.getElementById('btn-toggle-diff').click();");
            await WaitUntilAsync("Diff drawer rendered changed lines", async () =>
                await JsAsync("document.querySelectorAll('[data-diff-draft]').length > 0") == "true");
            await JsAsync("""
                (() => {
                  document.getElementById('btn-diff-split').click();
                  document.querySelector('[data-diff-draft]').click();
                })();
                """);
            await WaitUntilAsync("Inline diff comment editor open", async () =>
                await JsAsync("!!document.getElementById('inline-diff-draft-input')") == "true");
            await JsAsync("""
                (() => {
                  document.getElementById('inline-diff-draft-input').value = '请补充空值边界校验';
                  document.querySelector('[data-save-diff-draft]').click();
                })();
                """);
            await WaitUntilAsync("Diff review comment queued", async () =>
                await JsAsync("document.querySelectorAll('[data-remove-diff-comment]').length === 1") == "true");
            var preRevCards = int.Parse(await JsAsync("document.querySelectorAll('.msg-card').length"), System.Globalization.CultureInfo.InvariantCulture);
            await JsAsync("document.getElementById('btn-submit-diff-review').click();");
            await WaitUntilAsync("Diff review rework turn completed", async () =>
            {
                var curCards = int.Parse(await JsAsync("document.querySelectorAll('.msg-card').length"), System.Globalization.CultureInfo.InvariantCulture);
                var stopped = await JsAsync("document.getElementById('btn-stop-room').style.display === 'none'");
                var lastDone = await JsAsync("Array.from(document.querySelectorAll('.msg-card')).pop()?.textContent.includes('OK — Task completed') || false");
                return curCards >= preRevCards + 2 && stopped == "true" && lastDone == "true";
            });
            await Task.Delay(250);
            await JsAsync("document.getElementById('btn-diff-close').click();");

            // Click Merge Worktree button -> verify hello.txt is merged back into main repository!
            await JsAsync("document.getElementById('btn-room-merge').click();");
            await WaitUntilAsync("Worktree branch merged hello.txt back into main repo", () => Task.FromResult(File.Exists(helloPath)));
            await AssertNoFrontendErrorsAsync("Journey 6: Worktree Spawn, Diff Review & Merge");

            // 8. Journey 7: Open ConPTY Terminal Drawer -> spawn PowerShell -> run command -> close terminal!
            await JsAsync("document.getElementById('btn-toggle-term').click();");
            await WaitUntilAsync("Terminal dock visible", async () =>
                await JsAsync("document.getElementById('terminal-dock').style.display !== 'none'") == "true");
            await JsAsync("document.querySelector('[data-spawn-term=\"shell\"]').click();");
            await WaitUntilAsync("ConPTY PowerShell tab spawned", async () =>
                await JsAsync("document.querySelectorAll('[data-select-term]').length >= 1") == "true");
            var termIdRaw = await JsAsync("document.querySelector('[data-select-term]')?.dataset.selectTerm || ''");
            var termId = JsonSerializer.Deserialize<string>(termIdRaw) ?? string.Empty;
            if (!string.IsNullOrEmpty(termId))
            {
                await app.Dispatcher.InvokeAsync(() =>
                {
                    window!.Terminals.WriteInput(termId, "Write-Output 'HYPERION_PTY_LIVE_OK'\r\n");
                    window!.Terminals.Resize(termId, 132, 36);
                });
                await Task.Delay(500);
                await JsAsync("document.querySelector('[data-close-term]')?.click();");
                await Task.Delay(200);
            }
            await JsAsync("document.getElementById('btn-term-hide').click();");
            await AssertNoFrontendErrorsAsync("Journey 7: ConPTY Split Terminal");

            // 9. Journey 8: Multi-Agent Arena Race -> parallel worktrees -> verification command -> merge winner!
            var simProjJson = JsonSerializer.Serialize(simProjectDir);
            await JsAsync($$"""
                (() => {
                  window.__adeTestApi.switchView('arena');
                  document.getElementById('arena-repo-input').value = {{simProjJson}};
                  document.getElementById('arena-verify-input').value = 'Write-Output "VERIFY_PASS"';
                  document.getElementById('arena-prompt-input').value = '[ARENA_RACE] 并行实现无锁队列并输出文件';
                  const boxes = Array.from(document.querySelectorAll('[data-arena-ws]'));
                  boxes.forEach((b, i) => { b.checked = i < 2; });
                  document.getElementById('btn-arena-start').click();
                })();
                """);
            await WaitUntilAsync("Arena contestants finished & merge button enabled", async () =>
                await JsAsync("document.querySelectorAll('[data-arena-merge]:not([disabled])').length >= 2") == "true", timeoutMs: 25000);
            await JsAsync("document.querySelector('[data-arena-merge]:not([disabled])').click();");
            await WaitUntilAsync("Arena merge confirm modal", async () =>
                await JsAsync("!!document.getElementById('mc-ok')") == "true");
            await JsAsync("document.getElementById('mc-ok').click();");
            await Task.Delay(600);
            await AssertNoFrontendErrorsAsync("Journey 8: Multi-Agent Arena Race & Winner Merge");

            // 10. Journey 9: Fleet Account Management (Create Managed Profile -> Rename -> Convert to Clientless -> Delete)
            await JsAsync("window.__adeTestApi.switchView('fleet'); document.getElementById('btn-add-profile').click();");
            await WaitUntilAsync("Fleet add profile modal", async () =>
                await JsAsync("!!document.querySelector('[data-wiz-tab=\"desktop\"]') || !!document.getElementById('ua-save-btn')") == "true");
            await JsAsync("window.__adeTestApi.openOnboardingModal('desktop');");
            await WaitUntilAsync("Desktop tab open", async () =>
                await JsAsync("!!document.getElementById('mp-create')") == "true");
            await JsAsync("""
                (() => {
                  document.getElementById('mp-name').value = '模拟桌面号';
                  document.getElementById('mp-create').click();
                })();
                """);
            await WaitUntilAsync("Managed desktop profile created", async () =>
                await JsAsync("!document.getElementById('modal-backdrop')") == "true");
            await AssertNoFrontendErrorsAsync("Journey 9: Fleet Profile Management");

            if (cjkIssues.Count > 0)
            {
                throw new InvalidOperationException("Dynamic UI surfaces had untranslated Chinese in EN mode -> " + string.Join(" || ", cjkIssues));
            }

            // 11. Journey 10: Real Window Shutdown (`window.Close()`) while background tasks are alive!
            var closedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await app.Dispatcher.InvokeAsync(() =>
            {
                window!.Closed += (_, _) => closedTcs.TrySetResult();
                window.Close();
            });
            await closedTcs.Task.WaitAsync(TimeSpan.FromSeconds(6));
            window = null;
        }
        finally
        {
            if (window is not null)
            {
                try { await app.Dispatcher.InvokeAsync(() => window.Hide()); } catch { }
            }
            var target = Path.GetFullPath(root);
            var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Hyperion.UserSim"));
            if (target.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(600);
                    try { Directory.Delete(target, recursive: true); } catch { }
                });
            }
        }
    }

    private sealed class LocalOpenAiGatewayMock : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private Task? _acceptLoop;
        private int _chatRequests;
        private int _arenaCounter;
        private int _slowUsed;
        private int _writeMainUsed;
        private int _writeWtUsed;

        public string BaseUrl { get; private set; } = string.Empty;
        public int TotalChatRequests => Volatile.Read(ref _chatRequests);

        public void Start()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false); }
                catch { break; }
                _ = Task.Run(() => HandleClientAsync(client, token), CancellationToken.None);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var headerBytes = new List<byte>(2048);
                var one = new byte[1];
                while (headerBytes.Count < 65536)
                {
                    var r = await stream.ReadAsync(one.AsMemory(0, 1), token).ConfigureAwait(false);
                    if (r == 0) return;
                    headerBytes.Add(one[0]);
                    var count = headerBytes.Count;
                    if (count >= 4 && headerBytes[count - 4] == '\r' && headerBytes[count - 3] == '\n' && headerBytes[count - 2] == '\r' && headerBytes[count - 1] == '\n')
                        break;
                }

                var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
                var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;
                var reqParts = lines[0].Split(' ');
                var method = reqParts[0];
                var path = reqParts.Length > 1 ? reqParts[1] : "/";

                var contentLength = 0;
                foreach (var h in lines.Skip(1))
                {
                    if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        _ = int.TryParse(h["Content-Length:".Length..].Trim(), out contentLength);
                }

                var bodyBytes = new byte[contentLength];
                var offset = 0;
                while (offset < contentLength)
                {
                    var n = await stream.ReadAsync(bodyBytes.AsMemory(offset, contentLength - offset), token).ConfigureAwait(false);
                    if (n == 0) break;
                    offset += n;
                }
                var bodyText = Encoding.UTF8.GetString(bodyBytes, 0, offset);

                if (method == "GET" && path.Contains("/models", StringComparison.OrdinalIgnoreCase))
                {
                    const string modelsJson = """
                        {
                          "object": "list",
                          "data": [
                            { "id": "gpt-5.4", "object": "model", "owned_by": "openai" },
                            { "id": "gpt-5.4-mini", "object": "model", "owned_by": "openai" },
                            { "id": "o3-pro", "object": "model", "owned_by": "openai" }
                          ]
                        }
                        """;
                    await WriteJsonResponseAsync(stream, 200, modelsJson, token).ConfigureAwait(false);
                    return;
                }

                if (method == "POST" && path.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref _chatRequests);
                    await HandleChatCompletionsSseAsync(stream, bodyText, token).ConfigureAwait(false);
                    return;
                }

                await WriteJsonResponseAsync(stream, 404, "{\"error\":\"not found\"}", token).ConfigureAwait(false);
            }
        }

        private async Task HandleChatCompletionsSseAsync(NetworkStream stream, string bodyText, CancellationToken token)
        {
            var hasToolResult = false;
            var allUserText = new StringBuilder();
            try
            {
                var node = JsonNode.Parse(bodyText)?.AsObject();
                if (node?["messages"] is JsonArray msgs)
                {
                    foreach (var m in msgs)
                    {
                        var role = m?["role"]?.GetValue<string>();
                        if (role == "tool") hasToolResult = true;
                        if (role == "user" && m?["content"] is JsonValue cv && cv.TryGetValue<string>(out var s))
                            allUserText.AppendLine(s);
                    }
                }
            }
            catch { }

            var promptJoined = allUserText.ToString();
            var sseHeader = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(sseHeader, token).ConfigureAwait(false);

            static async Task SendSseObjAsync(NetworkStream s, object payload, CancellationToken ct)
            {
                var line = "data: " + JsonSerializer.Serialize(payload) + "\n\n";
                await s.WriteAsync(Encoding.UTF8.GetBytes(line), ct).ConfigureAwait(false);
                await s.FlushAsync(ct).ConfigureAwait(false);
            }

            if (promptJoined.Contains("[SLOW]", StringComparison.Ordinal) && Interlocked.CompareExchange(ref _slowUsed, 1, 0) == 0)
            {
                for (var i = 0; i < 40 && !token.IsCancellationRequested; i++)
                {
                    try
                    {
                        await SendSseObjAsync(stream, new
                        {
                            choices = new[] { new { delta = new { content = $"Streaming paragraph {i + 1}... " } } },
                        }, token).ConfigureAwait(false);
                        await Task.Delay(200, token).ConfigureAwait(false);
                    }
                    catch { return; }
                }
                await stream.WriteAsync("data: [DONE]\n\n"u8.ToArray(), token).ConfigureAwait(false);
                return;
            }

            var shouldWriteMain = promptJoined.Contains("[WRITE_MAIN_ONCE]", StringComparison.Ordinal) && Interlocked.CompareExchange(ref _writeMainUsed, 1, 0) == 0;
            var shouldWriteWt = promptJoined.Contains("[WRITE_WT_ONCE]", StringComparison.Ordinal) && Interlocked.CompareExchange(ref _writeWtUsed, 1, 0) == 0;
            var shouldWriteArena = promptJoined.Contains("[ARENA_RACE]", StringComparison.Ordinal);

            if (!hasToolResult && (shouldWriteMain || shouldWriteWt || shouldWriteArena))
            {
                var fileName = shouldWriteArena
                    ? $"arena_impl_{Interlocked.Increment(ref _arenaCounter)}.txt"
                    : "hello.txt";
                var argsJson = JsonSerializer.Serialize(new
                {
                    path = fileName,
                    content = $"Generated by Hyperion E2E simulation ({fileName})\nSecond line for diff review\n",
                });

                await SendSseObjAsync(stream, new
                {
                    choices = new[] { new { delta = new { reasoning_content = "Analyzing workspace structure and preparing safe file write..." } } },
                }, token).ConfigureAwait(false);

                await SendSseObjAsync(stream, new
                {
                    choices = new[]
                    {
                        new
                        {
                            delta = new
                            {
                                tool_calls = new[]
                                {
                                    new
                                    {
                                        index = 0,
                                        id = "call_sim_1",
                                        type = "function",
                                        function = new { name = "write_file", arguments = argsJson },
                                    },
                                },
                            },
                            finish_reason = "tool_calls",
                        },
                    },
                    usage = new { prompt_tokens = 120, completion_tokens = 45 },
                }, token).ConfigureAwait(false);

                await stream.WriteAsync("data: [DONE]\n\n"u8.ToArray(), token).ConfigureAwait(false);
                return;
            }

            await SendSseObjAsync(stream, new
            {
                choices = new[] { new { delta = new { reasoning_content = "Verifying completed changes..." } } },
            }, token).ConfigureAwait(false);

            await SendSseObjAsync(stream, new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new { content = "OK — Task completed cleanly.\n```txt\nVerified output\n```" },
                        finish_reason = "stop",
                    },
                },
                usage = new { prompt_tokens = 160, completion_tokens = 38 },
            }, token).ConfigureAwait(false);

            await stream.WriteAsync("data: [DONE]\n\n"u8.ToArray(), token).ConfigureAwait(false);
        }

        private static async Task WriteJsonResponseAsync(NetworkStream stream, int status, string json, CancellationToken token)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var reason = status == 200 ? "OK" : "Not Found";
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, token).ConfigureAwait(false);
            await stream.WriteAsync(body, token).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            if (_acceptLoop is not null)
            {
                try { await _acceptLoop.ConfigureAwait(false); } catch { }
            }
            _cts.Dispose();
        }
    }
}
