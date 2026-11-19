namespace CodexMultiLauncher;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ClientKind>))]
public enum ClientKind { Codex, Claude, Antigravity, DirectApi, MarketCli }

public static class ClientKindNames
{
    public static string DisplayName(this ClientKind client) => client switch
    {
        ClientKind.Codex => "Codex",
        ClientKind.Claude => "Claude",
        ClientKind.Antigravity => "Antigravity（反重力）",
        ClientKind.DirectApi => "免下载直连 API",
        ClientKind.MarketCli => "轻量级 CLI 引擎",
        _ => throw new InvalidOperationException("客户端类型无效。"),
    };
}

public sealed record Profile(string Id, string DisplayName, ClientKind Client = ClientKind.Codex);
