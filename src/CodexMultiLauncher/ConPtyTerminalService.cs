using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexMultiLauncher;

public sealed record TerminalSessionInfo(
    string Id,
    string Title,
    string? WorkspaceId,
    string? WorkspaceName,
    string WorkingDirectory,
    string CommandKind,
    int ProcessId,
    bool IsAlive);

/// <summary>
/// Native Windows ConPTY (CreatePseudoConsole) terminal multiplexer hosting interactive
/// PowerShell, Codex CLI, or Claude Code CLI sessions inside isolated account environments.
/// </summary>
public sealed class ConPtyTerminalService : IDisposable
{
    private readonly ConcurrentDictionary<string, ConPtySession> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;

    public event Action<string, string>? OutputReceived;
    public event Action<string, int>? SessionExited;

    public IReadOnlyList<TerminalSessionInfo> ListSessions() =>
        _sessions.Values.Select(s => s.ToInfo()).ToArray();

    public TerminalSessionInfo CreateSession(
        string? title,
        CodexWorkspace? workspace,
        string workingDirectory,
        string commandKind = "shell",
        short cols = 120,
        short rows = 32,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        string? initialInput = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            workingDirectory = Environment.CurrentDirectory;

        var fullDir = Path.GetFullPath(workingDirectory);
        var commandLine = BuildCommandLine(commandKind, workspace);
        var env = BuildEnvironmentDictionary(workspace, extraEnvironment);
        var id = "pty-" + Guid.NewGuid().ToString("N")[..10];
        var label = string.IsNullOrWhiteSpace(title)
            ? $"{(workspace?.DisplayName ?? "本机终端")} · {commandKind.ToUpperInvariant()}"
            : title.Trim();

        var session = ConPtySession.Start(
            id,
            label,
            workspace?.Id,
            workspace?.DisplayName,
            fullDir,
            commandKind,
            commandLine,
            env,
            Math.Clamp(cols, (short)20, (short)400),
            Math.Clamp(rows, (short)6, (short)200),
            onOutput: chunk => OutputReceived?.Invoke(id, chunk),
            onExit: code => SessionExited?.Invoke(id, code));

        _sessions[id] = session;
        if (!string.IsNullOrWhiteSpace(initialInput))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(350);
                session.Write(initialInput + "\r\n");
            });
        }
        return session.ToInfo();
    }

    public void WriteInput(string sessionId, string data)
    {
        if (string.IsNullOrEmpty(data)) return;
        if (_sessions.TryGetValue(sessionId, out var session))
            session.Write(data);
    }

    public void Resize(string sessionId, short cols, short rows)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            session.Resize(Math.Clamp(cols, (short)20, (short)400), Math.Clamp(rows, (short)6, (short)200));
    }

    public void CloseSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
            session.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var pair in _sessions.ToArray())
        {
            if (_sessions.TryRemove(pair.Key, out var session))
                session.Dispose();
        }
    }

    private static string BuildCommandLine(string commandKind, CodexWorkspace? workspace)
    {
        switch (commandKind.ToLowerInvariant())
        {
            case "codex":
                var codexExe = CodexExecutableLocator.FindCurrentCandidateOrThrow();
                return WindowsCommandLine.QuoteArgument(codexExe);
            case "claude":
                var claudeExe = ClaudeCliRunner.FindClaudeCliExecutableOrThrow(workspace);
                if (claudeExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                    return $"cmd.exe /d /k {WindowsCommandLine.QuoteArgument(claudeExe)}";
                return WindowsCommandLine.QuoteArgument(claudeExe);
            case "cmd":
                return "cmd.exe";
            default:
                var shellExe = GitWorktreeService.FindPreferredPowerShellExecutable();
                return $"{WindowsCommandLine.QuoteArgument(shellExe)} -NoLogo";
        }
    }

    private static Dictionary<string, string> BuildEnvironmentDictionary(
        CodexWorkspace? workspace,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            var val = entry.Value?.ToString();
            if (!string.IsNullOrEmpty(key) && val is not null && !key.StartsWith('='))
                env[key] = val;
        }

        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";

        if (workspace is not null)
        {
            if (workspace.Client == ClientKind.Codex && !string.IsNullOrWhiteSpace(workspace.CodexHome))
            {
                var home = Path.GetFullPath(workspace.CodexHome);
                ProfilePathSafety.ThrowIfReparsePath(home);
                env["CODEX_HOME"] = home;
                env["CODEX_SQLITE_HOME"] = home;
                foreach (var k in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL", "OPENAI_ORG_ID", "OPENAI_PROJECT_ID" })
                    env.Remove(k);
            }
            else if (workspace.Client == ClientKind.Claude && !string.IsNullOrWhiteSpace(workspace.ClaudeHome))
            {
                var home = Path.GetFullPath(workspace.ClaudeHome);
                ProfilePathSafety.ThrowIfReparsePath(home);
                env["CLAUDE_CONFIG_DIR"] = home;
                foreach (var k in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL" })
                    env.Remove(k);
            }
            else if (workspace.Client == ClientKind.Antigravity && workspace.Kind == CodexWorkspaceKind.Managed)
            {
                foreach (var (k, v) in AntigravityInstallation.CreateEnvironment(workspace.DataPaths))
                    env[k] = v;
            }
        }

        if (extraEnvironment is not null)
        {
            foreach (var (k, v) in extraEnvironment)
                env[k] = v;
        }

        // Preserve host global npm bin directory on PATH even when APPDATA is isolated per account
        var envLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(envLocal) && Directory.GetParent(envLocal) is { } appDataRoot)
        {
            var hostNpmBin = Path.Combine(appDataRoot.FullName, "Roaming", "npm");
            if (Directory.Exists(hostNpmBin))
            {
                var currentPath = env.TryGetValue("PATH", out var pVal) ? pVal : string.Empty;
                if (!currentPath.Contains(hostNpmBin, StringComparison.OrdinalIgnoreCase))
                    env["PATH"] = hostNpmBin + Path.PathSeparator + currentPath;
            }
        }

        return env;
    }

    private sealed class ConPtySession : IDisposable
    {
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private static readonly IntPtr ProcThreadAttributePseudoConsole = new(0x00020016);

        private readonly string _id;
        private readonly string _title;
        private readonly string? _workspaceId;
        private readonly string? _workspaceName;
        private readonly string _workingDirectory;
        private readonly string _commandKind;
        private readonly object _sync = new();
        private IntPtr _hPc;
        private IntPtr _attributeList;
        private IntPtr _hProcess;
        private IntPtr _hThread;
        private readonly int _processId;
        private FileStream? _stdinStream;
        private FileStream? _stdoutStream;
        private bool _disposed;
        private volatile bool _alive = true;

        private ConPtySession(
            string id,
            string title,
            string? workspaceId,
            string? workspaceName,
            string workingDirectory,
            string commandKind,
            IntPtr hPc,
            IntPtr attributeList,
            IntPtr hProcess,
            IntPtr hThread,
            int processId,
            FileStream stdinStream,
            FileStream stdoutStream)
        {
            _id = id;
            _title = title;
            _workspaceId = workspaceId;
            _workspaceName = workspaceName;
            _workingDirectory = workingDirectory;
            _commandKind = commandKind;
            _hPc = hPc;
            _attributeList = attributeList;
            _hProcess = hProcess;
            _hThread = hThread;
            _processId = processId;
            _stdinStream = stdinStream;
            _stdoutStream = stdoutStream;
        }

        public TerminalSessionInfo ToInfo() =>
            new(_id, _title, _workspaceId, _workspaceName, _workingDirectory, _commandKind, _processId, _alive);

        public static ConPtySession Start(
            string id,
            string title,
            string? workspaceId,
            string? workspaceName,
            string workingDirectory,
            string commandKind,
            string commandLine,
            IReadOnlyDictionary<string, string> environment,
            short cols,
            short rows,
            Action<string> onOutput,
            Action<int> onExit)
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = false };
            if (!CreatePipe(out var inRead, out var inWrite, ref sa, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 ConPTY 输入管道。");
            if (!CreatePipe(out var outRead, out var outWrite, ref sa, 0))
            {
                CloseHandle(inRead); CloseHandle(inWrite);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 ConPTY 输出管道。");
            }

            IntPtr hPc = IntPtr.Zero;
            IntPtr attrList = IntPtr.Zero;
            IntPtr envBlock = IntPtr.Zero;
            try
            {
                var hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inRead, outWrite, 0, out hPc);
                if (hr != 0)
                    throw new Win32Exception(hr, $"CreatePseudoConsole 失败 (0x{hr:X8})。");

                var lpSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
                attrList = Marshal.AllocHGlobal(lpSize);
                if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref lpSize))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法初始化进程属性表。");

                if (!UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributePseudoConsole, hPc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法绑定伪终端属性。");

                var envText = PackageEnvironmentBlock.BuildText(environment);
                envBlock = Marshal.AllocHGlobal(checked(envText.Length * sizeof(char)));
                Marshal.Copy(envText.ToCharArray(), 0, envBlock, envText.Length);

                // Setting STARTF_USESTDHANDLES with null handles ensures the child binds strictly
                // to the PseudoConsole even when the parent process itself has redirected stdio.
                var startupInfo = new STARTUPINFOEX
                {
                    StartupInfo = new STARTUPINFO
                    {
                        cb = Marshal.SizeOf<STARTUPINFOEX>(),
                        dwFlags = 0x00000100, // STARTF_USESTDHANDLES
                    },
                    lpAttributeList = attrList,
                };

                var cmdBuffer = new StringBuilder(commandLine);
                if (!CreateProcessW(
                    null,
                    cmdBuffer,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    envBlock,
                    workingDirectory,
                    ref startupInfo,
                    out var procInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法启动终端进程：{commandLine}");
                }

                // Child pseudo-console now owns these ends; close parent copies so EOF propagates cleanly.
                CloseHandle(inRead); inRead = IntPtr.Zero;
                CloseHandle(outWrite); outWrite = IntPtr.Zero;

                var stdinStream = new FileStream(new SafeFileHandle(inWrite, ownsHandle: true), FileAccess.Write, 4096, isAsync: false);
                inWrite = IntPtr.Zero;
                var stdoutStream = new FileStream(new SafeFileHandle(outRead, ownsHandle: true), FileAccess.Read, 8192, isAsync: false);
                outRead = IntPtr.Zero;

                var session = new ConPtySession(
                    id, title, workspaceId, workspaceName, workingDirectory, commandKind,
                    hPc, attrList, procInfo.hProcess, procInfo.hThread, checked((int)procInfo.dwProcessId),
                    stdinStream, stdoutStream);

                _ = Task.Run(() => session.PumpOutputLoop(onOutput, onExit));
                return session;
            }
            catch
            {
                if (inRead != IntPtr.Zero) CloseHandle(inRead);
                if (inWrite != IntPtr.Zero) CloseHandle(inWrite);
                if (outRead != IntPtr.Zero) CloseHandle(outRead);
                if (outWrite != IntPtr.Zero) CloseHandle(outWrite);
                if (hPc != IntPtr.Zero) ClosePseudoConsole(hPc);
                if (attrList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attrList);
                    Marshal.FreeHGlobal(attrList);
                }
                throw;
            }
            finally
            {
                if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
            }
        }

        public void Write(string input)
        {
            lock (_sync)
            {
                if (_disposed || _stdinStream is null) return;
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(input);
                    _stdinStream.Write(bytes, 0, bytes.Length);
                    _stdinStream.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                }
            }
        }

        public void Resize(short cols, short rows)
        {
            lock (_sync)
            {
                if (_disposed || _hPc == IntPtr.Zero) return;
                _ = ResizePseudoConsole(_hPc, new COORD { X = cols, Y = rows });
            }
        }

        private void PumpOutputLoop(Action<string> onOutput, Action<int> onExit)
        {
            var byteBuf = new byte[8192];
            var charBuf = new char[8192];
            var decoder = Encoding.UTF8.GetDecoder();
            var pendingLock = new object();
            var pendingBuffer = new StringBuilder();
            var flushScheduled = false;

            void FlushPending()
            {
                string chunk;
                lock (pendingLock)
                {
                    flushScheduled = false;
                    if (pendingBuffer.Length == 0) return;
                    chunk = pendingBuffer.ToString();
                    pendingBuffer.Clear();
                }
                try { onOutput(chunk); } catch { }
            }

            try
            {
                while (!_disposed && _stdoutStream is { } stream)
                {
                    var read = stream.Read(byteBuf, 0, byteBuf.Length);
                    if (read <= 0) break;
                    var chars = decoder.GetChars(byteBuf, 0, read, charBuf, 0, flush: false);
                    if (chars <= 0) continue;

                    var flushImmediately = false;
                    var scheduleTimer = false;
                    lock (pendingLock)
                    {
                        pendingBuffer.Append(charBuf, 0, chars);
                        if (pendingBuffer.Length >= 24_576)
                        {
                            flushImmediately = true;
                        }
                        else if (!flushScheduled)
                        {
                            flushScheduled = true;
                            scheduleTimer = true;
                        }
                    }

                    if (flushImmediately)
                    {
                        FlushPending();
                    }
                    else if (scheduleTimer)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(12).ConfigureAwait(false);
                            FlushPending();
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
            finally
            {
                FlushPending();
                _alive = false;
                int exitCode = 0;
                lock (_sync)
                {
                    if (_hProcess != IntPtr.Zero && GetExitCodeProcess(_hProcess, out var code))
                        exitCode = unchecked((int)code);
                }
                try { onExit(exitCode); } catch { }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _alive = false;

                try { _stdinStream?.Dispose(); } catch { }
                _stdinStream = null;

                // Verify the owned native process handle is still active (STILL_ACTIVE == 259) before killing by PID, preventing PID-reuse kills.
                var stillRunning = _hProcess != IntPtr.Zero && GetExitCodeProcess(_hProcess, out var nativeCode) && nativeCode == 259u;
                if (stillRunning)
                {
                    try
                    {
                        using var proc = Process.GetProcessById(_processId);
                        if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                    }
                    catch { }
                }

                if (_hPc != IntPtr.Zero)
                {
                    ClosePseudoConsole(_hPc);
                    _hPc = IntPtr.Zero;
                }

                try { _stdoutStream?.Dispose(); } catch { }
                _stdoutStream = null;

                if (_attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(_attributeList);
                    Marshal.FreeHGlobal(_attributeList);
                    _attributeList = IntPtr.Zero;
                }

                if (_hThread != IntPtr.Zero) { CloseHandle(_hThread); _hThread = IntPtr.Zero; }
                if (_hProcess != IntPtr.Zero) { CloseHandle(_hProcess); _hProcess = IntPtr.Zero; }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(
            string? lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
