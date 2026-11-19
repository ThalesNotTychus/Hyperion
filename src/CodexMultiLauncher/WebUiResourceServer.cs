using System.Reflection;
using Microsoft.Web.WebView2.Core;

namespace CodexMultiLauncher;

/// <summary>
/// Serves embedded WebUi/* assets over a virtual HTTPS origin (https://app.codexmultilauncher.local/)
/// so single-file portable builds require zero loose frontend files or localhost ports.
/// </summary>
internal static class WebUiResourceServer
{
    public const string Origin = "https://app.codexmultilauncher.local";
    private static readonly Assembly CurrentAssembly = typeof(WebUiResourceServer).Assembly;
    private static readonly Dictionary<string, string> ResourceLookup = BuildResourceLookup();

    public static async Task<CoreWebView2Environment> CreateEnvironmentAsync(string projectRoot)
    {
        var userDataFolder = ProfilePathSafety.GetContainedFullPath(projectRoot, "data", "launcher-webview");
        ProfilePathSafety.ThrowIfReparsePath(userDataFolder);
        Directory.CreateDirectory(userDataFolder);
        var options = new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection");
        return await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: userDataFolder, options: options);
    }

    public static void Attach(CoreWebView2 webView)
    {
        webView.AddWebResourceRequestedFilter($"{Origin}/*", CoreWebView2WebResourceContext.All);
        webView.WebResourceRequested += OnWebResourceRequested;
    }

    private static void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 webView) return;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
        if (!string.Equals($"{uri.Scheme}://{uri.Authority}", Origin, StringComparison.OrdinalIgnoreCase)) return;

        var relativePath = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (string.IsNullOrEmpty(relativePath)) relativePath = "index.html";

        if (string.Equals(relativePath, "assets/AppIcon.png", StringComparison.OrdinalIgnoreCase))
        {
            var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.png"))?.Stream;
            if (iconStream is not null)
            {
                e.Response = webView.Environment.CreateWebResourceResponse(iconStream, 200, "OK", "Content-Type: image/png\r\nCache-Control: no-cache");
                return;
            }
        }

        var key = relativePath.Replace('/', '.').Replace('\\', '.');
        if (!ResourceLookup.TryGetValue(key, out var manifestName))
        {
            e.Response = webView.Environment.CreateWebResourceResponse(Stream.Null, 404, "Not Found", "Content-Type: text/plain; charset=utf-8");
            return;
        }

        var stream = CurrentAssembly.GetManifestResourceStream(manifestName);
        if (stream is null)
        {
            e.Response = webView.Environment.CreateWebResourceResponse(Stream.Null, 404, "Not Found", "Content-Type: text/plain; charset=utf-8");
            return;
        }

        var contentType = GetContentType(relativePath);
        e.Response = webView.Environment.CreateWebResourceResponse(
            stream,
            200,
            "OK",
            $"Content-Type: {contentType}\r\nCache-Control: no-store");
    }

    private static Dictionary<string, string> BuildResourceLookup()
    {
        const string prefix = "CodexMultiLauncher.WebUi.";
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in CurrentAssembly.GetManifestResourceNames())
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var logical = name[prefix.Length..];
                map[logical] = name;
            }
        }
        return map;
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "application/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream",
    };
}
