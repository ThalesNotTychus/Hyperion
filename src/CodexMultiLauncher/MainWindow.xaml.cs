using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace CodexMultiLauncher;

public partial class MainWindow : Window
{
    private readonly string _projectRoot;
    private readonly bool _enableAutoStealth;
    private readonly ProfileService _profiles;
    private readonly WorkspaceCatalog _workspaces;
    private readonly AgentProviderStore _agentProviders;
    private readonly ModelCatalogService _modelCatalog;
    private readonly BuiltInApiAgentRunner _builtInApiRunner;
    private readonly GenericCliAgentRunner _marketCliRunner;
    private readonly PackagedCodexLauncher _launcher = new();
    private readonly IReadOnlyDictionary<ClientKind, CodexInstanceMonitor> _monitors;
    private readonly BackgroundWindowManager _stealthWindows = new();
    private readonly AccountPoolService _accountPool;
    private readonly UiSettingsStore _settingsStore;
    private readonly GitWorktreeService _gitWorktrees;
    private readonly GroupChatCoordinator _coordinator;
    private readonly ArenaService _arena;
    private readonly ConPtyTerminalService _terminals = new();
    private readonly WebUiBridge _bridge;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _streamFlushTimer;
    private readonly DispatcherTimer _broadcastDebounceTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Task, byte> _backgroundTasks = new();
    private readonly ConcurrentDictionary<string, GroupChatRoom> _dirtyRooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ArenaSession> _dirtyArenas = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _storageSummaries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _stealthManagedWorkspaces = new(StringComparer.Ordinal);

    private IReadOnlyList<Profile> _loadedProfiles = [];
    private IReadOnlyList<DetectedCliEngine> _detectedClis = [];
    private UiSettings _currentSettings;
    private CoreWebView2Environment? _webViewEnvironment;
    private CoreWebView2Environment? _previewEnvironment;
    private Task? _webViewInitTask;
    private Task? _discoveryTask;
    private string? _lastWorkspaceIdsSignature;
    private string _sandboxStatusText = "正在检查受管账号多开沙箱…";
    private bool _isLaunching, _isClosing, _closed, _preparingSandboxes, _started, _shellReady, _previewReady, _designVisible, _designInspecting;
    private string _designUrl = "http://localhost:3000";
    private int _usageTickCounter;

    public MainWindow(string projectRoot, bool enableAutoStealth = true)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _enableAutoStealth = enableAutoStealth;
        InitializeComponent();

        _settingsStore = new UiSettingsStore(_projectRoot);
        _currentSettings = _settingsStore.Load();
        _designUrl = _currentSettings.LastPreviewUrl;
        ApplyNativeChromeTheme(_currentSettings.Theme);

        _profiles = new ProfileService(_projectRoot);
        _workspaces = new WorkspaceCatalog(_projectRoot);
        _agentProviders = new AgentProviderStore(_projectRoot);
        _modelCatalog = new ModelCatalogService(_agentProviders);
        var desktopClients = new[] { ClientKind.Codex, ClientKind.Claude, ClientKind.Antigravity };
        _monitors = desktopClients.ToDictionary(client => client, client => new CodexInstanceMonitor(_projectRoot, client));
        _accountPool = new AccountPoolService(agentProviders: _agentProviders);
        _gitWorktrees = new GitWorktreeService(_projectRoot);

        ReloadProfiles();
        _detectedClis = UniversalAgentCatalog.DetectInstalledClis(_workspaces.Workspaces);

        _builtInApiRunner = new BuiltInApiAgentRunner(_agentProviders, _accountPool.RecordTokenUsage);
        _marketCliRunner = new GenericCliAgentRunner(
            _agentProviders,
            () => _workspaces.Workspaces,
            _accountPool.RecordTokenUsage);

        var runner = new MultiEngineChatRunner(
            new CodexGroupChatRunner(_accountPool.RecordTokenUsage),
            new ClaudeCliRunner(_accountPool.RecordTokenUsage),
            new AntigravityAgentRunner(onTokenUsage: _accountPool.RecordTokenUsage),
            _builtInApiRunner,
            _marketCliRunner);

        _coordinator = new GroupChatCoordinator(
            new GroupChatStore(_projectRoot),
            GetUnifiedWorkspaces,
            runner,
            _accountPool,
            () => _currentSettings,
            _modelCatalog);

        if (_coordinator.Rooms.Count == 0)
        {
            var initialRepo = !string.IsNullOrWhiteSpace(_currentSettings.LastRepositoryPath) && Directory.Exists(_currentSettings.LastRepositoryPath)
                ? _currentSettings.LastRepositoryPath
                : _projectRoot;
            _coordinator.CreateRoom(initialRepo, "主研发指挥室", ChatSandboxMode.WorkspaceWrite);
        }

        // Auto-enroll all configured accounts (Direct API + Market CLI + Codex + Claude + Antigravity) and upgrade empty default rooms to full Agent mode!
        _coordinator.SyncRosterWithWorkspaces(upgradeEmptyDefaultRoomToWrite: true);

        _arena = new ArenaService(
            _projectRoot,
            _gitWorktrees,
            GetUnifiedWorkspaces,
            runner,
            _accountPool,
            () => _currentSettings);

        _bridge = new WebUiBridge(this);

        _coordinator.Warning += warning => PushToast("warning", warning);
        _coordinator.RoomUpdated += room => _dirtyRooms[room.Id] = room;
        _arena.SessionUpdated += session => _dirtyArenas[session.Id] = session;

        _terminals.OutputReceived += (sessionId, chunk) =>
            Dispatcher.BeginInvoke(() => PostEvent("terminal.output", new { sessionId, data = chunk }), DispatcherPriority.Background);
        _terminals.SessionExited += (sessionId, exitCode) =>
            Dispatcher.BeginInvoke(() => PostEvent("terminal.exited", new { sessionId, exitCode }), DispatcherPriority.Background);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += RefreshTimer_Tick;

