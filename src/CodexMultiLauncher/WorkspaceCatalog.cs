namespace CodexMultiLauncher;

/// <summary>Builds the one-to-one path ownership map. Managed paths always win.</summary>
public sealed class WorkspaceCatalog
{
    private readonly string _projectRoot;
    private readonly DiscoveredWorkspaceStore _discoveredStore;
    private readonly Dictionary<string, CodexWorkspace> _byElectron = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CodexWorkspace> _byId = new(StringComparer.Ordinal);
    private IReadOnlyList<CodexWorkspace> _workspaces = [];

    public WorkspaceCatalog(string projectRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _discoveredStore = new DiscoveredWorkspaceStore(_projectRoot);
    }

    public IReadOnlyList<CodexWorkspace> Workspaces => _workspaces;
    public string? LastWarning { get; private set; }

    public void Reload(IReadOnlyCollection<Profile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _byElectron.Clear(); _byId.Clear();
        var list = new List<CodexWorkspace>();
        foreach (var profile in profiles)
        {
            var paths = new ProfilePaths(_projectRoot, profile.Id);
            AddOwned(new CodexWorkspace(profile.Id, profile.DisplayName, CodexWorkspaceKind.Managed,
                paths.ElectronUserData, profile.Client == ClientKind.Codex ? paths.CodexHome : null,
                profile.Client == ClientKind.Codex ? paths.RuntimeUserHome : null, profile.Client,
                profile.Client == ClientKind.Claude ? paths.ClaudeHome : null,
                profile.Client == ClientKind.Antigravity ? paths.AntigravityHome : null), list);
        }
        if (_discoveredStore.HasOriginalWorkspace())
        {
            var original = OriginalWorkspacePaths.GetCurrentUserDefaultPair();
            if (!_byElectron.ContainsKey(original.ElectronUserData))
                AddOwned(new CodexWorkspace("original", "原始 Codex", CodexWorkspaceKind.Original, original.ElectronUserData, original.CodexHome), list);
        }
        var claudeOriginal = ClaudeInstallation.GetOriginalDataDirectories().FirstOrDefault(Directory.Exists);
        if (claudeOriginal is not null && !_byElectron.ContainsKey(claudeOriginal))
            AddOwned(new CodexWorkspace("original-claude:" + claudeOriginal, "原始 Claude", CodexWorkspaceKind.Original,
                claudeOriginal, null, Client: ClientKind.Claude), list);
        var antigravityOriginal = AntigravityInstallation.GetOriginalDataDirectory();
        if (Directory.Exists(antigravityOriginal) && !_byElectron.ContainsKey(antigravityOriginal))
            AddOwned(new CodexWorkspace("original-antigravity", "原始 Antigravity", CodexWorkspaceKind.Original,
                antigravityOriginal, null, Client: ClientKind.Antigravity), list);
        _workspaces = list;
        LastWarning = _discoveredStore.LastWarning;
    }

    public CodexWorkspace? FindById(string? id) => id is not null && _byId.TryGetValue(id, out var workspace) ? workspace : null;

