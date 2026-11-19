using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexMultiLauncher;

/// <summary>One bounded, sequential JSONL conversation with a process owned by this launcher.</summary>
internal sealed class CodexAppServerSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stderr;
    private int _nextId;
    internal bool WasForceTerminated { get; private set; }
    internal int? ExitCode { get; private set; }

    // Takes ownership of a newly started process, including both redirected output streams.
    internal CodexAppServerSession(Process process)
    {
        _process = process;
        _stderr = DrainAsync(process.StandardError);
    }

    internal static async Task<CodexAppServerSession> StartAsync(WorkspaceDataPaths paths, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var home = Path.GetFullPath(paths.RequireCodexHome());
        var start = CreateStartInfo(CodexExecutableLocator.FindCurrentCandidateOrThrow(), home);
        var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new InvalidOperationException("无法启动 Codex app-server。"); }
        catch { process.Dispose(); throw; }
        var session = new CodexAppServerSession(process);
        try
        {
            using var initialized = await session.RequestAsync("initialize", new { clientInfo = new { name = "CodexMultiLauncher", version = "2" } }, token).ConfigureAwait(false);
            await session.SendAsync(CodexAppServerUsageClient.CreateInitializedNotificationJson(), token).ConfigureAwait(false);
            return session;
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string home)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            // JSONL is UTF-8, regardless of the Windows ANSI/OEM code page. A legacy
            // decoder can consume a JSON backslash as part of a multibyte character.
            StandardInputEncoding = new UTF8Encoding(false, true),
            StandardOutputEncoding = new UTF8Encoding(false, true),
            // Diagnostics are not protocol data; retain readable output on bad bytes.
            StandardErrorEncoding = Encoding.UTF8,
            // Account/config operations must not inherit the launcher's project configuration.
            WorkingDirectory = home
        };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--listen"); start.ArgumentList.Add("stdio://");
        // A short-lived account/config client must not run the plugin marketplace's
        // startup Git synchronization. This official CLI override is process-local;
        // it does not modify config.toml or the separately activated desktop client.
        start.ArgumentList.Add("--disable"); start.ArgumentList.Add("plugins");
        start.Environment["CODEX_HOME"] = home; start.Environment["CODEX_SQLITE_HOME"] = home;
        return start;
    }

    internal async Task<JsonDocument> RequestAsync(string method, object? parameters, CancellationToken token)
    {
        var id = ++_nextId;
        await SendAsync(CodexAppServerUsageClient.CreateRequestJson(id, method, parameters), token).ConfigureAwait(false);
        while (true)
        {
            string? line;
            try { line = await _process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false); }
            catch (DecoderFallbackException) { throw new CodexAppServerProtocolException($"Codex 的 {method} 响应不是有效的 UTF-8。"); }
            if (line is null) throw new CodexAppServerProtocolException($"Codex 在返回 {method} 前关闭。");
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException exception) { throw new CodexAppServerProtocolException($"Codex 返回了无效的 JSON：{exception.Message}"); }
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new CodexAppServerProtocolException("Codex 返回了无效的响应对象。"); }
            if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var number) || number != id)
            { document.Dispose(); continue; }
            if (root.TryGetProperty("error", out var error))
            {
                var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : (int?)null;
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "未知协议错误";
                document.Dispose();
                throw new CodexAppServerProtocolException($"{method}：{message}", code);
            }
            if (!root.TryGetProperty("result", out _)) { document.Dispose(); throw new CodexAppServerProtocolException($"{method} 响应缺少 result。"); }
            return document;
        }
    }

    private async Task SendAsync(string json, CancellationToken token)
    {
        await _process.StandardInput.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            // EOF is the stdio app-server's normal shutdown signal. Killing a healthy
            // server after every query can interrupt background child initialization.
            // Every request is flushed before reading its response; no buffered input
            // remains to send. A closed peer may already have broken this pipe.
            try { _process.StandardInput.Close(); }
            catch (IOException) { }
            // Drain remaining notifications without decoding them after the last request.
            // This also lets shutdown finish after a rejected UTF-8 response.
            var stdout = _process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            using (var graceful = new CancellationTokenSource(CodexAppServerUsageClient.ProcessExitTimeout))
            {
                try { await _process.WaitForExitAsync(graceful.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (graceful.IsCancellationRequested)
                {
                    if (!_process.HasExited)
                    {
                        WasForceTerminated = true;
                        _process.Kill(entireProcessTree: true);
                    }
                }
            }
            using var terminated = new CancellationTokenSource(CodexAppServerUsageClient.ProcessExitTimeout);
            await _process.WaitForExitAsync(terminated.Token).ConfigureAwait(false);
            ExitCode = _process.ExitCode;
            await Task.WhenAll(stdout, _stderr).WaitAsync(terminated.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or OperationCanceledException)
        { throw new InvalidOperationException("启动器创建的 Codex 查询进程未能确认退出。", e); }
        finally { _process.Dispose(); }
    }
}