        _streamFlushTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(60) };
        _streamFlushTimer.Tick += StreamFlushTimer_Tick;

        _broadcastDebounceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _broadcastDebounceTimer.Tick += (_, _) =>
        {
            _broadcastDebounceTimer.Stop();
            BroadcastFullState();
        };

        StateChanged += (_, _) => PostEvent("window.state", new { isMaximized = WindowState == WindowState.Maximized });

        Loaded += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            try
            {
                _streamFlushTimer.Start();
                await EnsureWebViewReadyAsync();
                if (_enableAutoStealth)
                {
                    Track(RefreshDiscoveryFromBridgeAsync());
                    Track(PrepareManagedSandboxesAsync());
                    Track(RefreshAllStorageSummariesAsync());
                    Track(RefreshPoolUsageAsync(null, all: true, force: true));
                    Track(AutoProvisionAntigravityCliIfNeededAsync());
                    _refreshTimer.Start();
                }
            }
            catch (Exception ex)
            {
                BootStatusText.Text = $"初始化失败：{ex.Message}";
                Debug.WriteLine($"[MainWindow.Loaded] {ex}");
            }
        };
    }

    internal string ProjectRoot => _projectRoot;
    internal UiSettings CurrentSettings => _currentSettings;
    internal AccountPoolService AccountPool => _accountPool;
    internal AgentProviderStore AgentProviders => _agentProviders;
    internal GitWorktreeService GitWorktrees => _gitWorktrees;
    internal GroupChatCoordinator Coordinator => _coordinator;
    internal ArenaService Arena => _arena;
    internal ConPtyTerminalService Terminals => _terminals;
    internal CancellationToken LifetimeToken => _lifetime.Token;

    internal IReadOnlyList<CodexWorkspace> GetUnifiedWorkspaces()
    {
        var hidden = (_currentSettings.HiddenWorkspaceIds ?? []).ToHashSet(StringComparer.Ordinal);
        var hideAllOriginalClaude = hidden.Any(h => h.StartsWith("original-claude", StringComparison.OrdinalIgnoreCase));
        return _agentProviders.BuildWorkspaces()
            .Concat(_workspaces.Workspaces)
            .Where(w => !hidden.Contains(w.Id) && !(hideAllOriginalClaude && w.Client == ClientKind.Claude && w.Kind == CodexWorkspaceKind.Original))
            .ToArray();
    }

    internal CodexWorkspace? FindUnifiedWorkspaceById(string? id) =>
        SelectWorkspaceById(GetUnifiedWorkspaces(), id);

    public static CodexWorkspace? SelectWorkspaceById(IEnumerable<CodexWorkspace> workspaces, string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : workspaces.FirstOrDefault(workspace => string.Equals(workspace.Id, id, StringComparison.Ordinal));

    internal Task EnsureWebViewReadyAsync() => _webViewInitTask ??= InitializeWebViewsInternalAsync();

    private async Task InitializeWebViewsInternalAsync()
    {
        try
        {
            ShellWebView.AllowExternalDrop = false;
            _webViewEnvironment = await WebUiResourceServer.CreateEnvironmentAsync(_projectRoot);
            await ShellWebView.EnsureCoreWebView2Async(_webViewEnvironment);

            var core = ShellWebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            WebUiResourceServer.Attach(core);

            core.NavigationStarting += (_, navArgs) =>
            {
                if (string.IsNullOrWhiteSpace(navArgs.Uri) ||
                    navArgs.Uri.StartsWith(WebUiResourceServer.Origin, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(navArgs.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                navArgs.Cancel = true;
                if (Uri.TryCreate(navArgs.Uri, UriKind.Absolute, out var extUri) &&
                    (extUri.Scheme == Uri.UriSchemeHttps || extUri.Scheme == Uri.UriSchemeHttp))
                {
                    try { Process.Start(new ProcessStartInfo { FileName = extUri.AbsoluteUri, UseShellExecute = true }); }
                    catch { }
                }
            };

            core.NewWindowRequested += (_, winArgs) =>
            {
                winArgs.Handled = true;
                if (Uri.TryCreate(winArgs.Uri, UriKind.Absolute, out var extUri) &&
                    (extUri.Scheme == Uri.UriSchemeHttps || extUri.Scheme == Uri.UriSchemeHttp))
                {
                    try { Process.Start(new ProcessStartInfo { FileName = extUri.AbsoluteUri, UseShellExecute = true }); }
                    catch { }
                }
            };

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavCompleted(object? s, CoreWebView2NavigationCompletedEventArgs args)
            {
                core.NavigationCompleted -= OnNavCompleted;
                _shellReady = true;
                BootOverlay.Visibility = Visibility.Collapsed;
                tcs.TrySetResult();
            }
            core.NavigationCompleted += OnNavCompleted;

            core.WebMessageReceived += async (_, args) =>
            {
                if (_isClosing) return;
                if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var src) ||
                    !string.Equals($"{src.Scheme}://{src.Authority}", WebUiResourceServer.Origin, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                try
                {
                    var json = args.TryGetWebMessageAsString();
                    if (!string.IsNullOrWhiteSpace(json))
                        await _bridge.HandleRawMessageAsync(json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ShellWebView.WebMessageReceived] {ex.Message}");
                }
            };

            core.Navigate($"{WebUiResourceServer.Origin}/index.html");
            await tcs.Task;
        }
        catch (Exception ex)
        {
            BootStatusText.Text = $"WebView2 初始化失败：{ex.Message}";
            throw;
        }
    }

    internal async Task<string> ExecuteShellScriptAsync(string script)
    {
        await EnsureWebViewReadyAsync();
        return await ShellWebView.CoreWebView2.ExecuteScriptAsync(script);
    }

    internal async Task CaptureShellPreviewPngAsync(string outputPath)
    {
        await EnsureWebViewReadyAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        await ShellWebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
    }

    private void ReloadProfiles()
    {
        _loadedProfiles = _profiles.LoadOrCreateDefaults();
        _workspaces.Reload(_loadedProfiles);
    }

    private void StreamFlushTimer_Tick(object? sender, EventArgs e)
    {
        if (!_shellReady || _isClosing) return;
        var flushedAny = false;
        foreach (var pair in _dirtyRooms.ToArray())
        {
            if (_dirtyRooms.TryRemove(pair.Key, out var room))
            {
                PostEvent("room.updated", MapRoomDto(room));
                flushedAny = true;
            }
        }
        foreach (var pair in _dirtyArenas.ToArray())
        {
            if (_dirtyArenas.TryRemove(pair.Key, out var session))
            {
                PostEvent("arena.updated", session);
                flushedAny = true;
            }
        }
        if (flushedAny)
        {
            // Also keep topbar/fleet active task counters & token counts live!
            PostEvent("pool.updated", _accountPool.BuildSummary(GetUnifiedWorkspaces(), _currentSettings.FailoverThresholdPercent));
        }
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing) return;
        Track(RefreshDiscoveryFromBridgeAsync());
        _usageTickCounter++;
        if (_usageTickCounter >= 20)
        {
            _usageTickCounter = 0;
            Track(RefreshPoolUsageAsync(null, all: true, force: false));
            Track(RefreshAllStorageSummariesAsync());
        }
    }

    internal void PostToShell(object payload)
    {
        if (!_shellReady || _isClosing || ShellWebView.CoreWebView2 is null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => PostToShell(payload));
            return;
        }
        try
        {
            var json = JsonSerializer.Serialize(payload, WebUiBridge.JsonOptions);
            ShellWebView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"[PostToShell] Ignored transient WebView2 state error: {ex.Message}");
        }
    }

    internal void PostEvent(string eventName, object? data) =>
        PostToShell(new { @event = eventName, data });

    internal void PushToast(string kind, string message) =>
        PostEvent("toast", new { kind, message });

    internal void BroadcastFullState()
    {
        if (!_shellReady || _isClosing) return;
        PostEvent("state.snapshot", BuildFullStateDto());
    }

    internal void RequestBroadcastFullState()
    {
        if (!_shellReady || _isClosing) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RequestBroadcastFullState, DispatcherPriority.Background);
            return;
        }
        _broadcastDebounceTimer.Stop();
        _broadcastDebounceTimer.Start();
    }

    internal void ApplyNativeChromeTheme(string? theme)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ApplyNativeChromeTheme(theme));
            return;
        }

        (byte r, byte g, byte b) bg;
        (byte r, byte g, byte b) border;
        (byte r, byte g, byte b) panel;
        (byte r, byte g, byte b) header;
        (byte r, byte g, byte b) text;
        (byte r, byte g, byte b) btnBg;
        (byte r, byte g, byte b) btnBorder;

        switch ((theme ?? "obsidian").Trim().ToLowerInvariant())
        {
            case "daylight":
                bg = (244, 246, 250);
                border = (203, 213, 225);
                panel = (255, 255, 255);
                header = (238, 242, 247);
                text = (15, 23, 42);
                btnBg = (226, 232, 240);
                btnBorder = (203, 213, 225);
                break;
            case "matrix":
                bg = (6, 13, 9);
                border = (22, 48, 34);
                panel = (9, 20, 14);
                header = (12, 27, 19);
                text = (209, 250, 229);
                btnBg = (18, 41, 29);
                btnBorder = (31, 71, 50);
                break;
            case "aurora":
                bg = (14, 11, 22);
                border = (39, 31, 61);
                panel = (19, 15, 31);
                header = (24, 19, 40);
                text = (237, 233, 254);
                btnBg = (34, 26, 56);
                btnBorder = (53, 41, 87);
                break;
            default:
                bg = (11, 14, 20);
                border = (30, 38, 54);
                panel = (15, 20, 31);
                header = (17, 23, 36);
                text = (203, 213, 225);
                btnBg = (30, 41, 59);
                btnBorder = (51, 65, 85);
                break;
        }

        static System.Windows.Media.SolidColorBrush Brush((byte r, byte g, byte b) c) =>
            new(System.Windows.Media.Color.FromRgb(c.r, c.g, c.b));

        var bgBrush = Brush(bg);
        var borderBrush = Brush(border);
        Background = bgBrush;
        RootFrame.Background = bgBrush;
        RootFrame.BorderBrush = borderBrush;
        BootOverlay.Background = bgBrush;
        ShellWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, bg.r, bg.g, bg.b);
        PreviewSplitter.Background = borderBrush;
        PreviewContainer.Background = Brush(panel);
        PreviewContainer.BorderBrush = borderBrush;
        PreviewHeaderBorder.Background = Brush(header);
        PreviewHeaderBorder.BorderBrush = borderBrush;
        PreviewTitleText.Foreground = Brush(text);
        PreviewCloseButton.Background = Brush(btnBg);
        PreviewCloseButton.Foreground = Brush(text);
        PreviewCloseButton.BorderBrush = Brush(btnBorder);
    }

    internal object BuildTelemetryTickDto()
    {
        var workspaces = GetUnifiedWorkspaces();
        var pool = _accountPool.BuildSummary(workspaces, _currentSettings.FailoverThresholdPercent);
        var allTracked = _monitors.Values.SelectMany(m => m.Instances).ToArray();
        var totalVisibleWindows = allTracked.Where(i => !i.IsStopped).Count(i => i.HasMainWindow);
        var totalHiddenWindows = _stealthWindows.TotalHiddenCount;
        var warnings = _monitors.Values.Select(m => m.LastWarning)
            .Append(_workspaces.LastWarning)
            .Append(_coordinator.PersistenceWarning)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new
        {
            IsLaunching = _isLaunching,
            IsPreparingSandboxes = _preparingSandboxes,
            SandboxStatusText = _sandboxStatusText,
            ClaudeCliAvailable = ClaudeCliRunner.TryFindClaudeCliExecutable(null, workspaces) is not null,
            AgyCliAvailable = AntigravityAgentRunner.TryFindAgyCliExecutable() is not null,
            Stealth = new
            {
                Enabled = _currentSettings.StealthBackgroundMode,
                VisibleClientWindows = totalVisibleWindows,
                HiddenClientWindows = totalHiddenWindows,
            },
            Pool = pool,
            Workspaces = MapWorkspacesDto(workspaces, allTracked),
            Instances = MapInstancesDto(allTracked),
            Warnings = warnings,
        };
    }

    private object[] MapInstancesDto(TrackedCodexInstance[] allTracked) =>
        allTracked
            .Select(i =>
            {
                var winState = _stealthWindows.GetWorkspaceWindowState(i.ProfileId, i.MemberProcessIds.Append(i.ProcessId));
                return (object)new
                {
                    i.ProfileId,
                    i.ProfileName,
                    Client = i.Workspace?.Client ?? ClientKind.Codex,
                    i.ProcessId,
                    i.StartedAtUtc,
                    i.Source,
                    i.SourceText,
                    i.State,
                    i.StateText,
                    i.ProcessCount,
                    i.UptimeText,
                    i.CpuPercent,
                    i.IsCpuAvailable,
                    i.CpuText,
                    i.WorkingSetBytes,
                    i.IsWorkingSetAvailable,
                    i.WorkingSetText,
                    i.HasMainWindow,
                    WindowText = winState.HiddenWindowCount > 0 && winState.VisibleWindowCount == 0
                        ? $"👻 后台隐身 ({winState.HiddenWindowCount})"
                        : i.WindowText,
                    IsStealthHidden = winState.HiddenWindowCount > 0 && winState.VisibleWindowCount == 0,
                    i.IsStopped,
                };
            })
            .ToArray();

    private object[] MapWorkspacesDto(IReadOnlyList<CodexWorkspace> workspaces, TrackedCodexInstance[] allTracked) =>
        workspaces.Select(w =>
        {
            var wsInstances = allTracked.Where(i => i.ProfileId == w.Id && !i.IsStopped).ToArray();
            var memberPids = wsInstances.SelectMany(i => i.MemberProcessIds.Append(i.ProcessId));
            var stealthState = _stealthWindows.GetWorkspaceWindowState(w.Id, memberPids);
            return (object)new
            {
                w.Id,
                w.DisplayName,
                w.Kind,
                w.Client,
                w.ClientName,
                w.HasDesktopClient,
                w.ProviderId,
                w.DefaultModel,
                w.Subtitle,
                w.IsLaunchable,
                w.CanReadUsage,
                w.ElectronUserData,
                w.CodexHome,
                w.ClaudeHome,
                w.AntigravityHome,
                StorageText = !w.HasDesktopClient
                    ? (w.Client == ClientKind.DirectApi ? "DPAPI 加密直连 · 零客户端体积" : "独立 CLI 沙箱目录")
                    : (_storageSummaries.TryGetValue(w.Id, out var st) ? st : (w.Kind == CodexWorkspaceKind.Managed ? "正在统计…" : "原始官方目录")),
                RunningCount = wsInstances.Length,
                TotalMemoryText = wsInstances.Length > 0 && wsInstances.All(i => i.IsWorkingSetAvailable)
                    ? RuntimeFormatting.FormatBytes(wsInstances.Sum(i => i.WorkingSetBytes))
                    : null,
                TotalCpuText = wsInstances.Length > 0 && wsInstances.All(i => i.IsCpuAvailable)
                    ? $"{wsInstances.Sum(i => i.CpuPercent):0.0}%"
                    : null,
                VisibleWindowCount = stealthState.VisibleWindowCount,
                HiddenWindowCount = stealthState.HiddenWindowCount,
                IsLoginWindowRevealed = wsInstances.Length > 0
                    && stealthState.VisibleWindowCount > 0
                    && _stealthWindows.IsTemporarilyRevealedForLogin(w.Id),
            };
        }).ToArray();

    internal object BuildFullStateDto()
    {
        var workspaces = GetUnifiedWorkspaces();
        var pool = _accountPool.BuildSummary(workspaces, _currentSettings.FailoverThresholdPercent);
        var allTracked = _monitors.Values.SelectMany(m => m.Instances).ToArray();
        var instances = MapInstancesDto(allTracked);

        var totalVisibleWindows = allTracked.Where(i => !i.IsStopped).Count(i => i.HasMainWindow);
        var totalHiddenWindows = _stealthWindows.TotalHiddenCount;

        var warnings = _monitors.Values.Select(m => m.LastWarning)
            .Append(_workspaces.LastWarning)
            .Append(_coordinator.PersistenceWarning)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new
        {
            ProjectRoot = _projectRoot,
            IsMaximized = WindowState == WindowState.Maximized,
            IsLaunching = _isLaunching,
            IsPreparingSandboxes = _preparingSandboxes,
            SandboxStatusText = _sandboxStatusText,
            ClaudeCliAvailable = ClaudeCliRunner.TryFindClaudeCliExecutable(null, workspaces) is not null,
            AgyCliAvailable = AntigravityAgentRunner.TryFindAgyCliExecutable() is not null,
            Settings = _currentSettings,
            HiddenWorkspaceCount = (_currentSettings.HiddenWorkspaceIds ?? []).Count,
            Stealth = new
            {
                Enabled = _currentSettings.StealthBackgroundMode,
                VisibleClientWindows = totalVisibleWindows,
                HiddenClientWindows = totalHiddenWindows,
            },
            Pool = pool,
            Workspaces = MapWorkspacesDto(workspaces, allTracked),
            UniversalAgents = _agentProviders.List().Select(r =>
            {
                var resolvedHome = _agentProviders.ResolveHomeDirectory(r);
                var isAgyBacked = AntigravityAgentRunner.IsAntigravityBackedCliRecord(r, resolvedHome);
                var effModel = isAgyBacked
                    && (string.IsNullOrWhiteSpace(r.Model)
                        || string.Equals(r.Model, "gemini-2.5-pro", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(r.Model, "pro", StringComparison.OrdinalIgnoreCase))
                    ? "gemini-4-carbon"
                    : r.Model;
                return new
                {
                    r.Id,
                    WorkspaceId = AgentProviderStore.ToWorkspaceId(r),
                    r.DisplayName,
                    r.AgentKind,
                    PresetId = isAgyBacked ? "antigravity-cli" : r.PresetId,
                    r.Protocol,
                    r.BaseUrl,
                    Model = effModel,
                    HasApiKey = !string.IsNullOrWhiteSpace(r.EncryptedApiKey),
                    r.MaskedApiKey,
                    r.CustomCliExecutable,
                    r.CustomCliArgsTemplate,
                    r.CustomHomeDirectory,
                    r.TokenBudgetLimit,
                    r.CreatedAtUtc,
                };
            }).ToArray(),
            ApiPresets = UniversalAgentCatalog.ApiPresets,
            CliPresets = _detectedClis,
            Instances = instances,
            Projects = BuildProjectsDto(),
            Rooms = _coordinator.Rooms.Select(MapRoomDto).ToArray(),
            ArenaSessions = _arena.GetSessions(),
            Terminals = _terminals.ListSessions(),
            Design = new
            {
                Visible = _designVisible,
                Url = _designUrl,
                Inspecting = _designInspecting,
            },
            Warnings = warnings,
        };
    }

    private object MapRoomDto(GroupChatRoom room)
    {
        var workspaces = GetUnifiedWorkspaces();
        return new
        {
            room.Id,
            room.Title,
            room.WorkingDirectory,
            room.SandboxMode,
            room.RepositoryRoot,
            room.WorktreeBranch,
            room.DedicatedWorkspaceId,
            room.PrimaryWorkspaceId,
            ProjectPath = NormalizeProjectRootPath(room.RepositoryRoot ?? room.WorkingDirectory),
            room.IsDedicatedAgentRoom,
            room.IsIsolatedWorktree,
            room.IsRunning,
            room.ActivityText,
            Agents = room.Agents.Select(a =>
            {
                var ws = workspaces.FirstOrDefault(w => w.Id == a.WorkspaceId);
                return new
                {
                    a.Id,
                    a.Alias,
                    a.WorkspaceId,
                    WorkspaceName = ws?.DisplayName ?? "账号不可用",
                    Client = ws?.Client ?? ClientKind.Codex,
                    a.Model,
                    a.ReasoningEffort,
                    a.ServiceTier,
                };
            }).ToArray(),
            Messages = room.Messages.Select(m => new
            {
                m.Id,
                m.Role,
                m.Sender,
                m.WorkspaceId,
                m.WorkspaceName,
                m.AgentId,
                m.ModelSummary,
                m.SentAtUtc,
                m.TimeText,
                m.State,
                m.StateText,
                m.ActivityStatus,
                m.FailoverNotice,
                m.ThinkingText,
                m.Steps,
                m.Text,
            }).ToArray(),
        };
    }

    internal UiSettings UpdateSettings(UiSettings next)
    {
        var prevStealth = _currentSettings.StealthBackgroundMode;
        var prevTheme = _currentSettings.Theme;
        _currentSettings = _settingsStore.Save(next);
        if (!string.Equals(prevTheme, _currentSettings.Theme, StringComparison.OrdinalIgnoreCase))
            ApplyNativeChromeTheme(_currentSettings.Theme);
        if (_enableAutoStealth && _currentSettings.StealthBackgroundMode && !prevStealth)
        {
            _stealthWindows.EnforceStealthOnInstances(_monitors.Values.SelectMany(m => m.Instances));
        }
        BroadcastFullState();
        return _currentSettings;
    }

    internal object HideAllClientWindowsNow()
    {
        var all = _monitors.Values.SelectMany(m => m.Instances).ToArray();
        foreach (var ws in _workspaces.Workspaces)
        {
            _stealthWindows.RevokeTemporaryLoginWindow(ws.Id);
            _stealthManagedWorkspaces[ws.Id] = 1;
        }
        var count = _stealthWindows.EnforceStealthOnInstances(all);
        _currentSettings = _settingsStore.Save(_currentSettings with { StealthBackgroundMode = true });
        BroadcastFullState();
        PushToast("success", count > 0
            ? $"👻 已将 {count} 个外部客户端窗口转入后台隐身运行（桌面与任务栏均不可见）"
            : "👻 后台隐身托管已生效，当前桌面无可见的外部 Agent 窗口");
        return BuildFullStateDto();
    }

    internal object RevealAllClientWindowsNow()
    {
        _stealthManagedWorkspaces.Clear();
        var count = _stealthWindows.RevealAllHiddenWindows();
        _currentSettings = _settingsStore.Save(_currentSettings with { StealthBackgroundMode = false });
        BroadcastFullState();
        PushToast("info", count > 0 ? $"👁 已恢复显示 {count} 个客户端窗口" : "当前没有处于隐藏状态的窗口");
        return BuildFullStateDto();
    }

    internal async Task<object> SetWorkspaceLoginWindowVisibilityAsync(string workspaceId, bool reveal)
    {
        var ws = _workspaces.FindById(workspaceId) ?? throw new InvalidOperationException("找不到指定账号。");
        var monitor = _monitors[ws.Client];

        if (!reveal)
        {
            _stealthWindows.RevokeTemporaryLoginWindow(workspaceId);
            var hidden = 0;
            foreach (var inst in monitor.Instances.Where(i => i.ProfileId == workspaceId && !i.IsStopped))
                hidden += _stealthWindows.HideInstanceWindows(inst.ProcessId, workspaceId, inst.MemberProcessIds);
            BroadcastFullState();
            PushToast("success", $"👻 “{ws.DisplayName}”已收回后台隐身运行（隐藏 {hidden} 个窗口）");
            return BuildFullStateDto();
        }

        _stealthWindows.AllowTemporaryLoginWindow(workspaceId, TimeSpan.FromMinutes(10));
        var existing = monitor.Instances.FirstOrDefault(i => i.ProfileId == workspaceId && !i.IsStopped);
        if (existing is not null)
        {
            _stealthWindows.RevealInstanceWindows(existing.ProcessId, workspaceId, existing.MemberProcessIds);
            var focused = monitor.Focus(existing);
            if (!focused && ws.Client == ClientKind.Antigravity)
            {
                await Task.Run(() => LaunchAntigravityProcess(ws.DataPaths, folderPath: null));
                await RefreshDiscoveryFromBridgeAsync();
            }
            BroadcastFullState();
            PushToast("info", $"🖥 已显现并前置“{ws.DisplayName}”桌面客户端窗口。");
            return BuildFullStateDto();
        }

        await LaunchOfficialClientAsync(workspaceId, workingDirectory: null, stealth: false);
        return BuildFullStateDto();
    }

    internal string? PickFolderDialog(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    internal async Task<object> CreateProfileFromBridgeAsync(string displayName, ClientKind client)
    {
        var profile = _profiles.CreateProfile(displayName, client);
        ReloadProfiles();
        _coordinator.SyncRosterWithWorkspaces();
        if (client == ClientKind.Codex)
        {
            var ws = _workspaces.FindById(profile.Id);
            if (ws is not null)
            {
                try { await WorkspaceSandboxService.PrepareAsync(ws, _lifetime.Token); } catch { }
            }
        }
        _ = InspectWorkspaceStorageAsync(profile.Id);
        _ = RefreshPoolUsageAsync(profile.Id, all: false, force: true);
        BroadcastFullState();
        PushToast("success", $"✓ 已创建隔离账号“{profile.DisplayName}”并自动加入指挥舱 @ 名单！");
        return BuildFullStateDto();
    }

    internal object RenameProfileFromBridge(string profileId, string displayName)
    {
        var updated = _profiles.RenameProfile(profileId, displayName);
        ReloadProfiles();
        _coordinator.SyncRosterWithWorkspaces();
        BroadcastFullState();
        PushToast("success", $"账号已重命名为“{updated.DisplayName}”。");
        return BuildFullStateDto();
    }

    internal async Task<object> DeleteProfileFromBridgeAsync(string profileId, bool deleteStorage, bool stopRunning = true)
    {
        var targetProfile = _loadedProfiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("找不到要移除的账号配置。");

        var runningInstances = _monitors.Values
            .SelectMany(m => m.Instances)
            .Where(i => string.Equals(i.ProfileId, profileId, StringComparison.Ordinal) && !i.IsStopped)
            .ToArray();

        if (runningInstances.Length > 0)
        {
            if (!stopRunning)
                throw new InvalidOperationException("该账号仍有正在运行的守护实例，请先关闭该实例后再删除配置。");

            var shouldKillAll = deleteStorage || _stealthManagedWorkspaces.ContainsKey(profileId);
            if (shouldKillAll)
            {
                var selfPid = checked((uint)Environment.ProcessId);
                foreach (var inst in runningInstances)
                {
                    if (inst.ProcessId == selfPid || inst.MemberProcessIds.Contains(selfPid)) continue;
                    try { ControlInstance(inst.ProcessId, inst.StartedAtUtc.ToString("O"), "kill"); } catch { }
                }
                await Task.Delay(250);
                try { await Task.WhenAll(_monitors.Values.Select(m => m.RefreshAllAsync(_workspaces))); } catch { }
            }
        }

        var profilePaths = _profiles.GetPaths(targetProfile);
        _profiles.DeleteProfile(profileId, deleteStorageDirectory: false);
        _stealthManagedWorkspaces.TryRemove(profileId, out _);
        _storageSummaries.TryRemove(profileId, out _);

        string? storageWarn = null;
        if (deleteStorage)
        {
            try
            {
                ProfilePathSafety.ThrowIfReparsePath(profilePaths.ProfileDirectory);
                if (Directory.Exists(profilePaths.ProfileDirectory))
                    Directory.Delete(profilePaths.ProfileDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                storageWarn = "（部分残留缓存文件仍被系统占用，账号已移除，稍后可手动清理目录）";
            }
        }

        ReloadProfiles();
        _coordinator.SyncRosterWithWorkspaces();
        BroadcastFullState();
        PushToast("success", $"✓ 已移除账号“{targetProfile.DisplayName}”{storageWarn ?? string.Empty}");
        return BuildFullStateDto();
    }

    internal object HideWorkspaceFromBridge(string workspaceId)
    {
        var ws = FindUnifiedWorkspaceById(workspaceId) ?? throw new InvalidOperationException("找不到该账号。");
        var list = (_currentSettings.HiddenWorkspaceIds ?? []).ToList();
        if (!list.Contains(workspaceId, StringComparer.Ordinal))
            list.Add(workspaceId);
        _currentSettings = _settingsStore.Save(_currentSettings with { HiddenWorkspaceIds = list });
        _coordinator.SyncRosterWithWorkspaces();
        BroadcastFullState();
        PushToast("success", $"✓ 已从列表移除“{ws.DisplayName}”（系统原有目录保持不动，可在底部高级面板随时恢复）");
        return BuildFullStateDto();
    }

    internal object RestoreHiddenWorkspacesFromBridge()
    {
        var count = (_currentSettings.HiddenWorkspaceIds ?? []).Count;
        _currentSettings = _settingsStore.Save(_currentSettings with { HiddenWorkspaceIds = [] });
        _coordinator.SyncRosterWithWorkspaces(forceReEnrollAll: true);
        BroadcastFullState();
        PushToast("success", count > 0 ? $"✓ 已恢复 {count} 个隐藏的系统原始账号" : "当前没有已隐藏的账号");
        return BuildFullStateDto();
    }

    internal object ConvertWorkspaceToClientlessFromBridge(string workspaceId)
    {
        var ws = FindUnifiedWorkspaceById(workspaceId) ?? throw new InvalidOperationException("未找到要转换的账号。");
        if (!ws.HasDesktopClient)
            throw new InvalidOperationException("该账号已经是免客户端模式。");

        string presetId;
        string preservedHome;
        switch (ws.Client)
        {
            case ClientKind.Antigravity:
                presetId = "antigravity-cli";
                preservedHome = ws.Kind == CodexWorkspaceKind.Managed && !string.IsNullOrWhiteSpace(ws.AntigravityHome)
                    ? ws.AntigravityHome!
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                break;
            case ClientKind.Claude:
                presetId = "claude-code";
                preservedHome = ws.Kind == CodexWorkspaceKind.Managed && !string.IsNullOrWhiteSpace(ws.ClaudeHome)
                    ? Directory.GetParent(ws.ClaudeHome!)?.FullName ?? ws.ClaudeHome!
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                break;
            default:
                presetId = "codex-cli";
                preservedHome = !string.IsNullOrWhiteSpace(ws.CodexHome)
                    ? Directory.GetParent(ws.CodexHome!)?.FullName ?? ws.CodexHome!
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                break;
        }

        var saved = _agentProviders.Upsert(new UniversalAgentUpsertRequest(
            Id: null,
            DisplayName: ws.DisplayName,
            AgentKind: UniversalAgentKind.MarketCli,
            PresetId: presetId,
            Protocol: ApiProtocolKind.OpenAiCompatible,
            BaseUrl: null,
            Model: null,
            ApiKeyPlaintext: null,
            CustomHomeDirectory: preservedHome));

        var newWsId = AgentProviderStore.ToWorkspaceId(saved);
        _coordinator.RebindWorkspaceId(workspaceId, newWsId);

        if (ws.Kind == CodexWorkspaceKind.Managed)
        {
            _profiles.DeleteProfile(ws.Id, deleteStorageDirectory: false);
            _stealthManagedWorkspaces.TryRemove(ws.Id, out _);
            ReloadProfiles();
        }
        else
        {
            var hiddenList = (_currentSettings.HiddenWorkspaceIds ?? []).ToList();
            if (!hiddenList.Contains(ws.Id, StringComparer.Ordinal))
                hiddenList.Add(ws.Id);
            _currentSettings = _settingsStore.Save(_currentSettings with { HiddenWorkspaceIds = hiddenList });
        }

        _detectedClis = UniversalAgentCatalog.DetectInstalledClis(_workspaces.Workspaces);
        _coordinator.SyncRosterWithWorkspaces();
        BroadcastFullState();
        PushToast("success", $"✓ 已将“{ws.DisplayName}”转为免客户端 CLI 模式，原目录 {preservedHome} 完整保留！");
        return BuildFullStateDto();
    }

    internal async Task<object> UpsertUniversalAgentFromBridgeAsync(UniversalAgentUpsertRequest request, bool runPingTest = false)
    {
        var saved = _agentProviders.Upsert(request);
        ClaudeCliRunner.InvalidateCliCache();
        AntigravityAgentRunner.InvalidateDiscoveryCache();
        _detectedClis = UniversalAgentCatalog.DetectInstalledClis(_workspaces.Workspaces);
        _coordinator.SyncRosterWithWorkspaces();
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            var newWsId = AgentProviderStore.ToWorkspaceId(saved);
            var readyIds = _accountPool.BuildSummary(GetUnifiedWorkspaces(), _currentSettings.FailoverThresholdPercent)
                .Entries
                .Where(e => e.AuthReady)
                .Select(e => e.WorkspaceId)
                .ToHashSet(StringComparer.Ordinal);
            if (readyIds.Contains(newWsId))
            {
                foreach (var room in _coordinator.Rooms.Where(r => !r.IsDedicatedAgentRoom && !r.IsRunning))
                {
                    if (string.IsNullOrWhiteSpace(room.PrimaryWorkspaceId) || !readyIds.Contains(room.PrimaryWorkspaceId))
                        _coordinator.SetRoomPrimaryWorkspace(room, newWsId);
                }
            }
        }
        string? pingNote = null;
        if (runPingTest && saved.AgentKind == UniversalAgentKind.DirectApi)
        {
            var res = await _builtInApiRunner.TestConnectionAsync(saved, _lifetime.Token);
            pingNote = res.Summary;
        }
        BroadcastFullState();
        PushToast("success", pingNote is not null
            ? $"✓ 已保存智能体“{saved.DisplayName}”并加入 @ 名单 · {pingNote}"
            : $"✓ 已接入免客户端智能体“{saved.DisplayName}”并自动加入指挥舱 @ 名单！");
        return BuildFullStateDto();
    }

    internal object DeleteUniversalAgentFromBridge(string idOrWorkspaceId)
    {
        _agentProviders.Delete(idOrWorkspaceId, deleteIsolatedHome: true);
        _coordinator.SyncRosterWithWorkspaces();
        BroadcastFullState();
        PushToast("success", "✓ 已移除该智能体配置与隔离目录");
        return BuildFullStateDto();
    }

    internal async Task<ApiConnectionTestResult> TestUniversalAgentFromBridgeAsync(UniversalAgentUpsertRequest draftOrExisting)
    {
        UniversalAgentRecord tempRecord;
        if (!string.IsNullOrWhiteSpace(draftOrExisting.Id) && string.IsNullOrWhiteSpace(draftOrExisting.ApiKeyPlaintext))
        {
            var existing = _agentProviders.FindById(draftOrExisting.Id)
                ?? throw new InvalidOperationException("找不到要测试的智能体配置。");
            tempRecord = existing with
            {
                Protocol = draftOrExisting.Protocol,
                BaseUrl = string.IsNullOrWhiteSpace(draftOrExisting.BaseUrl) ? existing.BaseUrl : draftOrExisting.BaseUrl.Trim().TrimEnd('/'),
                Model = string.IsNullOrWhiteSpace(draftOrExisting.Model) ? existing.Model : draftOrExisting.Model.Trim(),
            };
        }
        else
        {
            var preset = UniversalAgentCatalog.GetApiPreset(draftOrExisting.PresetId);
            var keyEnc = DpapiSecretProtector.Protect(draftOrExisting.ApiKeyPlaintext);
            if (preset.RequiresApiKey && string.IsNullOrWhiteSpace(keyEnc))
                throw new InvalidOperationException("请先填写 API Key 再进行连通性自检。");
            tempRecord = new UniversalAgentRecord(
                Id: "probe-temp",
                DisplayName: string.IsNullOrWhiteSpace(draftOrExisting.DisplayName) ? "Probe" : draftOrExisting.DisplayName.Trim(),
                AgentKind: UniversalAgentKind.DirectApi,
                PresetId: draftOrExisting.PresetId,
                Protocol: draftOrExisting.Protocol,
                BaseUrl: (string.IsNullOrWhiteSpace(draftOrExisting.BaseUrl) ? preset.DefaultBaseUrl : draftOrExisting.BaseUrl).Trim().TrimEnd('/'),
                Model: (string.IsNullOrWhiteSpace(draftOrExisting.Model) ? preset.DefaultModel : draftOrExisting.Model).Trim(),
                EncryptedApiKey: keyEnc,
                MaskedApiKey: DpapiSecretProtector.Mask(draftOrExisting.ApiKeyPlaintext),
                CustomCliExecutable: null,
                CustomCliArgsTemplate: null,
                TokenBudgetLimit: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        var result = await _builtInApiRunner.TestConnectionAsync(tempRecord, _lifetime.Token);
        PushToast("success", $"⚡ {result.Summary}");
        return result;
    }

    internal object ScanMarketClisFromBridge()
    {
        ClaudeCliRunner.InvalidateCliCache();
        AntigravityAgentRunner.InvalidateDiscoveryCache();
        _detectedClis = UniversalAgentCatalog.DetectInstalledClis(_workspaces.Workspaces);
        BroadcastFullState();
        var installedCount = _detectedClis.Count(c => c.Installed && c.PresetId != "custom-cli");
        PushToast("success", $"✓ 已扫描本机环境：共发现 {installedCount} 款可用轻量级 CLI 智能体引擎");
        return BuildFullStateDto();
    }

    internal TerminalSessionInfo InstallMarketCliInTerminalFromBridge(string presetId, string? workingDirectory)
    {
        var preset = UniversalAgentCatalog.GetCliPreset(presetId);
        var cwd = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory) ? workingDirectory : _projectRoot;
        var session = _terminals.CreateSession(
            $"安装 {preset.Badge}",
            workspace: null,
            workingDirectory: cwd,
            commandKind: "shell",
            cols: 120,
            rows: 28,
            extraEnvironment: null,
            initialInput: preset.InstallCommand);
        BroadcastFullState();
        PushToast("info", $"⬇ 已在内置分屏终端启动安装：{preset.InstallCommand}");
        return session;
    }

    internal void OpenFolderInExplorer(string? workspaceId, string? targetPath)
    {
        string? folder = targetPath;
        if (string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(workspaceId))
        {
            var ws = FindUnifiedWorkspaceById(workspaceId) ?? throw new InvalidOperationException("未找到该工作区。");
            folder = !ws.HasDesktopClient
                ? ws.ElectronUserData
                : (ws.Kind == CodexWorkspaceKind.Managed ? Path.GetDirectoryName(ws.ElectronUserData)! : ws.ElectronUserData);
        }
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new InvalidOperationException("目录不存在，无法在资源管理器中打开。");

        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(Path.GetFullPath(folder));
        Process.Start(start);
    }

    internal async Task RefreshPoolUsageAsync(string? workspaceId, bool all, bool force)
    {
        if (_isClosing) return;
        _detectedClis = UniversalAgentCatalog.DetectInstalledClis(_workspaces.Workspaces);
        var unified = GetUnifiedWorkspaces();
        if (all || string.IsNullOrWhiteSpace(workspaceId))
        {
            await _accountPool.RefreshAllCodexAsync(
                unified,
                force,
                _lifetime.Token,
                onEachUpdated: RequestBroadcastFullState);
        }
        else if (FindUnifiedWorkspaceById(workspaceId) is { } ws)
        {
            if (ws.CanReadUsage)
                await _accountPool.RefreshWorkspaceAsync(ws, force, _lifetime.Token);
            else
                await _accountPool.RefreshAllCodexAsync([ws], force, _lifetime.Token);
        }
        RequestBroadcastFullState();
    }

    private async Task AutoProvisionAntigravityCliIfNeededAsync()
    {
        if (_isClosing || AntigravityAgentRunner.TryFindAgyCliExecutable() is not null)
            return;

        var unified = GetUnifiedWorkspaces();
        var hasAnyAntigravity = unified.Any(w =>
            (w.Client == ClientKind.Antigravity && w.IsLaunchable)
            || (w.Client == ClientKind.MarketCli && AntigravityAgentRunner.IsAntigravityBackedCliRecord(
                _agentProviders.FindById(w.ProviderId ?? w.Id),
                w.AntigravityHome ?? w.ElectronUserData)));
        if (!hasAnyAntigravity)
            return;

        try
        {
            await AntigravityAgentRunner.EnsureAgyCliExecutableAsync(token: _lifetime.Token);
            if (_isClosing) return;
            await RefreshPoolUsageAsync(null, all: true, force: true);
        }
        catch (OperationCanceledException) when (_isClosing)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoProvisionAntigravityCli] {ex.Message}");
        }
    }

    internal async Task RefreshDiscoveryFromBridgeAsync()
    {
        if (_isClosing) return;
        if (_discoveryTask is { IsCompleted: false }) { await _discoveryTask; return; }
        _discoveryTask = Task.WhenAll(_monitors.Values.Select(m => m.RefreshAllAsync(_workspaces)));
        await _discoveryTask;
        if (_isClosing) return;

        var currentWorkspaces = GetUnifiedWorkspaces();
        var sig = string.Join('|', currentWorkspaces.Select(w => w.Id));
        var rosterChanged = !string.Equals(_lastWorkspaceIdsSignature, sig, StringComparison.Ordinal);
        if (rosterChanged)
        {
            _lastWorkspaceIdsSignature = sig;
            _coordinator.SyncRosterWithWorkspaces();
        }

        // Enforce background stealth mode ONLY on workspaces explicitly placed into stealth daemon mode
        // (so pre-existing user IDE windows open before launcher startup are never unexpectedly hidden!)
        if (_enableAutoStealth && _currentSettings.StealthBackgroundMode && !_stealthManagedWorkspaces.IsEmpty)
        {
            var managedInstances = _monitors.Values.SelectMany(m => m.Instances)
                .Where(i => i.ProfileId is not null && _stealthManagedWorkspaces.ContainsKey(i.ProfileId));
            _stealthWindows.EnforceStealthOnInstances(managedInstances);
        }

        if (rosterChanged)
            BroadcastFullState();
        else if (_shellReady && !_isClosing)
            PostEvent("telemetry.tick", BuildTelemetryTickDto());
    }

    internal async Task PrepareManagedSandboxesAsync()
    {
        if (_preparingSandboxes || _isLaunching || _isClosing) return;
        _preparingSandboxes = true;
        BroadcastFullState();
        var changed = 0;
        var errors = new List<string>();
        try
        {
            foreach (var workspace in _workspaces.Workspaces.Where(w => w.Client == ClientKind.Codex && w.Kind == CodexWorkspaceKind.Managed).ToArray())
            {
                try { if ((await WorkspaceSandboxService.PrepareAsync(workspace, _lifetime.Token)).Changed) changed++; }
                catch (OperationCanceledException) when (_isClosing) { return; }
                catch (Exception ex) { errors.Add($"{workspace.DisplayName}：{ex.Message}"); }
            }
            _sandboxStatusText = errors.Count > 0
                ? string.Join("；", errors)
                : changed > 0
                    ? $"已修复 {changed} 个受管账号的多开沙箱配置并保留备份。"
                    : "全部受管 Codex 账号多开沙箱就绪（unelevated 隔离令牌）。";
        }
        finally
        {
            _preparingSandboxes = false;
            if (!_isClosing) BroadcastFullState();
        }
    }

    internal async Task<object> LaunchOfficialClientAsync(string workspaceId, string? workingDirectory, bool stealth = false)
    {
        if (_isLaunching || _preparingSandboxes)
            throw new InvalidOperationException("当前有另一个客户端启动或沙箱检查任务正在执行，请稍候。");

        var workspace = _workspaces.FindById(workspaceId)
            ?? throw new InvalidOperationException("未找到选定的账号工作区。");
        if (!workspace.IsLaunchable)
            throw new InvalidOperationException("该外部工作区未验证配置路径，不支持直接启动。");

        _isLaunching = true;
        BroadcastFullState();
        try
        {
            if (!stealth)
            {
                _stealthManagedWorkspaces.TryRemove(workspaceId, out _);
                _stealthWindows.AllowTemporaryLoginWindow(workspaceId, TimeSpan.FromMinutes(10));
            }
            else
            {
                _stealthManagedWorkspaces[workspaceId] = 1;
                _stealthWindows.RevokeTemporaryLoginWindow(workspaceId);
            }

            await RefreshDiscoveryFromBridgeAsync();
            var monitor = _monitors[workspace.Client];
            if (!monitor.DiscoveryAvailable)
                throw new InvalidOperationException(monitor.LastWarning ?? "无法验证现有实例状态，请先检查官方安装包。");

            if (workspace.Kind == CodexWorkspaceKind.Managed)
            {
                var profile = _loadedProfiles.First(p => p.Id == workspace.Id);
                _profiles.EnsureProfileStorage(profile);
                if (workspace.Client == ClientKind.Codex)
                    await WorkspaceSandboxService.PrepareAsync(workspace, _lifetime.Token);
            }

            var existing = monitor.Instances.FirstOrDefault(i => i.ProfileId == workspace.Id && !i.IsStopped);
            if (!stealth && workspace.Client == ClientKind.Antigravity && workspace.Kind == CodexWorkspaceKind.Managed && existing is null)
            {
                PushToast("info", $"🛡 已为“{workspace.DisplayName}”挂载独立文件级凭据沙箱（.gemini/antigravity），在此窗口登录或切换 Google 账号不会影响原始反重力或其他分身。");
            }
            if (existing is not null)
            {
                if (stealth)
                {
                    _stealthWindows.HideInstanceWindows(existing.ProcessId, workspace.Id, existing.MemberProcessIds);
                    var msgStealth = $"👻 “{workspace.DisplayName}”已在后台静默守护运行中。";
                    PushToast("success", msgStealth);
                    return new { launched = false, stealth = true, pid = existing.ProcessId, message = msgStealth };
                }

                _stealthWindows.RevealInstanceWindows(existing.ProcessId, workspace.Id, existing.MemberProcessIds);
                var focused = monitor.Focus(existing);
                if (!focused && workspace.Client == ClientKind.Antigravity)
                {
                    await Task.Run(() => LaunchAntigravityProcess(workspace.DataPaths, workingDirectory));
                    await RefreshDiscoveryFromBridgeAsync();
                    focused = true;
                }
                var focusMsg = $"👁 已显现并定位“{workspace.DisplayName}”客户端窗口。";
                PushToast("info", focusMsg);
                return new { launched = false, focused, pid = existing.ProcessId, message = focusMsg };
            }

            if (workspace.Client == ClientKind.Codex && workspace.Kind == CodexWorkspaceKind.Managed)
                await WorkspaceRuntimeMarketplaceService.PrepareAsync(workspace, _lifetime.Token);

            var requestedAtUtc = DateTimeOffset.UtcNow;
            uint pid;
            if (workspace.Client == ClientKind.Antigravity && !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                pid = await Task.Run(() => LaunchAntigravityProcess(workspace.DataPaths, workingDirectory));
            }
            else
            {
                pid = await Task.Run(() => _launcher.Launch(workspace.DataPaths));
            }

            var associated = monitor.RegisterLaunched(workspace, pid, requestedAtUtc);
            await RefreshDiscoveryFromBridgeAsync();

            var msg = $"✓ 已正常启动官方客户端“{workspace.DisplayName}”（PID {pid}）。";
            PushToast("success", msg);
            return new { launched = true, associated, stealth = false, pid, message = msg };
        }
        finally
        {
            _isLaunching = false;
            if (!_isClosing) BroadcastFullState();
        }
    }

    private static uint LaunchAntigravityProcess(WorkspaceDataPaths paths, string? folderPath)
    {
        WindowsSecurity.ThrowIfHighIntegrity();
        var executable = AntigravityInstallation.GetExecutablePath();
        var start = AntigravityInstallation.CreateStartInfo(executable, paths);
        if (!string.IsNullOrWhiteSpace(folderPath))
            start.ArgumentList.Add(Path.GetFullPath(folderPath));
        ProfilePathSafety.ThrowIfReparsePath(paths.ElectronUserData);
        if (paths.AntigravityHome is not null)
        {
            AntigravityInstallation.RequireIsolationSupport(executable);
            Directory.CreateDirectory(Path.Combine(paths.AntigravityHome, ".gemini", "antigravity"));
            Directory.CreateDirectory(Path.Combine(paths.AntigravityHome, "AppData", "Roaming"));
            Directory.CreateDirectory(Path.Combine(paths.AntigravityHome, "AppData", "Local"));
            Directory.CreateDirectory(Path.Combine(paths.AntigravityHome, ".config"));
        }
        Directory.CreateDirectory(paths.ElectronUserData);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Antigravity 未能启动。");
        return checked((uint)process.Id);
    }

    internal bool ControlInstance(uint pid, string? startedAtIso, string action)
    {
        var instance = _monitors.Values.SelectMany(m => m.Instances).FirstOrDefault(i =>
            i.ProcessId == pid && (string.IsNullOrWhiteSpace(startedAtIso) || !DateTimeOffset.TryParse(startedAtIso, out var ts) || i.StartedAtUtc == ts));
        if (instance is null) return false;
        var monitor = _monitors.Values.First(m => m.Instances.Contains(instance));
        bool ok;
        switch (action)
        {
            case "focus":
                _stealthWindows.AllowTemporaryLoginWindow(instance.ProfileId ?? string.Empty, TimeSpan.FromMinutes(5));
                _stealthWindows.RevealInstanceWindows(instance.ProcessId, instance.ProfileId, instance.MemberProcessIds);
                ok = monitor.Focus(instance);
                break;
            case "close":
                ok = monitor.Close(instance);
                break;
            case "kill":
                ok = monitor.Kill(instance);
                break;
            default:
                ok = false;
                break;
        }
        BroadcastFullState();
        return ok;
    }

    internal bool TileInstances(string? workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId) && _workspaces.FindById(workspaceId) is { } ws)
        {
            var subset = _monitors[ws.Client].Instances.Where(i => i.ProfileId == workspaceId);
            return _monitors[ws.Client].TileAll(subset);
        }
        return NativeWindowFocus.TryTile(_monitors.Values.SelectMany(m => m.Instances).Where(i => !i.IsStopped).SelectMany(i => i.GetVerifiedTopLevelWindows()));
    }

    internal bool MinimizeInstances(string? workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId) && _workspaces.FindById(workspaceId) is { } ws)
        {
            var subset = _monitors[ws.Client].Instances.Where(i => i.ProfileId == workspaceId);
            return _monitors[ws.Client].MinimizeAll(subset);
        }
        var any = false;
        foreach (var m in _monitors.Values) any = m.MinimizeAll() || any;
        return any;
    }

    internal async Task<object> InspectWorkspaceStorageAsync(string workspaceId)
    {
        var ws = _workspaces.FindById(workspaceId) ?? throw new InvalidOperationException("找不到工作区。");
        if (ws.Kind != CodexWorkspaceKind.Managed)
            return new { workspaceId, text = "原始官方目录" };
        var profile = _loadedProfiles.FirstOrDefault(p => p.Id == workspaceId);
        if (profile is null) return new { workspaceId, text = "不可用" };
        var snapshot = await ProfileStorageInspector.InspectAsync(_profiles.GetPaths(profile), ws.Client, _lifetime.Token);
        var text = RuntimeFormatting.FormatBytes(snapshot.TotalBytes)
            + (snapshot.SkippedEntries > 0 ? $"（跳过 {snapshot.SkippedEntries} 项）" : string.Empty);
        _storageSummaries[workspaceId] = text;
        BroadcastFullState();
        return new { workspaceId, text, snapshot.TotalBytes };
    }

    private async Task RefreshAllStorageSummariesAsync()
    {
        foreach (var profile in _loadedProfiles.ToArray())
        {
            if (_isClosing) return;
            try
            {
                var snapshot = await ProfileStorageInspector.InspectAsync(_profiles.GetPaths(profile), profile.Client, _lifetime.Token);
                _storageSummaries[profile.Id] = RuntimeFormatting.FormatBytes(snapshot.TotalBytes);
            }
            catch { }
        }
        if (!_isClosing) RequestBroadcastFullState();
    }

    internal async Task<object> CreateRoomFromBridgeAsync(string? title, string? directory, bool createWorktree, string? sandboxModeStr, string? dedicatedWorkspaceId = null, string? primaryWorkspaceId = null)
    {
        var baseDir = string.IsNullOrWhiteSpace(directory) ? _projectRoot : Path.GetFullPath(directory);
        if (!Directory.Exists(baseDir)) throw new InvalidOperationException("所选项目目录不存在。");

        string workingDir = baseDir;
        string? repoRoot = null;
        string? wtBranch = null;
        var mode = string.Equals(sandboxModeStr, "ReadOnly", StringComparison.OrdinalIgnoreCase)
            ? ChatSandboxMode.ReadOnly
            : ChatSandboxMode.WorkspaceWrite;

        if (createWorktree)
        {
            var created = await _gitWorktrees.CreateIsolatedWorktreeAsync(baseDir, "room", title ?? "task", null, _lifetime.Token);
            workingDir = created.WorktreePath;
            repoRoot = created.RepositoryRoot;
            wtBranch = created.BranchName;
            mode = ChatSandboxMode.WorkspaceWrite;
        }
        else
        {
            var repo = await _gitWorktrees.InspectRepositoryAsync(baseDir, _lifetime.Token);
            if (repo.IsGitRepository) repoRoot = repo.RepositoryRoot;
        }

        var canonicalProj = NormalizeProjectRootPath(repoRoot ?? baseDir);
        RegisterRecentProject(canonicalProj);
        var room = _coordinator.CreateRoom(workingDir, title, mode, repoRoot, wtBranch, dedicatedWorkspaceId);
        if (!string.IsNullOrWhiteSpace(primaryWorkspaceId))
        {
            try { _coordinator.SetRoomPrimaryWorkspace(room, primaryWorkspaceId); } catch { }
        }
        BroadcastFullState();
        return MapRoomDto(room);
    }

    private static string NormalizeProjectRootPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return string.Empty;
        try
        {
            var full = Path.GetFullPath(rawPath.Trim());
            var root = Path.GetPathRoot(full);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                ? full
                : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return rawPath.Trim();
        }
    }

    private void RegisterRecentProject(string projectPath)
    {
        var norm = NormalizeProjectRootPath(projectPath);
        if (string.IsNullOrWhiteSpace(norm)) return;
        var list = (_currentSettings.RecentProjects ?? []).ToList();
        if (!list.Any(p => string.Equals(NormalizeProjectRootPath(p), norm, StringComparison.OrdinalIgnoreCase)))
        {
            list.Insert(0, norm);
        }
        _currentSettings = _settingsStore.Save(_currentSettings with
        {
            LastRepositoryPath = norm,
            RecentProjects = list,
        });
    }

    private object[] BuildProjectsDto()
    {
        var orderedPaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? candidate)
        {
            var norm = NormalizeProjectRootPath(candidate);
            if (string.IsNullOrWhiteSpace(norm)) return;
            if (seen.Add(norm)) orderedPaths.Add(norm);
        }

        var projectRooms = _coordinator.Rooms.Where(r => !r.IsDedicatedAgentRoom).ToArray();
        foreach (var p in _currentSettings.RecentProjects ?? [])
            AddCandidate(p);
        foreach (var r in projectRooms)
            AddCandidate(r.RepositoryRoot ?? r.WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(_currentSettings.LastRepositoryPath))
            AddCandidate(_currentSettings.LastRepositoryPath);
        if (orderedPaths.Count == 0)
            AddCandidate(_projectRoot);

        return orderedPaths
            .Where(p => Directory.Exists(p) || projectRooms.Any(r => string.Equals(NormalizeProjectRootPath(r.RepositoryRoot ?? r.WorkingDirectory), p, StringComparison.OrdinalIgnoreCase)))
            .Select(p =>
            {
                var name = Path.GetFileName(p);
                if (string.IsNullOrWhiteSpace(name)) name = p;
                var isGit = Directory.Exists(Path.Combine(p, ".git")) || File.Exists(Path.Combine(p, ".git"));
                var count = projectRooms.Count(r => string.Equals(NormalizeProjectRootPath(r.RepositoryRoot ?? r.WorkingDirectory), p, StringComparison.OrdinalIgnoreCase));
                return (object)new
                {
                    Path = p,
                    Name = name,
                    IsGitRepository = isGit,
                    ThreadCount = count,
                };
            })
            .ToArray();
    }

    internal async Task<object> CreateProjectFromBridgeAsync(string parentDirectory, string projectName, bool initGit, string? primaryWorkspaceId)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory))
            throw new InvalidOperationException("请选择存放新项目的父级文件夹。");
        var cleanParent = Path.GetFullPath(parentDirectory.Trim());
        if (!Directory.Exists(cleanParent))
            throw new InvalidOperationException("所选父级文件夹不存在。");
        ProfilePathSafety.ThrowIfReparsePath(cleanParent);

        var cleanName = (projectName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanName) || cleanName.Length > 80
            || cleanName is "." or ".."
            || cleanName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || cleanName.Contains('/') || cleanName.Contains('\\'))
        {
            throw new InvalidOperationException("请输入合法的项目文件夹名称（不含路径斜杠或特殊非法字符）。");
        }

        var targetDir = Path.GetFullPath(Path.Combine(cleanParent, cleanName));
        if (!targetDir.StartsWith(cleanParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("项目路径越界，已拒绝创建。");
        ProfilePathSafety.ThrowIfReparsePath(targetDir);

        Directory.CreateDirectory(targetDir);

        if (initGit && !Directory.Exists(Path.Combine(targetDir, ".git")) && !File.Exists(Path.Combine(targetDir, ".git")))
        {
            try
            {
                var gitExe = GitWorktreeService.FindGitExecutable();
                static async Task RunGitInitStepAsync(string exe, string cwd, CancellationToken ct, params string[] gitArgs)
                {
                    var psi = new ProcessStartInfo(exe)
                    {
                        WorkingDirectory = cwd,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    foreach (var a in gitArgs) psi.ArgumentList.Add(a);
                    using var proc = Process.Start(psi);
                    if (proc is not null) await proc.WaitForExitAsync(ct);
                }

                await RunGitInitStepAsync(gitExe, targetDir, _lifetime.Token, "init", "-b", "main");
                await RunGitInitStepAsync(gitExe, targetDir, _lifetime.Token, "-c", "user.name=Hyperion", "-c", "user.email=hyperion@local", "commit", "--allow-empty", "-m", "chore: initialize project");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CreateProject.gitInit] {ex.Message}");
            }
        }

        var repo = await _gitWorktrees.InspectRepositoryAsync(targetDir, _lifetime.Token);
        var repoRoot = repo.IsGitRepository ? repo.RepositoryRoot : null;
        var canonical = NormalizeProjectRootPath(repoRoot ?? targetDir);
        RegisterRecentProject(canonical);

        var room = _coordinator.CreateRoom(targetDir, "主对话", ChatSandboxMode.WorkspaceWrite, repoRoot, null, null);
        if (!string.IsNullOrWhiteSpace(primaryWorkspaceId))
        {
            try { _coordinator.SetRoomPrimaryWorkspace(room, primaryWorkspaceId); } catch { }
        }

        PushToast("success", $"✓ 已创建项目「{cleanName}」并打开工作台");
        BroadcastFullState();
        return new
        {
            projectPath = canonical,
            roomId = room.Id,
            room = MapRoomDto(room),
            state = BuildFullStateDto(),
        };
    }

    internal async Task<object?> OpenProjectFromBridgeAsync(string? directory, string? primaryWorkspaceId)
    {
        var chosen = directory;
        if (string.IsNullOrWhiteSpace(chosen))
        {
            var initial = !string.IsNullOrWhiteSpace(_currentSettings.LastRepositoryPath) && Directory.Exists(_currentSettings.LastRepositoryPath)
                ? Directory.GetParent(_currentSettings.LastRepositoryPath)?.FullName ?? _currentSettings.LastRepositoryPath
                : _projectRoot;
            chosen = PickFolderDialog("选择要打开的项目文件夹", initial);
            if (string.IsNullOrWhiteSpace(chosen)) return null;
        }

        var fullDir = Path.GetFullPath(chosen.Trim());
        if (!Directory.Exists(fullDir))
            throw new InvalidOperationException("所选项目文件夹不存在。");
        ProfilePathSafety.ThrowIfReparsePath(fullDir);

        var repo = await _gitWorktrees.InspectRepositoryAsync(fullDir, _lifetime.Token);
        var repoRoot = repo.IsGitRepository ? repo.RepositoryRoot : null;
        var canonical = NormalizeProjectRootPath(repoRoot ?? fullDir);
        RegisterRecentProject(canonical);

        var existingRoom = _coordinator.Rooms.FirstOrDefault(r =>
            !r.IsDedicatedAgentRoom &&
            string.Equals(NormalizeProjectRootPath(r.RepositoryRoot ?? r.WorkingDirectory), canonical, StringComparison.OrdinalIgnoreCase));

        if (existingRoom is null)
        {
            existingRoom = _coordinator.CreateRoom(canonical, "主对话", ChatSandboxMode.WorkspaceWrite, repoRoot, null, null);
            if (!string.IsNullOrWhiteSpace(primaryWorkspaceId))
            {
                try { _coordinator.SetRoomPrimaryWorkspace(existingRoom, primaryWorkspaceId); } catch { }
            }
        }

        PushToast("success", $"✓ 已打开项目「{Path.GetFileName(canonical)}」");
        BroadcastFullState();
        return new
        {
            projectPath = canonical,
            roomId = existingRoom.Id,
            room = MapRoomDto(existingRoom),
            state = BuildFullStateDto(),
        };
    }

    internal async Task<object> RemoveProjectFromBridgeAsync(string directory, bool removeThreads)
    {
        var norm = NormalizeProjectRootPath(directory);
        var nextProjects = (_currentSettings.RecentProjects ?? [])
            .Where(p => !string.Equals(NormalizeProjectRootPath(p), norm, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _currentSettings = _settingsStore.Save(_currentSettings with { RecentProjects = nextProjects });

        if (removeThreads)
        {
            var matching = _coordinator.Rooms
                .Where(r => !r.IsRunning && string.Equals(NormalizeProjectRootPath(r.RepositoryRoot ?? r.WorkingDirectory), norm, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var r in matching)
            {
                if (_coordinator.Rooms.Count(x => !x.IsDedicatedAgentRoom) <= 1 && !r.IsDedicatedAgentRoom)
                    break;
                try { await DeleteRoomFromBridgeAsync(r.Id, removeWorktree: r.IsIsolatedWorktree); } catch { }
            }
        }

        BroadcastFullState();
        PushToast("info", "已从侧栏列表移除该项目（本地硬盘文件保持完好）");
        return BuildFullStateDto();
    }

    internal object SetRoomPrimaryWorkspaceFromBridge(string roomId, string? workspaceId)
    {
        var room = RequireRoomById(roomId);
        _coordinator.SetRoomPrimaryWorkspace(room, workspaceId);
        BroadcastFullState();
        return MapRoomDto(room);
    }

    internal async Task<WorkspaceModelCatalog> GetWorkspaceModelCatalogFromBridgeAsync(string workspaceId, bool forceRefresh, bool wakeDaemon)
    {
        var ws = FindUnifiedWorkspaceById(workspaceId) ?? throw new InvalidOperationException("找不到指定的智能体账号。");
        return await _modelCatalog.GetCatalogAsync(ws, forceRefresh, wakeDaemon, _lifetime.Token);
    }

    internal async Task<WorkspaceModelCatalog> ProbeDraftApiModelsFromBridgeAsync(UniversalAgentUpsertRequest draft)
    {
        UniversalAgentRecord tempRecord;
        if (!string.IsNullOrWhiteSpace(draft.Id) && string.IsNullOrWhiteSpace(draft.ApiKeyPlaintext))
        {
            var existing = _agentProviders.FindById(draft.Id)
                ?? throw new InvalidOperationException("找不到该智能体配置。");
            tempRecord = existing with
            {
                Protocol = draft.Protocol,
                BaseUrl = string.IsNullOrWhiteSpace(draft.BaseUrl) ? existing.BaseUrl : draft.BaseUrl.Trim().TrimEnd('/'),
                Model = string.IsNullOrWhiteSpace(draft.Model) ? existing.Model : draft.Model.Trim(),
            };
        }
        else
        {
            var preset = UniversalAgentCatalog.GetApiPreset(draft.PresetId);
            tempRecord = new UniversalAgentRecord(
                Id: "draft-probe",
                DisplayName: draft.DisplayName,
                AgentKind: UniversalAgentKind.DirectApi,
                PresetId: draft.PresetId,
                Protocol: draft.Protocol,
                BaseUrl: (string.IsNullOrWhiteSpace(draft.BaseUrl) ? preset.DefaultBaseUrl : draft.BaseUrl).Trim().TrimEnd('/'),
                Model: (string.IsNullOrWhiteSpace(draft.Model) ? preset.DefaultModel : draft.Model).Trim(),
                EncryptedApiKey: DpapiSecretProtector.Protect(draft.ApiKeyPlaintext),
                MaskedApiKey: DpapiSecretProtector.Mask(draft.ApiKeyPlaintext),
                CustomCliExecutable: null,
                CustomCliArgsTemplate: null,
                TokenBudgetLimit: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }
        return await _modelCatalog.ProbeDirectApiDraftAsync(tempRecord, _lifetime.Token);
    }

    internal object SetRoomAgentModelOptionsFromBridge(
        string roomId,
        string? agentId,
        string? workspaceId,
        string? model,
        string? reasoningEffort,
        string? serviceTier)
    {
        var room = RequireRoomById(roomId);
        _coordinator.UpdateAgentModelOptions(room, agentId, workspaceId, model, reasoningEffort, serviceTier);
        BroadcastFullState();
        return MapRoomDto(room);
    }

    internal async Task<object> EnsureDedicatedAgentRoomAsync(string workspaceId, string? preferredDirectory)
    {
        var existing = _coordinator.Rooms.FirstOrDefault(r => string.Equals(r.DedicatedWorkspaceId, workspaceId, StringComparison.Ordinal));
        if (existing is not null)
            return MapRoomDto(existing);

        var sharedRoom = _coordinator.Rooms.FirstOrDefault(r => !r.IsDedicatedAgentRoom);
        var defaultDir = !string.IsNullOrWhiteSpace(preferredDirectory) && Directory.Exists(preferredDirectory)
            ? preferredDirectory
            : (sharedRoom is not null && Directory.Exists(sharedRoom.WorkingDirectory)
                ? sharedRoom.WorkingDirectory
                : (!string.IsNullOrWhiteSpace(_currentSettings.LastRepositoryPath) && Directory.Exists(_currentSettings.LastRepositoryPath)
                    ? _currentSettings.LastRepositoryPath
                    : _projectRoot));

        var repo = await _gitWorktrees.InspectRepositoryAsync(defaultDir, _lifetime.Token);
        string? wtBranch = sharedRoom is not null && string.Equals(sharedRoom.WorkingDirectory, defaultDir, StringComparison.OrdinalIgnoreCase)
            ? sharedRoom.WorktreeBranch
            : null;
        var room = _coordinator.CreateRoom(
            defaultDir,
            title: null,
            sandboxMode: ChatSandboxMode.WorkspaceWrite,
            repositoryRoot: repo.IsGitRepository ? repo.RepositoryRoot : null,
            worktreeBranch: wtBranch,
            dedicatedWorkspaceId: workspaceId);
        BroadcastFullState();
        return MapRoomDto(room);
    }

    internal object ConfigureRoomFromBridge(string roomId, string title, string directory, string? sandboxModeStr, IReadOnlyList<GroupChatAgent> agents)
    {
        var room = RequireRoomById(roomId);
        ChatSandboxMode? mode = sandboxModeStr is null
            ? null
            : (string.Equals(sandboxModeStr, "WorkspaceWrite", StringComparison.OrdinalIgnoreCase) ? ChatSandboxMode.WorkspaceWrite : ChatSandboxMode.ReadOnly);
        var dirChanged = !string.Equals(NormalizeProjectRootPath(directory), NormalizeProjectRootPath(room.WorkingDirectory), StringComparison.OrdinalIgnoreCase);
        string? nextRepoRoot = null;
        string? nextBranch = null;
        if (dirChanged)
        {
            var full = Path.GetFullPath(directory);
            var isGit = Directory.Exists(Path.Combine(full, ".git")) || File.Exists(Path.Combine(full, ".git"));
            nextRepoRoot = isGit ? full : string.Empty;
            nextBranch = string.Empty;
            RegisterRecentProject(full);
        }
        _coordinator.Configure(room, title, directory, agents, mode, nextRepoRoot, nextBranch);
        BroadcastFullState();
        return MapRoomDto(room);
    }

    internal object SetRoomSandboxModeFromBridge(string roomId, string sandboxModeStr)
    {
        var room = RequireRoomById(roomId);
        var mode = string.Equals(sandboxModeStr, "ReadOnly", StringComparison.OrdinalIgnoreCase)
            ? ChatSandboxMode.ReadOnly
            : ChatSandboxMode.WorkspaceWrite;
        _coordinator.SetRoomSandboxMode(room, mode);
        PushToast("info", mode == ChatSandboxMode.WorkspaceWrite ? "🛠 已切换为全功能读写执行模式（Agent 可直接改代码与跑命令）" : "🔍 已切换为只读分析模式");
        BroadcastFullState();
        return MapRoomDto(room);
    }

    internal object SyncAllRoomRostersFromBridge()
    {
        _coordinator.SyncRosterWithWorkspaces(forceReEnrollAll: true);
        BroadcastFullState();
        PushToast("success", "✓ 已将所有可用隔离账号同步加入当前会话 @ 名单");
        return BuildFullStateDto();
    }

    internal async Task DeleteRoomFromBridgeAsync(string roomId, bool removeWorktree)
    {
        var room = RequireRoomById(roomId);
        var repoRoot = room.RepositoryRoot;
        var wtDir = room.WorkingDirectory;
        var isWt = room.IsIsolatedWorktree;
        _coordinator.DeleteRoom(room);
        if (removeWorktree && isWt && !string.IsNullOrWhiteSpace(repoRoot))
        {
            try { await _gitWorktrees.RemoveWorktreeAsync(repoRoot, wtDir, force: true, _lifetime.Token); }
            catch (Exception ex) { PushToast("warning", $"任务已删除，但清理 Worktree 目录时提示：{ex.Message}"); }
        }
    }

    internal object SwitchRoomAgentWorkspace(string roomId, string agentId, string workspaceId)
    {
        var room = RequireRoomById(roomId);
        _coordinator.SwitchAgentWorkspace(room, agentId, workspaceId);
        var ws = FindUnifiedWorkspaceById(workspaceId);
        PushToast("success", $"已切换至“{ws?.DisplayName ?? workspaceId}”，后续回复将使用该独立身份。");
        BroadcastFullState();
        return MapRoomDto(room);
    }

    internal async Task<object> SpawnWorktreeForRoomAsync(string roomId, string? labelHint)
    {
        var room = RequireRoomById(roomId);
        if (room.IsRunning) throw new InvalidOperationException("请等待当前回复完成后再派生独立 Git Worktree。");
        var sourceDir = room.RepositoryRoot ?? room.WorkingDirectory;
        var wt = await _gitWorktrees.CreateIsolatedWorktreeAsync(sourceDir, $"room-{room.Id[..8]}", labelHint ?? room.Title, null, _lifetime.Token);
        _coordinator.Configure(room, room.Title, wt.WorktreePath, room.Agents, ChatSandboxMode.WorkspaceWrite, wt.RepositoryRoot, wt.BranchName);
        PushToast("success", $"已派生独立 Git Worktree 分支 {wt.BranchName} 并启用可写模式。");
        BroadcastFullState();
        return MapRoomDto(room);
    }

    internal async Task<object> MergeRoomWorktreeAsync(string roomId)
    {
        var room = RequireRoomById(roomId);
        if (!room.IsIsolatedWorktree || string.IsNullOrWhiteSpace(room.RepositoryRoot) || string.IsNullOrWhiteSpace(room.WorktreeBranch))
            throw new InvalidOperationException("当前会话未绑定独立 Git Worktree 分支。");
        if (room.IsRunning) throw new InvalidOperationException("请等待当前回复结束后再合并分支。");

        var git = GitWorktreeService.FindGitExecutable();
        await RunGitCommitIfDirtyAsync(git, room.WorkingDirectory, $"ADE Task: {room.Title}");
        var summary = await _gitWorktrees.MergeBranchIntoRepositoryAsync(room.RepositoryRoot, room.WorktreeBranch, _lifetime.Token);
        PushToast("success", summary);
        return new { summary };
    }

    private static async Task RunGitCommitIfDirtyAsync(string gitExe, string worktreeDir, string commitMessage)
    {
        static async Task<(int Code, string Out)> ExecAsync(string exe, string cwd, params string[] args)
        {
            var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var s = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, s);
        }

        await ExecAsync(gitExe, worktreeDir, "add", "-A");
        var st = await ExecAsync(gitExe, worktreeDir, "status", "--porcelain=v1");
        if (!string.IsNullOrWhiteSpace(st.Out))
            await ExecAsync(gitExe, worktreeDir, "-c", "user.name=Hyperion", "-c", "user.email=hyperion@local", "commit", "-m", commitMessage);
    }

    internal void StartRoomSend(string roomId, string text, IReadOnlyList<ChatImageAttachment>? attachments = null)
    {
        var room = RequireRoomById(roomId);
        Track(_coordinator.SendAsync(room, text, attachments));
    }

    internal void StopRoom(string roomId)
    {
        var room = RequireRoomById(roomId);
        _coordinator.Stop(room);
    }

    internal TerminalSessionInfo CreateTerminalSession(string? title, string? workspaceId, string cwd, string commandKind, short cols, short rows)
    {
        var ws = !string.IsNullOrWhiteSpace(workspaceId) ? FindUnifiedWorkspaceById(workspaceId) : null;
        IReadOnlyDictionary<string, string>? extraEnv = null;
        string? initialInput = null;
        var actualKind = commandKind;

        if (ws is { HasDesktopClient: false } && _agentProviders.FindById(ws.ProviderId ?? ws.Id) is { } rec)
        {
            extraEnv = _agentProviders.CreateIsolatedEnvironment(rec);
            if (string.Equals(commandKind, "cli-login", StringComparison.OrdinalIgnoreCase))
            {
                actualKind = "shell";
                var resolvedHome = _agentProviders.ResolveHomeDirectory(rec);
                var effPresetId = AntigravityAgentRunner.IsAntigravityBackedCliRecord(rec, resolvedHome) ? "antigravity-cli" : rec.PresetId;
                var preset = UniversalAgentCatalog.GetCliPreset(effPresetId);
                var resolvedBin = UniversalAgentCatalog.TryFindCliBinary(preset.Id, preset.BinaryName, _workspaces.Workspaces);
                var cmdInvoke = !string.IsNullOrWhiteSpace(resolvedBin) ? $"& \"{resolvedBin}\"" : preset.BinaryName;
                initialInput = preset.Id switch
                {
                    "antigravity-cli" => $"{cmdInvoke} acp --gemini-dir \"{Path.Combine(resolvedHome, ".gemini")}\" --app-data-dir antigravity --login",
                    "claude-code" => $"{cmdInvoke} auth login",
                    "codex-cli" => $"{cmdInvoke} login",
                    "opencode" => $"{cmdInvoke} auth login",
                    _ => cmdInvoke,
                };
            }
        }
        else if (ws is { Client: ClientKind.Antigravity } && string.Equals(commandKind, "cli-login", StringComparison.OrdinalIgnoreCase))
        {
            actualKind = "shell";
            var agyHome = AntigravityAgentRunner.GetHomeForWorkspace(ws);
            extraEnv = AntigravityAgentRunner.CreateEnvironmentForHome(agyHome);
            var agyBin = AntigravityAgentRunner.TryFindAgyCliExecutable();
            var loginFlags = ws.Kind == CodexWorkspaceKind.Managed
                ? $"acp --gemini-dir \"{Path.Combine(agyHome, ".gemini")}\" --app-data-dir antigravity --login"
                : "acp --login";
            initialInput = !string.IsNullOrWhiteSpace(agyBin) ? $"& \"{agyBin}\" {loginFlags}" : $"agy {loginFlags}";
        }
        var info = _terminals.CreateSession(title, ws, cwd, actualKind, cols, rows, extraEnv, initialInput);
        BroadcastFullState();
        return info;
    }

    internal async Task<object> SetDesignPreviewVisibleAsync(bool visible, string? url)
    {
        _designVisible = visible;
        if (!string.IsNullOrWhiteSpace(url)) _designUrl = NormalizeUrl(url);

        if (!visible)
        {
            _designInspecting = false;
            InspectActiveDot.Visibility = Visibility.Collapsed;
            PreviewSplitter.Visibility = Visibility.Collapsed;
            PreviewContainer.Visibility = Visibility.Collapsed;
            PreviewSplitterColumn.Width = new GridLength(0);
            PreviewPaneColumn.Width = new GridLength(0);
        }
        else
        {
            PreviewSplitterColumn.Width = new GridLength(5);
            if (PreviewPaneColumn.Width.Value < 320)
                PreviewPaneColumn.Width = new GridLength(480);
            PreviewSplitter.Visibility = Visibility.Visible;
            PreviewContainer.Visibility = Visibility.Visible;
            await EnsurePreviewWebViewInitializedAsync();
            PreviewWebView.CoreWebView2.Navigate(_designUrl);
        }

        PostDesignState();
        return new { visible = _designVisible, url = _designUrl, inspecting = _designInspecting };
    }

    internal async Task<object> NavigateDesignPreviewAsync(string url)
    {
        _designUrl = NormalizeUrl(url);
        _currentSettings = _settingsStore.Save(_currentSettings with { LastPreviewUrl = _designUrl });
        if (!_designVisible)
            return await SetDesignPreviewVisibleAsync(true, _designUrl);
        await EnsurePreviewWebViewInitializedAsync();
        PreviewWebView.CoreWebView2.Navigate(_designUrl);
        PostDesignState();
        return new { visible = _designVisible, url = _designUrl, inspecting = _designInspecting };
    }

    internal async Task<object> SetDesignInspectModeAsync(bool enabled)
    {
        if (!_designVisible) await SetDesignPreviewVisibleAsync(true, _designUrl);
        await EnsurePreviewWebViewInitializedAsync();
        _designInspecting = enabled;
        InspectActiveDot.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        PreviewTitleText.Text = enabled ? "点选模式已开启：在下方网页点击任意元素自动抓取 DOM/CSS/截图" : $"Design Mode · {_designUrl}";
        await PreviewWebView.CoreWebView2.ExecuteScriptAsync($"window.__adeSetInspectMode && window.__adeSetInspectMode({(enabled ? "true" : "false")});");
        PostDesignState();
        return new { inspecting = _designInspecting };
    }

    internal async Task<object> CaptureDesignViewportAsync()
    {
        if (!_designVisible) await SetDesignPreviewVisibleAsync(true, _designUrl);
        await EnsurePreviewWebViewInitializedAsync();
        var dataUrl = await CapturePreviewAsDataUrlAsync();
        var payload = new
        {
            url = PreviewWebView.CoreWebView2.Source,
            selector = "viewport",
            tagName = "page",
            textPreview = PreviewWebView.CoreWebView2.DocumentTitle,
            outerHtml = $"<!-- Viewport capture of {PreviewWebView.CoreWebView2.Source} -->",
            computedStyles = new Dictionary<string, string>(),
            screenshotDataUrl = dataUrl,
        };
        PostEvent("design.elementCaptured", payload);
        return payload;
    }

    private async Task EnsurePreviewWebViewInitializedAsync()
    {
        if (_previewReady) return;
        PreviewWebView.AllowExternalDrop = false;
        _previewEnvironment ??= await WebUiResourceServer.CreatePreviewEnvironmentAsync(_projectRoot);
        await PreviewWebView.EnsureCoreWebView2Async(_previewEnvironment);
        var core = PreviewWebView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.NavigationStarting += (_, navArgs) =>
        {
            if (string.IsNullOrWhiteSpace(navArgs.Uri) ||
                string.Equals(navArgs.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (!Uri.TryCreate(navArgs.Uri, UriKind.Absolute, out var targetUri) ||
                (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
            {
                navArgs.Cancel = true;
            }
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(DesignModeInspector.BootstrapScript);
        core.NavigationCompleted += (_, _) =>
        {
            _designUrl = core.Source;
            PreviewTitleText.Text = $"Design Mode · {core.DocumentTitle} ({core.Source})";
            PostDesignState();
        };
        core.WebMessageReceived += async (_, args) =>
        {
            try
            {
                var raw = args.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(raw)) return;
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "elementPicked")
                {
                    _designInspecting = false;
                    InspectActiveDot.Visibility = Visibility.Collapsed;
                    var screenshot = await CapturePreviewAsDataUrlAsync();
                    var picked = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw);
                    if (picked is not null)
                    {
                        picked["screenshotDataUrl"] = screenshot;
                        PostEvent("design.elementCaptured", picked);
                        PostDesignState();
                        PushToast("success", "已抓取页面元素 DOM、计算样式与实时截图，已附加至输入框！");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PreviewWebView.WebMessageReceived] {ex.Message}");
            }
        };
        _previewReady = true;
    }

    private async Task<string?> CapturePreviewAsDataUrlAsync()
    {
        try
        {
            using var ms = new MemoryStream();
            await PreviewWebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CapturePreviewAsDataUrlAsync] {ex.Message}");
            return null;
        }
    }

    private void PostDesignState() =>
        PostEvent("design.state", new { visible = _designVisible, url = _designUrl, inspecting = _designInspecting });

    private async void ClosePreviewPane_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SetDesignPreviewVisibleAsync(false, null);
        }
        catch (Exception ex)
        {
            PushToast("error", ex.Message);
        }
    }

    private static string NormalizeUrl(string raw)
    {
        var trimmed = raw.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new InvalidOperationException("请输入有效的网页预览地址（如 http://localhost:3000）。");
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "http://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("仅支持 http:// 或 https:// 协议的网页预览地址。");
        }
        return uri.AbsoluteUri;
    }

    private GroupChatRoom RequireRoomById(string roomId) =>
        _coordinator.Rooms.FirstOrDefault(r => string.Equals(r.Id, roomId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException("找不到指定的任务会话。");

    private async void Track(Task task)
    {
        _backgroundTasks[task] = 0;
        try { await task; }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception ex) { if (!_isClosing) PushToast("error", ex.Message); }
        finally { _backgroundTasks.TryRemove(task, out _); }
    }

    private async void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_isClosing) return;
        if (_isLaunching)
        {
            PushToast("warning", "正在完成客户端包激活与临时环境清理，请稍后关闭窗口。");
            return;
        }

        _isClosing = true;
        _refreshTimer.Stop();
        _streamFlushTimer.Stop();
        _broadcastDebounceTimer.Stop();
        _lifetime.Cancel();

        try
        {
            _terminals.Dispose();
            await _coordinator.ShutdownAsync();
            await _arena.ShutdownAsync();
            await Task.WhenAll(_backgroundTasks.Keys.ToArray()).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Expected when _lifetime.Cancel() aborts background refresh/telemetry tasks during window close.
        }
        catch (Exception ex)
        {
            MessageBox.Show($"关闭时清理提示：{ex.Message}", "Hyperion");
        }
        finally
        {
            foreach (var monitor in _monitors.Values) monitor.Dispose();
            _accountPool.Dispose();
            _lifetime.Dispose();
            _closed = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