    public CodexWorkspace ResolveObservedElectronDirectory(string canonicalElectronDirectory, ClientKind client = ClientKind.Codex)
    {
        if (!PathIdentity.TryCanonicalize(canonicalElectronDirectory, out canonicalElectronDirectory))
            throw new InvalidOperationException("观察到的 Electron 数据目录无效。");
        if (_byElectron.TryGetValue(canonicalElectronDirectory, out var managedOrPersisted))
        {
            if (managedOrPersisted.Client != client) throw new InvalidOperationException("客户端与数据目录的归属不一致，已拒绝认领。");
            return managedOrPersisted;
        }
        if (client is ClientKind.Claude or ClientKind.Antigravity)
        {
            var isOriginal = client == ClientKind.Claude
                ? ClaudeInstallation.GetOriginalDataDirectories().Any(path => PathIdentity.Equals(path, canonicalElectronDirectory))
                : PathIdentity.Equals(AntigravityInstallation.GetOriginalDataDirectory(), canonicalElectronDirectory);
            var idPrefix = client == ClientKind.Claude ? "claude:" : "antigravity:";
            var observedId = client == ClientKind.Antigravity && isOriginal ? "original-antigravity"
                : (isOriginal ? "original-" : "external-") + idPrefix + canonicalElectronDirectory;
            var external = new CodexWorkspace(observedId,
                (isOriginal ? "原始 " : "外部 ") + client.DisplayName(), isOriginal ? CodexWorkspaceKind.Original : CodexWorkspaceKind.ExternalUnresolved,
                canonicalElectronDirectory, null, Client: client);
            _byElectron.Add(canonicalElectronDirectory, external); _byId.Add(external.Id, external);
            _workspaces = _workspaces.Append(external).ToArray();
            return external;
        }
        var original = OriginalWorkspacePaths.GetCurrentUserDefaultPair();
        if (PathIdentity.Equals(canonicalElectronDirectory, original.ElectronUserData))
        {
            try { _discoveredStore.SaveOriginalWorkspace(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { LastWarning = $"原始工作区未能保存，当前实例仍可使用：{exception.Message}"; }
            var workspace = new CodexWorkspace("original", "原始 Codex", CodexWorkspaceKind.Original, original.ElectronUserData, original.CodexHome);
            _byElectron[workspace.ElectronUserData] = workspace; _byId[workspace.Id] = workspace;
            _workspaces = _workspaces.Append(workspace).ToArray();
            return workspace;
        }
        var id = $"external:{canonicalElectronDirectory}";
        if (_byId.TryGetValue(id, out var existing)) return existing;
        var unresolved = new CodexWorkspace(id, "外部 Codex", CodexWorkspaceKind.ExternalUnresolved, canonicalElectronDirectory, null);
        _byElectron[unresolved.ElectronUserData] = unresolved; _byId[unresolved.Id] = unresolved;
        _workspaces = _workspaces.Append(unresolved).ToArray();
        return unresolved;
    }

    private void AddOwned(CodexWorkspace workspace, List<CodexWorkspace> list)
    {
        if (!PathIdentity.TryCanonicalize(workspace.ElectronUserData, out var electron))
            throw new InvalidOperationException("工作区数据路径无效。");
        string? home = null;
        if (workspace.CodexHome is not null && !PathIdentity.TryCanonicalize(workspace.CodexHome, out home))
            throw new InvalidOperationException("工作区 CODEX_HOME 路径无效。");
        string? claudeHome = null;
        if (workspace.ClaudeHome is not null && !PathIdentity.TryCanonicalize(workspace.ClaudeHome, out claudeHome))
            throw new InvalidOperationException("工作区 Claude 配置路径无效。");
        string? antigravityHome = null;
        if (workspace.AntigravityHome is not null && !PathIdentity.TryCanonicalize(workspace.AntigravityHome, out antigravityHome))
            throw new InvalidOperationException("工作区 Antigravity 主目录无效。");
        var canonical = workspace with { ElectronUserData = electron, CodexHome = home, ClaudeHome = claudeHome, AntigravityHome = antigravityHome };
        var ownedPaths = OwnedPaths(canonical).ToArray();
        if (list.SelectMany(OwnedPaths).Any(existing => ownedPaths.Any(path => PathIdentity.Equals(existing, path))))
            throw new InvalidOperationException("两个工作区不能拥有同一个客户端数据目录。");
        _byElectron.Add(electron, canonical); _byId.Add(canonical.Id, canonical); list.Add(canonical);
    }

    private static IEnumerable<string> OwnedPaths(CodexWorkspace workspace)
    {
        yield return workspace.ElectronUserData;
        if (workspace.CodexHome is not null) yield return workspace.CodexHome;
        if (workspace.ClaudeHome is not null) yield return workspace.ClaudeHome;
        if (workspace.AntigravityHome is not null) yield return workspace.AntigravityHome;
    }
}
