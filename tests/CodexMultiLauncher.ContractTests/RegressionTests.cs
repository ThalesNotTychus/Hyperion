using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexMultiLauncher;

internal static class RegressionTests
{
    public static async Task RunAsync()
    {
        RefreshTicksCannotStarveWork();
        WorkspaceSwitchRejectsStaleCompletion();
        MetricsNotifyAndInvalidate();
        ParentPidReuseDoesNotAbsorbOlderRoots();
        SandboxPolicyRejectsForbiddenAndMalformedRequirements();
        SandboxLayerMustMatchExactHome();
        ResumeRejectsAnotherProcessThread();
        AppServerTransportUsesStrictUtf8();
        await StdioLifecycleTests.RunAsync();
        await OptionalUsageProtocolIsExplicitAsync();
        await RealAppServerMigrationAsync();
        Console.WriteLine("Regression checks and real app-server integration passed.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected contract rejection.");
    }
    private static void RefreshTicksCannotStarveWork()
    {
        var now = DateTimeOffset.UtcNow;
        using var gate = new WorkspaceRefreshGate(TimeSpan.FromSeconds(20));
        var first = gate.TryBegin("a", now)!;
        for (var i = 1; i <= 30; i++) Check(gate.TryBegin("a", now.AddSeconds(i * 2)) is null, "A timer must not restart an in-flight scan, even after freshness expires.");
        Check(!first.Token.IsCancellationRequested && gate.IsCurrent(first), "Long scan was starved.");
        gate.Complete(first, now.AddMinutes(1));
        Check(gate.TryBegin("a", now.AddSeconds(61)) is null, "Fresh results must not rescan.");
        var next = gate.TryBegin("a", now.AddSeconds(80))!;
        Check(next is not null, "A completed stale scan must be refreshable.");
        gate.Complete(next!, now.AddSeconds(81));
    }
    private static void WorkspaceSwitchRejectsStaleCompletion()
    {
        var now = DateTimeOffset.UtcNow;
        using var gate = new WorkspaceRefreshGate(TimeSpan.FromSeconds(20));
        var old = gate.TryBegin("a", now)!;
        var current = gate.TryBegin("b", now)!;
        Check(old.Token.IsCancellationRequested && !gate.IsCurrent(old), "Old account results must be rejected.");
        gate.Complete(old, now);
        Check(gate.IsCurrent(current), "Stale completion must not clear the new request.");
        gate.Cancel(); Check(current.Token.IsCancellationRequested, "Leaving for an external workspace must cancel current work.");
        gate.Complete(current, now);
        var returned = gate.TryBegin("a", now)!;
        Check(returned is not null, "Cancellation must not mark an account fresh.");
        gate.Complete(returned!, now);
    }
    private static void MetricsNotifyAndInvalidate()
    {
        var now = DateTimeOffset.UtcNow;
        var identity = new ProcessIdentity(123, now);
        using var instance = new TrackedCodexInstance(identity, null, CodexInstanceSource.External);
        var notifications = new HashSet<string?>();
        instance.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        instance.ApplyMeasurement([identity], @"C:\official\ChatGPT.exe", new(TimeSpan.Zero, 1000, true, true, 0, now));
        instance.ApplyMeasurement([identity], @"C:\official\ChatGPT.exe", new(TimeSpan.FromSeconds(.1), 2000, true, true, Stopwatch.Frequency, now.AddSeconds(1)));
        notifications.Clear();
        instance.ApplyMeasurement([identity], @"C:\official\ChatGPT.exe", new(TimeSpan.FromSeconds(.3), 3000, true, true, 2 * Stopwatch.Frequency, now.AddSeconds(2)));
        Check(notifications.Contains("CpuText") && notifications.Contains("WorkingSetText"), "Formatted metrics must notify while availability stays true.");
        instance.MarkUnavailable();
        Check(!instance.IsCpuAvailable && !instance.IsWorkingSetAvailable && !instance.HasMainWindow, "Discovery failure must invalidate stale metrics and windows.");
        instance.ApplyMeasurement([identity], @"C:\official\ChatGPT.exe", new(TimeSpan.FromSeconds(4), 4000, true, true, 3 * Stopwatch.Frequency, now.AddSeconds(3)));
        Check(!instance.IsCpuAvailable, "The first sample after a gap must not reuse stale CPU baseline.");
    }
    private static void ParentPidReuseDoesNotAbsorbOlderRoots()
    {
        var now = DateTimeOffset.UtcNow;
        var oldChild = new ProcessIdentity(2, now);
        var reusedParent = new ProcessIdentity(1, now.AddMinutes(1));
        var roots = OfficialProcessTree.GroupRoots([(oldChild, 1u, true), (reusedParent, 0u, true)]);
        Check(roots.Count == 2 && roots[reusedParent].Count == 1, "A reused newer parent PID must not absorb an older process.");
    }
    private static void SandboxPolicyRejectsForbiddenAndMalformedRequirements()
    {
        foreach (var input in new[] { "{\"requirements\":null}", "{\"requirements\":{\"allowedWindowsSandboxImplementations\":[\"unelevated\",\"elevated\"]}}" })
        { using var json = JsonDocument.Parse(input); WorkspaceSandboxService.RequireCompatibleModeAllowed(json.RootElement); }
        foreach (var input in new[] { "{}", "{\"requirements\":true}", "{\"requirements\":{\"allowedWindowsSandboxImplementations\":[\"elevated\"]}}", "{\"requirements\":{\"allowedWindowsSandboxImplementations\":[]}}" })
        { using var json = JsonDocument.Parse(input); Reject(() => WorkspaceSandboxService.RequireCompatibleModeAllowed(json.RootElement)); }
        using var elevated = JsonDocument.Parse("{\"windows\":{\"sandbox\":\"elevated\"}}");
        Check(!WorkspaceSandboxService.IsCompatible(elevated.RootElement), "Elevated mode must not be accepted for an independent managed home.");
    }
    private static void SandboxLayerMustMatchExactHome()
    {
        var path = @"C:\profiles\one\config.toml";
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { layers = new[] { new { name = new { type = "user", file = path }, config = new { windows = new { sandbox = "elevated" } }, version = "v1" } } }));
        var layer = WorkspaceSandboxService.GetUserLayer(json.RootElement, path);
        Check(layer.GetProperty("version").GetString() == "v1", "Exact user layer must match.");
        Reject(() => WorkspaceSandboxService.GetUserLayer(json.RootElement, @"C:\profiles\two\config.toml"));
        using var write = JsonDocument.Parse(JsonSerializer.Serialize(WorkspaceSandboxService.CreateWriteParameters(path, "v1")));
        Check(write.RootElement.GetProperty("expectedVersion").GetString() == "v1" && write.RootElement.GetProperty("keyPath").GetString() == "windows.sandbox", "Migration must version-check one sandbox key, without changing tool approval or full-access permissions.");
    }
    private static void ResumeRejectsAnotherProcessThread()
    {
        using var process = Process.GetCurrentProcess();
        var thread = process.Threads[0].Id;
        Check(!PackagedAppResume.Resume([PackagedAppResume.ResumeArgument, "-p", "1", "-tid", thread.ToString()]), "Resume must reject a thread owned by another PID.");
    }
    private static async Task OptionalUsageProtocolIsExplicitAsync()
    {
        Task<JsonDocument> Request(string method, int error) => method switch
        {
            "account/read" => Task.FromResult(JsonDocument.Parse("{\"result\":{\"account\":{\"type\":\"chatgpt\",\"email\":\"fixture@example.test\"}}}")),
            "account/rateLimits/read" => Task.FromResult(JsonDocument.Parse("{\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":25}}}}")),
            "account/usage/read" => Task.FromException<JsonDocument>(new CodexAppServerProtocolException("fixture", error)),
            _ => throw new InvalidOperationException("Unexpected endpoint.")
        };
        var partial = await CodexAppServerUsageClient.ReadAccountUsageAsync((m, _, _) => Request(m, -32601), CancellationToken.None);
        Check(partial.PrimaryUsedPercent == 25 && partial.LifetimeTokens is null && partial.Email == "fixture@example.test", "Missing optional extension must preserve real account/limits and leave lifetime unknown.");
        try
        {
            await CodexAppServerUsageClient.ReadAccountUsageAsync((m, _, _) => Request(m, -32000), CancellationToken.None);
            throw new InvalidOperationException("Non-compatibility error was hidden.");
        }
        catch (CodexAppServerProtocolException e) when (e.Code == -32000) { }
    }

    private static void AppServerTransportUsesStrictUtf8()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var start = CodexAppServerSession.CreateStartInfo("codex.exe", Path.GetTempPath());
        Check(start.ArgumentList.SequenceEqual(new[] { "app-server", "--listen", "stdio://", "--disable", "plugins" }), "Account/config sessions must disable plugin startup through a process-local CLI override.");
        const string json = """{"projects":{"D:\\中\\":{"trust_level":"trusted"}},"text":"中文 😀 café \"quoted\""}""";
        // Include literal Unicode on the wire, as the Rust app-server serializer does.
        var wire = Encoding.UTF8.GetBytes(json);
        foreach (var encoding in new[] { start.StandardInputEncoding!, start.StandardOutputEncoding! })
        {
            Check(encoding.CodePage == 65001 && encoding.GetPreamble().Length == 0, "JSONL must use UTF-8 without a BOM.");
            Check(encoding.GetString(wire) == json && encoding.GetBytes(json).SequenceEqual(wire), "Unicode and JSON escapes must round-trip unchanged.");
            try { encoding.GetString(new byte[] { 0xc3, 0x28 }); throw new InvalidOperationException("Malformed UTF-8 was silently replaced."); }
            catch (DecoderFallbackException) { }
        }
        Check(start.StandardErrorEncoding!.CodePage == 65001, "Diagnostics must not inherit the system code page.");
        using var valid = JsonDocument.Parse(start.StandardOutputEncoding!.GetString(wire));
        Check(valid.RootElement.GetProperty("projects").GetProperty(@"D:\中\").GetProperty("trust_level").GetString() == "trusted", "Windows Unicode project keys must survive decoding.");
        try
        {
            using var damaged = JsonDocument.Parse(Encoding.GetEncoding(936).GetString(wire));
            throw new InvalidOperationException("Regression fixture must reproduce JSON corruption under CP936.");
        }
        catch (JsonException) { }
    }

    private static async Task RealAppServerMigrationAsync()
    {
        if (Environment.GetEnvironmentVariable("CI") == "true" && CodexExecutableLocator.TryFindCurrentCandidate() is null)
        {
            Console.WriteLine("SKIP RealAppServerMigrationAsync on CI runner without local Codex binary.");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.Regression", "中文 😀 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Exception? testFailure = null;
        try
        {
            var service = new ProfileService(root);
            var profiles = service.LoadOrCreateDefaults();
            var catalog = new WorkspaceCatalog(root); catalog.Reload(profiles);
            var first = catalog.Workspaces[0]; var second = catalog.Workspaces[1];
            var firstPath = Path.Combine(first.CodexHome!, "config.toml");
            const string old = "# preserve this comment 中文\ncli_auth_credentials_store = \"file\"\nmodel_reasoning_effort = \"high\"\n[features]\nplugins = true\n[projects.'D:\\中\\']\ntrust_level = \"trusted\"\n[windows]\nsandbox = \"elevated\"\n";
            File.WriteAllText(firstPath, old);
            var secondBytes = File.ReadAllBytes(Path.Combine(second.CodexHome!, "config.toml"));
            var result = await WorkspaceSandboxService.PrepareAsync(first);
            Check(result.Changed && File.ReadAllText(result.BackupPath!) == old, "Migration must keep an exact old-config backup.");
            var after = File.ReadAllText(firstPath);
            Check(after.Contains("# preserve this comment") && after.Contains("model_reasoning_effort = \"high\"") && after.Contains("unelevated"), "Official TOML writer must preserve unrelated configuration.");
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var session = await CodexAppServerSession.StartAsync(first.DataPaths, deadline.Token);
                await using (session)
                {
                    using var read = await session.RequestAsync("config/read", new { includeLayers = true }, deadline.Token);
                    var user = WorkspaceSandboxService.GetUserLayer(read.RootElement.GetProperty("result"), firstPath);
                    Check(user.GetProperty("config").GetProperty("projects").GetProperty(@"D:\中\").GetProperty("trust_level").GetString() == "trusted", "Real app-server must preserve the exact Unicode project path through config read/write.");
                    Check(user.GetProperty("config").GetProperty("features").GetProperty("plugins").GetBoolean(), "Query override must never persistently disable the user's plugins.");
                    Check(!read.RootElement.GetProperty("result").GetProperty("config").GetProperty("features").GetProperty("plugins").GetBoolean(), "The official app-server must confirm plugins disabled for this query process.");
                }
                Check(!session.WasForceTerminated && session.ExitCode == 0, "The real app-server must exit normally after stdin EOF.");
            }
            Check(File.ReadAllBytes(Path.Combine(second.CodexHome!, "config.toml")).SequenceEqual(secondBytes), "Migration must not write another account's config.");
            Check(!(await WorkspaceSandboxService.PrepareAsync(first)).Changed, "Migration must be idempotent.");
            Check(!(await WorkspaceSandboxService.PrepareAsync(second)).Changed, "New profiles must already be compatible.");
            foreach (var workspace in catalog.Workspaces.Where(workspace => workspace.Client == ClientKind.Codex && workspace.Kind == CodexWorkspaceKind.Managed))
                Check(!File.Exists(Path.Combine(workspace.CodexHome!, ".sandbox", "setup_marker.json")), "Configuration preparation must not initialize shared elevated sandbox users.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            using var usage = new CodexAppServerUsageClient();
            try { await usage.ReadAsync(first, cancellation.Token); throw new InvalidOperationException("Cancelled usage unexpectedly succeeded."); }
            catch (OperationCanceledException) { }
            Check((await usage.ReadAsync(first)).State == CodexUsageState.Unauthenticated, "Cancelled query must not poison the next request; fixture has no login.");
            File.WriteAllText(firstPath, "[windows\n");
            try { await WorkspaceSandboxService.PrepareAsync(first); throw new InvalidOperationException("Malformed config unexpectedly accepted."); }
            catch (InvalidOperationException ex) when (!ex.Message.Contains("unexpectedly accepted", StringComparison.Ordinal)) { }
            Check(File.ReadAllText(firstPath) == "[windows\n", "Rejected configuration must not be rewritten.");
        }
        catch (Exception exception) { testFailure = exception; throw; }
        finally
        {
            var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.Regression"));
            var target = Path.GetFullPath(root);
            if (target.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(target, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
