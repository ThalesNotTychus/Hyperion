using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CodexMultiLauncher;

internal static class GroupChatTests
{
    public static async Task<bool> TryRunChildAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "--group-chat-child") return false;
        Console.InputEncoding = new UTF8Encoding(false, true); Console.OutputEncoding = new UTF8Encoding(false, true);
        var prompt = await Console.In.ReadToEndAsync();
        var identity = new { Id = Environment.ProcessId, Started = Process.GetCurrentProcess().StartTime.ToUniversalTime() };
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "child-" + args[1] + ".json"), JsonSerializer.Serialize(identity));
        if (!prompt.Contains("中文 🧪", StringComparison.Ordinal)) throw new InvalidOperationException("Prompt encoding lost.");
        switch (args[1])
        {
            case "success":
                await Console.Error.WriteAsync(new string('x', 180_000) + " private-diagnostic-value");
                await Console.Out.WriteLineAsync("{\"type\":\"thread.started\",\"thread_id\":\"fixture\"}");
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { type = "item.completed", item = new { id = "answer", type = "agent_message", text = "你好 🧪" } }));
                await Console.Out.WriteLineAsync("{\"type\":\"turn.completed\",\"usage\":{}}");
                break;
            case "stubborn":
                await Console.Out.WriteLineAsync("{\"type\":\"turn.started\"}"); await Console.Out.FlushAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan); break;
            case "bad-json": await Console.Out.WriteLineAsync("not-json"); break;
            case "bad-utf8": await Console.OpenStandardOutput().WriteAsync(new byte[] { 0xc3, 0x28, 0x0a }); break;
            case "failed": await Console.Out.WriteLineAsync("{\"type\":\"turn.failed\",\"error\":{\"message\":\"private-diagnostic-value\"}}"); break;
            default: throw new InvalidOperationException("Unknown group chat child scenario.");
        }
        return true;
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.GroupChatTests", Guid.NewGuid().ToString("N"), "中文 空格 🧪");
        Directory.CreateDirectory(root);
        try
        {
            var profiles = new ProfileService(root).LoadOrCreateDefaults();
            var catalog = new WorkspaceCatalog(root); catalog.Reload(profiles);
            var workspaces = catalog.Workspaces;
            var a = new GroupChatAgent(Guid.NewGuid().ToString("N"), "GPT1", profiles[0].Id);
            var b = new GroupChatAgent(Guid.NewGuid().ToString("N"), "GPT2", profiles[1].Id);
            Check(GroupChatRouting.Resolve("@gpt1 @GPT1 @GPT2 hello", [a, b]).SequenceEqual(new[] { a, b }), "Mentions must be case insensitive, deduplicated and ordered.");
            Check(GroupChatRouting.Resolve("mail@example.com @GPT1 hello", [a, b]).Single() == a, "Emails cannot become recipients.");
            Throws(() => GroupChatRouting.Resolve("@missing hello", [a, b]));
            Throws(() => GroupChatRouting.Resolve("@GPT1", [a, b]));
            Throws(() => GroupChatRouting.Resolve("hello", [a, b]));
            Throws(() => GroupChatRouting.ValidateAgents([a, b with { Alias = "gpt1" }]));
            var store = new GroupChatStore(root);
            var runner = new ControlledRunner();
            var coordinator = new GroupChatCoordinator(store, () => workspaces, runner);
            var room = coordinator.CreateRoom(root);
            coordinator.Configure(room, "测试群聊", root, [a, b]);
            var batch = coordinator.SendAsync(room, "@GPT1 @GPT2 比较方案 中文 🧪");
            Check(runner.Tickets.Count == 2 && runner.MaximumActive == 2, "Both addressed accounts must run concurrently.");
            Check(runner.Tickets[0].Request.Workspace.Id == a.WorkspaceId && runner.Tickets[1].Request.Workspace.Id == b.WorkspaceId, "Each identity must retain its own account.");
            Check(runner.Tickets.All(ticket => LastPromptMessage(ticket.Request.Prompt) == "@GPT1 @GPT2 比较方案 中文 🧪"
                && !ticket.Request.Prompt.Contains("等待回复", StringComparison.Ordinal)), "A batch must use shared user history without placeholder messages.");
            Throws(() => coordinator.Configure(room, "busy", root, [a, b]));
            await ThrowsAsync(() => coordinator.SendAsync(room, "@GPT1 another"));
            runner.Tickets[0].Completion.SetResult("第一份独立回复");
            runner.Tickets[1].Completion.SetResult("第二份独立回复");
            await batch;
            Check(room.Messages.Count == 3 && room.Messages[1].Sender == "GPT1" && room.Messages[2].Sender == "GPT2"
                && room.Messages[1].WorkspaceId == a.WorkspaceId && room.Messages[2].WorkspaceId == b.WorkspaceId
                && room.Messages.Skip(1).All(message => message.State == ChatMessageState.Completed), "Replies must stay in the shared record with distinct sender identities.");
            var followup = coordinator.SendAsync(room, "@GPT2 看看 GPT1 的回复 中文 🧪");
            Check(runner.Tickets.Count == 3 && runner.Tickets[2].Request.Agent.Alias == "GPT2"
                && runner.Tickets[2].Request.Prompt.Contains("第一份独立回复", StringComparison.Ordinal)
                && runner.Tickets[2].Request.Prompt.Contains("第二份独立回复", StringComparison.Ordinal), "Only the addressed identity must receive the prior shared answers.");
            runner.Tickets[2].Completion.SetResult("已参考其他身份的回复"); await followup;
            var reloaded = new GroupChatStore(root).Load().Single();
            Check(reloaded.Title == room.Title && reloaded.Agents.SequenceEqual(room.Agents) && reloaded.Messages.Count == 5, "Bindings and transcript must survive reopening.");
            coordinator.SetRoomPrimaryWorkspace(room, b.WorkspaceId);
            var directTurn = coordinator.SendAsync(room, "帮我初始化项目结构");
            Check(runner.Tickets.Last().Request.Workspace.Id == b.WorkspaceId
                && room.Messages[^2].Text == "帮我初始化项目结构", "Unmentioned project prompt must route directly to PrimaryWorkspaceId without prepending @alias.");
            runner.Tickets.Last().Completion.SetResult("已完成项目初始化");
            await directTurn;
            Check(new GroupChatStore(root).Load().Single().PrimaryWorkspaceId == b.WorkspaceId, "PrimaryWorkspaceId must persist across reloads.");
            var settingsStore = new UiSettingsStore(root);
            var savedSettings = settingsStore.Save(new UiSettings(RecentProjects: [root + "\\", root, Path.Combine(root, "subproj")]));
            Check(savedSettings.RecentProjects is { Count: 2 } && settingsStore.Load().RecentProjects is { Count: 2 }, "RecentProjects must normalize and deduplicate paths.");
            var secondRoom = coordinator.CreateRoom(root);
            var stopped = coordinator.SendAsync(room, "@GPT1 stop 中文 🧪");
            var unaffected = coordinator.SendAsync(secondRoom, "@GPT1 continue 中文 🧪");
            coordinator.Stop(room); await stopped;
            Check(room.Messages.Last().State == ChatMessageState.Cancelled && secondRoom.IsRunning, "Stopping one room must not cancel another.");
            runner.Tickets.Last().Completion.SetResult("另一个群聊继续"); await unaffected;
            var pending = coordinator.SendAsync(secondRoom, "@GPT2 shutdown 中文 🧪");
            await coordinator.ShutdownAsync(); await pending;
            Check(secondRoom.Messages.Last().State == ChatMessageState.Cancelled && !secondRoom.IsRunning, "Shutdown must cancel and await owned replies.");
            var interrupted = store.Load(); interrupted[0].Messages.Last().State = ChatMessageState.Running; store.Save(interrupted);
            Check(store.Load()[0].Messages.Last().State == ChatMessageState.Interrupted, "A previous in-flight message must not be silently resumed or billed again.");
            var invalid = Encoding.UTF8.GetBytes("broken json"); File.WriteAllBytes(store.FilePath, invalid);
            Throws(() => store.Load());
            Check(File.ReadAllBytes(store.FilePath).SequenceEqual(invalid), "Invalid history must not be overwritten.");
            var request = new GroupChatRun(a, catalog.FindById(a.WorkspaceId)!, root, "中文 🧪");
            var start = CodexGroupChatRunner.CreateStartInfo(Path.Combine(root, "codex.exe"), request);
            Check(!start.UseShellExecute && start.CreateNoWindow && start.ArgumentList.Contains("read-only") && !start.ArgumentList.Any(argument => argument.StartsWith("--dangerously", StringComparison.Ordinal)), "Chat processes must preserve sandbox restrictions.");
            Check(start.Environment["CODEX_HOME"] == request.Workspace.CodexHome && start.Environment["CODEX_SQLITE_HOME"] == request.Workspace.CodexHome
                && !start.Environment.ContainsKey("OPENAI_API_KEY") && !start.Environment.ContainsKey("CODEX_API_KEY"), "Account routing must override homes and exclude inherited API credentials.");
            Check(!start.ArgumentList.Contains(request.Prompt) && start.ArgumentList.Last() == "-", "User messages must travel via stdin rather than the command line.");
            await RuntimeProcessesAsync(request);
            await VerifyInstalledCliFlagsAsync(request);
            EventsNeverExposeReasoningOrToolOutput();
            Console.WriteLine("Group chat mentions, shared context, parallel identity isolation, room cancellation, persistence and real child-process lifecycle passed. No model request performed.");
        }
        finally
        {
            var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.GroupChatTests"));
            var target = Path.GetFullPath(root);
            Check(target.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Group chat fixture cleanup outside boundary rejected.");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    private static async Task RuntimeProcessesAsync(GroupChatRun request)
    {
        foreach (var scenario in new[] { "success", "stubborn", "bad-json", "bad-utf8", "failed" })
        {
            var runner = new CodexGroupChatRunner(run =>
            {
                var executable = Environment.ProcessPath!;
                var start = CodexGroupChatRunner.CreateStartInfo(executable, run); start.ArgumentList.Clear();
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add("--group-chat-child"); start.ArgumentList.Add(scenario); return start;
            });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var progress = new InlineProgress(update => { if (scenario == "stubborn") deadline.Cancel(); Check(!update.Text.Contains("private-diagnostic-value", StringComparison.Ordinal), "Diagnostics cannot become chat messages."); });
            if (scenario == "success") Check(await runner.RunAsync(request, progress, deadline.Token) == "你好 🧪", "Unicode replies must survive protocol decoding.");
            else if (scenario == "stubborn")
            {
                try { await runner.RunAsync(request, progress, deadline.Token); throw new Exception("Cancellation was ignored."); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            }
            else await ThrowsAsync(async () => { await runner.RunAsync(request, progress, deadline.Token); });
            using var identity = JsonDocument.Parse(File.ReadAllText(Path.Combine(request.WorkingDirectory, "child-" + scenario + ".json")));
            try
            {
                using var process = Process.GetProcessById(identity.RootElement.GetProperty("Id").GetInt32());
                Check(process.HasExited || process.StartTime.ToUniversalTime() != identity.RootElement.GetProperty("Started").GetDateTime(), "A completed or cancelled fixture must not retain its owned process.");
            }
            catch (ArgumentException) { }
        }
    }

    private static void EventsNeverExposeReasoningOrToolOutput()
    {
        var events = new CodexGroupChatEvents();
        events.Accept("{\"type\":\"item.completed\",\"item\":{\"id\":\"reason\",\"type\":\"reasoning\",\"text\":\"private reasoning\"}}");
        events.Accept("{\"type\":\"item.completed\",\"item\":{\"id\":\"tool\",\"type\":\"command_execution\",\"aggregated_output\":\"private command output\"}}");
        events.Accept("{\"type\":\"item.updated\",\"item\":{\"id\":\"answer\",\"type\":\"agent_message\",\"text\":\"partial\"}}");
        events.Accept("{\"type\":\"item.completed\",\"item\":{\"id\":\"answer\",\"type\":\"agent_message\",\"text\":\"complete\"}}");
        events.Accept("{\"type\":\"turn.completed\"}");
        Check(events.Completed && events.Text == "complete", "Only assistant messages belong in a transcript; item updates must not duplicate content.");
        Throws(() => GroupChatRouting.BuildPrompt(new(Guid.NewGuid().ToString("N"), "GPT1", "account"), [], new string('x', GroupChatRouting.MaximumContextCharacters)));
    }
    private static async Task VerifyInstalledCliFlagsAsync(GroupChatRun request)
    {
        if (Environment.GetEnvironmentVariable("CI") == "true" && CodexExecutableLocator.TryFindCurrentCandidate() is null)
        {
            Console.WriteLine("SKIP VerifyInstalledCliFlagsAsync on CI runner without local Codex binary.");
            return;
        }
        var start = CodexGroupChatRunner.CreateStartInfo(CodexExecutableLocator.FindCurrentCandidateOrThrow(), request);
        start.ArgumentList.Add("--help");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start real Codex CLI help probe.");
        try
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(deadline.Token);
            Check(process.ExitCode == 0 && (await output).Contains("Usage: codex exec", StringComparison.Ordinal), "The installed CLI must accept all chat launch flags without a model request.");
            await error;
        }
        finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(2000); } }
    }
    private sealed class ControlledRunner : IGroupChatRunner
    {
        public List<Ticket> Tickets { get; } = [];
        public int MaximumActive { get; private set; }
        private int _active;
        public async Task<string> RunAsync(GroupChatRun request, IProgress<GroupChatUpdate> progress, CancellationToken token)
        {
            var ticket = new Ticket(request); Tickets.Add(ticket); _active++; MaximumActive = Math.Max(MaximumActive, _active);
            try { return await ticket.Completion.Task.WaitAsync(token); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
    private sealed record Ticket(GroupChatRun Request)
    { public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private sealed class InlineProgress(Action<GroupChatUpdate> callback) : IProgress<GroupChatUpdate>
    { public void Report(GroupChatUpdate value) => callback(value); }
    private static string? LastPromptMessage(string prompt)
    {
        using var document = JsonDocument.Parse(prompt[prompt.IndexOf('[')..]);
        return document.RootElement.EnumerateArray().Last().GetProperty("text").GetString();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
    { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection."); }
    private static async Task ThrowsAsync(Func<Task> action)
    { try { await action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected asynchronous rejection."); }
}
