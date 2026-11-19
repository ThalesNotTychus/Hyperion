using System.Diagnostics;
using System.Reflection;
using System.Text;
using CodexMultiLauncher;

internal static class StdioLifecycleTests
{
    private const string ChildArgument = "--stdio-lifecycle-child";
    private const int DrainStressBytes = 1024 * 1024; // Exceeds redirected-pipe buffers.

    internal static async Task<bool> TryRunChildAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != ChildArgument) return false;
        Console.InputEncoding = new UTF8Encoding(false, true);
        Console.OutputEncoding = new UTF8Encoding(false, true);
        await Console.Out.WriteLineAsync("ready");
        await Console.Out.FlushAsync();
        switch (args[1])
        {
            case "cooperative":
                await Console.In.ReadToEndAsync();
                var bytes = new byte[DrainStressBytes];
                Array.Fill(bytes, (byte)'x');
                await Task.WhenAll(Console.OpenStandardOutput().WriteAsync(bytes).AsTask(), Console.OpenStandardError().WriteAsync(bytes).AsTask());
                break;
            case "stubborn":
                await Console.In.ReadToEndAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan);
                break;
            case "invalid-utf8":
                await Console.In.ReadLineAsync();
                using (var output = Console.OpenStandardOutput())
                {
                    await output.WriteAsync(new byte[] { 0xc3, 0x28, 0x0a });
                    await output.FlushAsync();
                    await Console.In.ReadToEndAsync();
                }
                break;
            case "exited":
                break;
            default:
                throw new InvalidOperationException("Unknown child scenario.");
        }
        return true;
    }

    internal static async Task RunAsync()
    {
        foreach (var scenario in new[] { "cooperative", "stubborn", "invalid-utf8", "exited" })
        {
            var executable = Environment.ProcessPath!;
            var start = CodexAppServerSession.CreateStartInfo(executable, Path.GetTempPath());
            start.ArgumentList.Clear();
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add(ChildArgument); start.ArgumentList.Add(scenario);
            using var child = new Process { StartInfo = start };
            if (!child.Start()) throw new InvalidOperationException("Could not start lifecycle fixture.");
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (await child.StandardOutput.ReadLineAsync(deadline.Token) != "ready") throw new InvalidOperationException("Child fixture did not become ready.");
                var session = new CodexAppServerSession(child);
                if (scenario == "exited") await child.WaitForExitAsync(deadline.Token);
                try
                {
                    if (scenario == "invalid-utf8")
                    {
                        try { using var ignored = await session.RequestAsync("fixture/read", null, deadline.Token); throw new Exception("Invalid wire bytes were accepted."); }
                        catch (CodexAppServerProtocolException e) when (e.Message.Contains("UTF-8", StringComparison.Ordinal)) { }
                    }
                }
                finally { await session.DisposeAsync(); }
                if (session.WasForceTerminated != (scenario == "stubborn")) throw new InvalidOperationException($"Incorrect shutdown path for {scenario}.");
                if (scenario != "stubborn" && session.ExitCode != 0) throw new InvalidOperationException($"Healthy child did not exit normally: {scenario}.");
            }
            catch
            {
                // Only this fixture's Process is owned; the session may have disposed it.
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await child.WaitForExitAsync(cleanup.Token);
                    }
                }
                catch (InvalidOperationException) { }
                throw;
            }
        }
        Console.WriteLine("Stdio lifecycle: EOF drain, forced timeout, invalid UTF-8 and already-exited process passed.");
    }
}
