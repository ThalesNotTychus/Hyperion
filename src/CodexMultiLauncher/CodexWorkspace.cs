namespace CodexMultiLauncher;

public enum CodexWorkspaceKind { Managed, Original, ExternalUnresolved }

/// <summary>
/// Unified workspace / agent identity used across the ADE Command Center, Arena, Terminal, and Quota Pool.
/// Supports desktop isolated profiles (Codex, Claude, Antigravity) as well as zero-download Direct API
/// and lightweight Market CLI agents.
/// </summary>
public sealed record CodexWorkspace(
    string Id,
    string DisplayName,
    CodexWorkspaceKind Kind,
    string ElectronUserData,
    string? CodexHome,
    string? RuntimeUserHome = null,
    ClientKind Client = ClientKind.Codex,
    string? ClaudeHome = null,
    string? AntigravityHome = null,
    string? SubtitleOverride = null,
    string? ProviderId = null,
    string? DefaultModel = null)
{
    public string ClientName => Client.DisplayName();
    public bool HasDesktopClient => Client is ClientKind.Codex or ClientKind.Claude or ClientKind.Antigravity;
    public bool IsLaunchable => (Client is ClientKind.Claude or ClientKind.Antigravity or ClientKind.DirectApi or ClientKind.MarketCli || CodexHome is not null)
        && (Kind is CodexWorkspaceKind.Managed or CodexWorkspaceKind.Original);
    public bool CanReadUsage => Client == ClientKind.Codex && CodexHome is not null && (Kind is CodexWorkspaceKind.Managed or CodexWorkspaceKind.Original);
    public string Subtitle => !string.IsNullOrWhiteSpace(SubtitleOverride)
        ? SubtitleOverride!
        : ClientName + " · " + (Kind switch
        {
            CodexWorkspaceKind.Managed => "隔离数据目录",
            CodexWorkspaceKind.Original => "原始官方数据目录",
            _ => "外部客户端 · 数据路径未验证",
        });

    public WorkspaceDataPaths DataPaths => new(ElectronUserData, CodexHome,
        Kind == CodexWorkspaceKind.Managed ? RuntimeUserHome : null, Client,
        Kind == CodexWorkspaceKind.Managed ? ClaudeHome : null,
        Kind == CodexWorkspaceKind.Managed ? AntigravityHome : null);
}

public sealed record WorkspaceDataPaths(string ElectronUserData, string? CodexHome, string? RuntimeUserHome = null,
    ClientKind Client = ClientKind.Codex, string? ClaudeHome = null, string? AntigravityHome = null)
{
    public string RequireCodexHome() => CodexHome ?? throw new InvalidOperationException("此工作区的 CODEX_HOME 未验证，不能启动或查询用量。");
}
