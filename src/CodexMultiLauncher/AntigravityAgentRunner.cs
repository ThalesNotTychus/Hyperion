using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexMultiLauncher;

public sealed record AntigravityHomeInspection(
    bool HasAuthState,
    bool HasAgyCli)
{
    public bool IsAuthenticated => HasAuthState && HasAgyCli;
}

public sealed record AntigravityUsageSnapshot(
    string? Account,
    string? Plan,
    decimal? PrimaryUsedPercent,
    DateTimeOffset? PrimaryResetsAt,
    decimal? SecondaryUsedPercent,
    DateTimeOffset? SecondaryResetsAt,
    DateTimeOffset RefreshedAtUtc);

/// <summary>
/// Official zero-risk Agent Client Protocol (ACP, https://agentclientprotocol.com) runner for Google Antigravity CLI (`agy acp`).
/// Communicates strictly over documented `stdio` JSON-RPC 2.0 (`initialize` -> `session/new` -> `session/set_config_option` ->
/// `session/prompt` -> `session/update` / `session/request_permission` / `session/cancel`), never reading OAuth tokens,
/// never scraping internal daemon files, and never calling undocumented `language_server` endpoints.
/// </summary>
public sealed class AntigravityAgentRunner : IGroupChatRunner
{
    public const string OfficialInstallCommand = "irm https://antigravity.google/cli/install.ps1 | iex";
    private const string OfficialManifestBaseUrl = "https://antigravity-cli-auto-updater-974169037036.us-central1.run.app";

    private static readonly SemaphoreSlim BootstrapLock = new(1, 1);
    private static readonly HttpClient BootstrapHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromMinutes(4),
    };

    private readonly Func<GroupChatRun, ProcessStartInfo>? _createStartInfo;
    private readonly Action<string, long, long>? _onTokenUsage;

    public AntigravityAgentRunner(
        Func<GroupChatRun, ProcessStartInfo>? createStartInfo = null,
        Action<string, long, long>? onTokenUsage = null)
    {
        _createStartInfo = createStartInfo;
        _onTokenUsage = onTokenUsage;
    }

    public static string GetHomeForWorkspace(CodexWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind == CodexWorkspaceKind.Managed && !string.IsNullOrWhiteSpace(workspace.AntigravityHome))
            return Path.GetFullPath(workspace.AntigravityHome);
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static (string? Path, long ExpiresTick) _cachedAgyExe;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool HasAuth, long ExpiresTick)> HomeAuthCache = new(StringComparer.OrdinalIgnoreCase);

    public static void InvalidateDiscoveryCache()
    {
        _cachedAgyExe = default;
        HomeAuthCache.Clear();
    }

    public static string? TryFindAgyCliExecutable()
    {
        var cached = _cachedAgyExe;
        if (Environment.TickCount64 < cached.ExpiresTick && (cached.Path is null || File.Exists(cached.Path)))
            return cached.Path;

        var resolved = TryFindAgyCliExecutableCore();
        _cachedAgyExe = (resolved, Environment.TickCount64 + 30_000);
        return resolved;
    }

    private static string? TryFindAgyCliExecutableCore()
    {
        var candidates = new List<string>();

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var envLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        foreach (var loc in new[] { localAppData, envLocalAppData }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Official Google Antigravity CLI installer (`install.ps1`) installs here: `%LOCALAPPDATA%\agy\bin\agy.exe`
            candidates.Add(Path.Combine(loc!, "agy", "bin", "agy.exe"));
            candidates.Add(Path.Combine(loc!, "agy", "agy.exe"));
            candidates.Add(Path.Combine(loc!, "Programs", "antigravity-cli", "agy.exe"));
            candidates.Add(Path.Combine(loc!, "Programs", "antigravity-cli", "bin", "agy.exe"));
            candidates.Add(Path.Combine(loc!, "antigravity-cli", "bin", "agy.exe"));
            candidates.Add(Path.Combine(loc!, "Microsoft", "WinGet", "Links", "agy.exe"));
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, ".antigravity", "bin", "agy.exe"));
            candidates.Add(Path.Combine(userProfile, ".antigravity", "bin", "agy.cmd"));
            candidates.Add(Path.Combine(userProfile, ".local", "bin", "agy.exe"));
            candidates.Add(Path.Combine(userProfile, ".local", "bin", "agy.cmd"));
            candidates.Add(Path.Combine(userProfile, ".gemini", "antigravity-cli", "bin", "agy.exe"));
        }

        if (!string.IsNullOrWhiteSpace(roamingAppData))
        {
            candidates.Add(Path.Combine(roamingAppData, "npm", "agy.cmd"));
            candidates.Add(Path.Combine(roamingAppData, "npm", "agy.exe"));
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Path.IsPathFullyQualified(dir)) continue;
                candidates.Add(Path.Combine(dir, "agy.exe"));
                candidates.Add(Path.Combine(dir, "agy.cmd"));
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                // Exclude Electron IDE window launcher (`...\Programs\antigravity\bin\agy.cmd` next to `Antigravity.exe`)
                // when it is merely an Electron wrapper rather than the standalone `agy` CLI.
                var parent = Directory.GetParent(candidate);
                var grandParent = parent?.Parent;
                if (grandParent is not null
                    && File.Exists(Path.Combine(grandParent.FullName, "Antigravity.exe"))
                    && candidate.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                {
                    var scriptText = File.ReadAllText(candidate);
                    if (scriptText.Contains("ELECTRON_RUN_AS_NODE", StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                ProfilePathSafety.ThrowIfReparsePath(candidate);
                return Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
            }
        }

        return null;
    }

    public static string FindAgyCliExecutableOrThrow() =>
        TryFindAgyCliExecutable() ?? throw new InvalidOperationException(
            $"未检测到 Google 官方 Antigravity CLI (`agy`)，且自动部署失败。请在网络通畅时重试或在终端执行 `{OfficialInstallCommand}`。");

    /// <summary>
    /// Automatically downloads and verifies the official Google Antigravity CLI (`%LOCALAPPDATA%\agy\bin\agy.exe`)
    /// using Google's official manifest endpoint and SHA-512 checksum (identical to `https://antigravity.google/cli/install.ps1`)
    /// when not yet installed, so users never need to run terminal install commands manually.
    /// </summary>
    public static async Task<string> EnsureAgyCliExecutableAsync(
        IProgress<GroupChatUpdate>? progress = null,
        CancellationToken token = default)
    {
        var existing = TryFindAgyCliExecutable();
        if (existing is not null) return existing;

        await BootstrapLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            existing = TryFindAgyCliExecutable();
            if (existing is not null) return existing;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                throw new InvalidOperationException("无法定位系统 LocalApplicationData 目录以安装官方 `agy` CLI。");

            var platform = RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "windows_arm64"
                : "windows_amd64";

            progress?.Report(new GroupChatUpdate(string.Empty, "正在首次自动部署 Google 官方 `agy` CLI（免手动安装）…", [
                new ChatExecutionStep("agy-bootstrap", "system", "首次自动下载并校验 Google 官方 `agy` CLI (SHA-512)", "running")
            ]));

            using var manifestResp = await BootstrapHttp.GetAsync($"{OfficialManifestBaseUrl}/manifests/{platform}.json", token).ConfigureAwait(false);
            manifestResp.EnsureSuccessStatusCode();
            using var manifestDoc = JsonDocument.Parse(await manifestResp.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var downloadUrl = TryReadString(manifestDoc.RootElement, "url");
            var expectedSha512 = TryReadString(manifestDoc.RootElement, "sha512");
            if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(expectedSha512))
                throw new InvalidOperationException("官方 `agy` 清单缺少下载链接或 SHA-512 签名摘要。");

            var stagingDir = Path.Combine(localAppData, "antigravity", "staging");
            var targetDir = Path.Combine(localAppData, "agy", "bin");
            Directory.CreateDirectory(stagingDir);
            Directory.CreateDirectory(targetDir);

            var stagingFile = Path.Combine(stagingDir, $"agy-{Guid.NewGuid():N}.exe");
            var targetFile = Path.Combine(targetDir, "agy.exe");

            try
            {
                using (var binResp = await BootstrapHttp.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    binResp.EnsureSuccessStatusCode();
                    await using var netStream = await binResp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await using var fileStream = new FileStream(stagingFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await netStream.CopyToAsync(fileStream, token).ConfigureAwait(false);
                }

                byte[] actualHash;
                await using (var verifyStream = File.OpenRead(stagingFile))
                {
                    actualHash = await SHA512.HashDataAsync(verifyStream, token).ConfigureAwait(false);
                }
                var actualHex = Convert.ToHexStringLower(actualHash);
                if (!string.Equals(actualHex, expectedSha512.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                    throw new InvalidOperationException("官方 `agy.exe` 下载文件 SHA-512 完整性校验未通过，已安全中止部署。");

                File.Move(stagingFile, targetFile, overwrite: true);
                InvalidateDiscoveryCache();

                try
                {
                    var setupPsi = new ProcessStartInfo(targetFile)
                    {
                        WorkingDirectory = targetDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    setupPsi.ArgumentList.Add("install");
                    using var setupProc = Process.Start(setupPsi);
                    if (setupProc is not null)
                    {
                        using var setupCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        setupCts.CancelAfter(TimeSpan.FromSeconds(10));
                        await setupProc.WaitForExitAsync(setupCts.Token).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Non-fatal PATH registration step; targetFile is already verified and ready on disk.
                }

                return Path.GetFullPath(targetFile);
            }
            finally
            {
                try { if (File.Exists(stagingFile)) File.Delete(stagingFile); } catch { }
            }
        }
        finally
        {
            BootstrapLock.Release();
        }
    }

    public static AntigravityHomeInspection InspectHome(string? homeRoot)
    {
        var hasCli = TryFindAgyCliExecutable() is not null;
        if (string.IsNullOrWhiteSpace(homeRoot))
            return new AntigravityHomeInspection(false, hasCli);

        try
        {
            var fullHome = Path.GetFullPath(homeRoot);
            if (HomeAuthCache.TryGetValue(fullHome, out var cached) && cached.HasAuth && Environment.TickCount64 < cached.ExpiresTick && Directory.Exists(fullHome))
            {
                return new AntigravityHomeInspection(true, hasCli);
            }

            var cliConfigDir = Path.Combine(fullHome, ".gemini", "antigravity-cli");
            var geminiRoot = Path.Combine(fullHome, ".gemini", "antigravity");

            var hasCliConfig = Directory.Exists(cliConfigDir) && Directory.EnumerateFileSystemEntries(cliConfigDir).Any();
            var hasGeminiState = Directory.Exists(geminiRoot) && Directory.EnumerateFileSystemEntries(geminiRoot).Any();
            var hasAuth = hasCliConfig || hasGeminiState;
            if (hasAuth)
                HomeAuthCache[fullHome] = (true, Environment.TickCount64 + 10_000);
            else
                HomeAuthCache.TryRemove(fullHome, out _);
            return new AntigravityHomeInspection(hasAuth, hasCli);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new AntigravityHomeInspection(false, hasCli);
        }
    }

    public static bool HasStandaloneGeminiCliOauth(string? homeRoot)
    {
        if (string.IsNullOrWhiteSpace(homeRoot)) return false;
        try
        {
            var geminiFolder = Path.Combine(Path.GetFullPath(homeRoot), ".gemini");
            return File.Exists(Path.Combine(geminiFolder, "oauth_creds.json"))
                || File.Exists(Path.Combine(geminiFolder, "google_accounts.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static bool IsAntigravityBackedCliRecord(UniversalAgentRecord? record, string? homeRoot)
    {
        if (record is null || record.AgentKind != UniversalAgentKind.MarketCli)
            return false;
        if (!string.IsNullOrWhiteSpace(record.CustomCliArgsTemplate) || !string.IsNullOrWhiteSpace(record.EncryptedApiKey))
            return false;
        if (string.Equals(record.PresetId, "antigravity-cli", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(record.PresetId, "gemini-cli", StringComparison.OrdinalIgnoreCase))
            return !HasStandaloneGeminiCliOauth(homeRoot) && InspectHome(homeRoot).HasAuthState;
        return false;
    }

    public static async Task<AntigravityUsageSnapshot?> ReadCliUsageAsync(
        string? homeRoot,
        CancellationToken token,
        ProcessStartInfo? customStartInfo = null)
    {
        ProcessStartInfo psi;
        if (customStartInfo is not null)
        {
            psi = customStartInfo;
        }
        else
        {
            var agyExe = TryFindAgyCliExecutable();
            if (agyExe is null) return null;
            var workDir = !string.IsNullOrWhiteSpace(homeRoot) && Directory.Exists(homeRoot)
                ? Path.GetFullPath(homeRoot)
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var env = !string.IsNullOrWhiteSpace(homeRoot) ? CreateEnvironmentForHome(homeRoot) : null;
            psi = CreateCliArgsStartInfo(agyExe, workDir, ["-p", "/usage", "--output-format", "json"], env);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(8));

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) return null;
        try
        {
            proc.StandardInput.Close();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(budget.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(budget.Token);
            await proc.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            return ParseCliUsageJson(stdout, DateTimeOffset.UtcNow);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    public static AntigravityUsageSnapshot? ParseCliUsageJson(string? rawOutput, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(rawOutput)) return null;
        var trimmed = rawOutput.Trim();
        var braceStart = trimmed.IndexOf('{');
        var braceEnd = trimmed.LastIndexOf('}');
        if (braceStart < 0 || braceEnd <= braceStart) return null;

        try
        {
            using var doc = JsonDocument.Parse(trimmed[braceStart..(braceEnd + 1)]);
            return ParseCliUsageElement(doc.RootElement, now, depth: 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AntigravityUsageSnapshot? ParseCliUsageElement(JsonElement root, DateTimeOffset now, int depth)
    {
        if (root.ValueKind != JsonValueKind.Object || depth > 3) return null;

        if (root.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object)
        {
            var nested = ParseCliUsageElement(structured, now, depth + 1);
            if (nested is not null) return nested;
        }

        if (root.TryGetProperty("response", out var respProp) && respProp.ValueKind == JsonValueKind.String)
        {
            var respText = respProp.GetString();
            if (!string.IsNullOrWhiteSpace(respText) && respText.Contains('{', StringComparison.Ordinal))
            {
                var nested = ParseCliUsageJson(respText, now);
                if (nested is not null) return nested;
            }
        }

        var account = TryReadString(root, "account") ?? TryReadString(root, "email");
        var plan = TryReadString(root, "plan") ?? TryReadString(root, "tier");
        decimal? primaryUsed = null;
        DateTimeOffset? primaryReset = null;
        decimal? secondaryUsed = null;
        DateTimeOffset? secondaryReset = null;

        // 1. Official `agy -p /usage --output-format json` v1.5+ format: `command.data.groups[].buckets[]`
        if (root.TryGetProperty("command", out var cmdObj) && cmdObj.ValueKind == JsonValueKind.Object &&
            cmdObj.TryGetProperty("data", out var dataObj) && dataObj.ValueKind == JsonValueKind.Object &&
            dataObj.TryGetProperty("groups", out var groupsArr) && groupsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var grp in groupsArr.EnumerateArray())
            {
                if (grp.ValueKind != JsonValueKind.Object) continue;
                if (!grp.TryGetProperty("buckets", out var bucketsArr) || bucketsArr.ValueKind != JsonValueKind.Array) continue;

                foreach (var bucket in bucketsArr.EnumerateArray())
                {
                    if (bucket.ValueKind != JsonValueKind.Object) continue;
                    var winKind = (TryReadString(bucket, "window") ?? TryReadString(bucket, "id") ?? TryReadString(bucket, "name") ?? string.Empty).ToLowerInvariant();
                    var remFrac = TryReadPercent(bucket, "remaining_fraction") ?? TryReadPercent(bucket, "remainingFraction");
                    decimal? used = null;
                    if (remFrac is not null)
                    {
                        var remPct = remFrac.Value <= 1.0001m ? remFrac.Value * 100m : remFrac.Value;
                        used = Math.Clamp(100m - remPct, 0m, 100m);
                    }
                    else
                    {
                        used = TryReadPercent(bucket, "used_pct") ?? TryReadPercent(bucket, "usedPercent");
                    }
                    if (used is null) continue;

                    DateTimeOffset? resetAt = null;
                    var resetRaw = TryReadString(bucket, "reset_time") ?? TryReadString(bucket, "resets_at") ?? TryReadString(bucket, "resetsAt");
                    if (!string.IsNullOrWhiteSpace(resetRaw) && DateTimeOffset.TryParse(resetRaw, out var parsedReset))
                        resetAt = parsedReset;

                    var isWeekly = winKind.Contains("week", StringComparison.Ordinal) || winKind.Contains("7d", StringComparison.Ordinal);
                    if (isWeekly)
                    {
                        if (secondaryUsed is null)
                        {
                            secondaryUsed = Math.Clamp(used.Value, 0m, 100m);
                            secondaryReset = resetAt;
                        }
                    }
                    else if (primaryUsed is null)
                    {
                        primaryUsed = Math.Clamp(used.Value, 0m, 100m);
                        primaryReset = resetAt;
                    }
                }

                // Prefer the primary Gemini Models group if both 5h and weekly buckets were populated
                if (primaryUsed is not null && secondaryUsed is not null)
                    break;
            }
        }

        // 2. Alternative `windows[]` array format
        if (root.TryGetProperty("windows", out var windows) && windows.ValueKind == JsonValueKind.Array)
        {
            foreach (var win in windows.EnumerateArray())
            {
                if (win.ValueKind != JsonValueKind.Object) continue;
                var name = (TryReadString(win, "name") ?? TryReadString(win, "id") ?? string.Empty).ToLowerInvariant();
                var used = TryReadPercent(win, "used_pct") ?? TryReadPercent(win, "usedPercent");
                if (used is null)
                {
                    var rem = TryReadPercent(win, "remaining_pct") ?? TryReadPercent(win, "remainingPercent");
                    if (rem is not null) used = Math.Clamp(100m - rem.Value, 0m, 100m);
                }
                if (used is null) continue;

                DateTimeOffset? resetAt = null;
                var resetRaw = TryReadString(win, "resets_at") ?? TryReadString(win, "resetsAt");
                if (!string.IsNullOrWhiteSpace(resetRaw) && DateTimeOffset.TryParse(resetRaw, out var parsedReset))
                    resetAt = parsedReset;

                var isWeekly = name.Contains("week", StringComparison.Ordinal) || name.Contains("7d", StringComparison.Ordinal) || name.Contains("7-day", StringComparison.Ordinal);
                if (isWeekly && secondaryUsed is null)
                {
                    secondaryUsed = Math.Clamp(used.Value, 0m, 100m);
                    secondaryReset = resetAt;
                }
                else if (primaryUsed is null)
                {
                    primaryUsed = Math.Clamp(used.Value, 0m, 100m);
                    primaryReset = resetAt;
                }
                else if (secondaryUsed is null)
                {
                    secondaryUsed = Math.Clamp(used.Value, 0m, 100m);
                    secondaryReset = resetAt;
                }
            }
        }

        if (primaryUsed is null && secondaryUsed is null && string.IsNullOrWhiteSpace(plan) && string.IsNullOrWhiteSpace(account))
            return null;

        return new AntigravityUsageSnapshot(account, plan, primaryUsed, primaryReset, secondaryUsed, secondaryReset, now);
    }

    private static string? TryReadString(JsonElement obj, string propName) =>
        obj.TryGetProperty(propName, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString()!.Trim()
            : null;

    private static decimal? TryReadPercent(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
        if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString()?.TrimEnd('%'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var sd)) return sd;
        return null;
    }

    private static ProcessStartInfo CreateCliArgsStartInfo(
        string agyExecutable,
        string workingDirectory,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        var isBatch = agyExecutable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || agyExecutable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo(isBatch ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") : agyExecutable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        if (isBatch)
        {
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(agyExecutable);
        }

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        ScrubInheritedAntigravityEnv(psi);
        if (environmentOverrides is not null)
        {
            foreach (var (k, v) in environmentOverrides)
                psi.Environment[k] = v;
        }

        return psi;
    }

    public static IReadOnlyDictionary<string, string> CreateEnvironmentForHome(string homeRoot)
    {
        var fullHome = Path.GetFullPath(homeRoot);
        var defaultUserHome = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (PathIdentity.Equals(fullHome, defaultUserHome))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["USERPROFILE"] = defaultUserHome,
                ["HOME"] = defaultUserHome,
            };
        }

        var appDataRoaming = Path.Combine(fullHome, "AppData", "Roaming");
        var xdgConfig = Path.Combine(fullHome, ".config");
        var geminiDir = Path.Combine(fullHome, ".gemini");
        var appDataDir = Path.Combine(geminiDir, "antigravity");
        Directory.CreateDirectory(appDataRoaming);
        Directory.CreateDirectory(xdgConfig);
        Directory.CreateDirectory(geminiDir);
        Directory.CreateDirectory(appDataDir);

        var parentDirName = Path.GetFileName(Path.GetDirectoryName(fullHome) ?? string.Empty);
        var scopeSuffix = !string.IsNullOrWhiteSpace(parentDirName) && fullHome.EndsWith("antigravity-home", StringComparison.OrdinalIgnoreCase)
            ? parentDirName
            : Path.GetFileName(fullHome);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["USERPROFILE"] = fullHome,
            ["HOME"] = fullHome,
            ["APPDATA"] = appDataRoaming,
            ["XDG_CONFIG_HOME"] = xdgConfig,
            ["ANTIGRAVITY_GEMINI_DIR"] = geminiDir,
            ["ANTIGRAVITY_APP_DATA_DIR"] = appDataDir,
            ["ANTIGRAVITY_CREDENTIAL_SCOPE"] = "codexmultilauncher-" + scopeSuffix,
        };
    }

    private static void ScrubInheritedAntigravityEnv(ProcessStartInfo psi)
    {
        var staleKeys = psi.Environment.Keys
            .Where(k => k.StartsWith("ANTIGRAVITY_", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var k in staleKeys)
            psi.Environment.Remove(k);
    }

    public static ProcessStartInfo CreateAcpStartInfo(string agyExecutable, string workingDirectory, IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        var isBatch = agyExecutable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || agyExecutable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo(isBatch ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") : agyExecutable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        if (isBatch)
        {
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(agyExecutable);
        }

        psi.ArgumentList.Add("acp");

        ScrubInheritedAntigravityEnv(psi);
        if (environmentOverrides is not null)
        {
            foreach (var (k, v) in environmentOverrides)
                psi.Environment[k] = v;
        }

        return psi;
    }

    public Task<string> RunAsync(GroupChatRun request, IProgress<GroupChatUpdate> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Workspace.Client != ClientKind.Antigravity || !request.Workspace.IsLaunchable)
            throw new InvalidOperationException("此身份必须绑定已验证的 Antigravity 账号。");

        var home = GetHomeForWorkspace(request.Workspace);
        var env = CreateEnvironmentForHome(home);
        return ExecuteAcpTurnAsync(env, request, progress, token);
    }

    public Task<string> RunWithCustomHomeAsync(string homeRoot, GroupChatRun request, IProgress<GroupChatUpdate> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var env = CreateEnvironmentForHome(homeRoot);
        return ExecuteAcpTurnAsync(env, request, progress, token);
    }

    public static async Task<JsonDocument?> ProbeAcpSessionNewAsync(
        string? homeRoot,
        string workingDirectory,
        CancellationToken token,
        ProcessStartInfo? customStartInfo = null)
    {
        ProcessStartInfo psi;
        if (customStartInfo is not null)
        {
            psi = customStartInfo;
        }
        else
        {
            var agyExe = TryFindAgyCliExecutable();
            if (agyExe is null) return null;
            var env = !string.IsNullOrWhiteSpace(homeRoot) ? CreateEnvironmentForHome(homeRoot) : null;
            psi = CreateAcpStartInfo(agyExe, workingDirectory, env);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(10));

        await using var client = await AcpJsonRpcConnection.StartAsync(psi, workingDirectory, ChatSandboxMode.ReadOnly, null, budget.Token).ConfigureAwait(false);
        await client.InitializeAsync(budget.Token).ConfigureAwait(false);
        return await client.CreateSessionAsync(workingDirectory, budget.Token).ConfigureAwait(false);
    }

    private async Task<string> ExecuteAcpTurnAsync(
        IReadOnlyDictionary<string, string> envOverrides,
        GroupChatRun request,
        IProgress<GroupChatUpdate> progress,
        CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
            throw new InvalidOperationException("项目工作目录不存在。");

        ProcessStartInfo startInfo;
        if (_createStartInfo is not null)
        {
            startInfo = _createStartInfo(request);
        }
        else
        {
            var agyExe = await EnsureAgyCliExecutableAsync(progress, token).ConfigureAwait(false);
            startInfo = CreateAcpStartInfo(agyExe, request.WorkingDirectory, envOverrides);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMinutes(20));

        progress.Report(new GroupChatUpdate(string.Empty, "正在通过官方 ACP 协议连接 Antigravity…", [
            new ChatExecutionStep("acp-init", "system", "建立官方 `agy acp` (Agent Client Protocol) 会话", "running")
        ]));

        await using var connection = await AcpJsonRpcConnection.StartAsync(
            startInfo,
            request.WorkingDirectory,
            request.SandboxMode,
            progress,
            (inTok, outTok) => _onTokenUsage?.Invoke(request.Workspace.Id, inTok, outTok),
            budget.Token).ConfigureAwait(false);

        await connection.InitializeAsync(budget.Token).ConfigureAwait(false);
        using var sessionNewDoc = await connection.CreateSessionAsync(request.WorkingDirectory, budget.Token).ConfigureAwait(false);
        var root = sessionNewDoc.RootElement;
        var sessionId = root.TryGetProperty("sessionId", out var sidProp) && sidProp.ValueKind == JsonValueKind.String
            ? sidProp.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidOperationException("ACP `session/new` 未返回有效的 sessionId。");

        // Apply standard ACP `session/set_config_option` for model, thinking level, and mode
        await connection.ApplyRequestedConfigOptionsAsync(
            sessionId!,
            root,
            request.SandboxMode,
            request.Agent.Model,
            request.Agent.ReasoningEffort,
            budget.Token).ConfigureAwait(false);

        return await connection.PromptAndStreamAsync(sessionId!, request.Prompt, request.Attachments, budget.Token).ConfigureAwait(false);
    }

    internal static async Task WriteJsonRpcMessageAsync(Stream output, string json, CancellationToken token)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
        await output.WriteAsync(headerBytes, token).ConfigureAwait(false);
        await output.WriteAsync(bodyBytes, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<string?> ReadNextJsonRpcMessageAsync(Stream input, CancellationToken token)
    {
        var headerBuf = new List<byte>(128);
        var oneByte = new byte[1];

        while (true)
        {
            headerBuf.Clear();
            var firstRead = await input.ReadAsync(oneByte.AsMemory(0, 1), token).ConfigureAwait(false);
            if (firstRead == 0) return null;

            // Fallback tolerance if a stream sends newline-delimited JSON without Content-Length headers
            if (oneByte[0] == (byte)'{')
            {
                headerBuf.Add(oneByte[0]);
                while (true)
                {
                    var r = await input.ReadAsync(oneByte.AsMemory(0, 1), token).ConfigureAwait(false);
                    if (r == 0 || oneByte[0] == (byte)'\n') break;
                    headerBuf.Add(oneByte[0]);
                }
                return Encoding.UTF8.GetString(headerBuf.ToArray()).Trim();
            }

            // Skip leading CR/LF between frames
            if (oneByte[0] is (byte)'\r' or (byte)'\n')
                continue;

            headerBuf.Add(oneByte[0]);
            while (true)
            {
                var r = await input.ReadAsync(oneByte.AsMemory(0, 1), token).ConfigureAwait(false);
                if (r == 0) return null;
                headerBuf.Add(oneByte[0]);
                var c = headerBuf.Count;
                if (c >= 4 &&
                    headerBuf[c - 4] == (byte)'\r' &&
                    headerBuf[c - 3] == (byte)'\n' &&
                    headerBuf[c - 2] == (byte)'\r' &&
                    headerBuf[c - 1] == (byte)'\n')
                {
                    break;
                }
                if (c >= 2 &&
                    headerBuf[c - 2] == (byte)'\n' &&
                    headerBuf[c - 1] == (byte)'\n')
                {
                    break;
                }
            }

            var headerText = Encoding.ASCII.GetString(headerBuf.ToArray());
            var contentLength = -1;
            foreach (var rawLine in headerText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                const string prefix = "Content-Length:";
                if (rawLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(rawLine[prefix.Length..].Trim(), out var parsedLen) &&
                    parsedLen >= 0)
                {
                    contentLength = parsedLen;
                    break;
                }
            }

            if (contentLength < 0)
                continue;

            var payload = new byte[contentLength];
            await input.ReadExactlyAsync(payload, token).ConfigureAwait(false);
            return Encoding.UTF8.GetString(payload);
        }
    }

    private sealed class AcpJsonRpcConnection : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Stream _stdinStream;
        private readonly Stream _stdoutStream;
        private readonly string _workspaceRoot;
        private readonly ChatSandboxMode _sandboxMode;
        private readonly IProgress<GroupChatUpdate>? _progress;
        private readonly Action<long, long>? _onTokenUsage;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly Dictionary<int, TaskCompletionSource<JsonDocument>> _pending = [];
        private readonly object _pendingGate = new();
        private readonly StringBuilder _answer = new();
        private readonly StringBuilder _thinking = new();
        private readonly List<ChatExecutionStep> _steps = [];
        private readonly Task _readLoopTask;
        private readonly Task<string> _stderrTask;
        private int _nextId;
        private string? _activeSessionId;
        private volatile bool _cancelled;

        private AcpJsonRpcConnection(
            Process process,
            string workspaceRoot,
            ChatSandboxMode sandboxMode,
            IProgress<GroupChatUpdate>? progress,
            Action<long, long>? onTokenUsage)
        {
            _process = process;
            _stdinStream = process.StandardInput.BaseStream;
            _stdoutStream = process.StandardOutput.BaseStream;
            _workspaceRoot = Path.GetFullPath(workspaceRoot);
            _sandboxMode = sandboxMode;
            _progress = progress;
            _onTokenUsage = onTokenUsage;
            _stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            _readLoopTask = Task.Run(ReadStdoutLoopAsync);
        }

        public static Task<AcpJsonRpcConnection> StartAsync(
            ProcessStartInfo startInfo,
            string workspaceRoot,
            ChatSandboxMode sandboxMode,
            IProgress<GroupChatUpdate>? progress,
            Action<long, long>? onTokenUsage,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var proc = new Process { StartInfo = startInfo };
            try
            {
                if (!proc.Start())
                {
                    proc.Dispose();
                    throw new InvalidOperationException("无法启动 `agy acp` 进程。");
                }
                return Task.FromResult(new AcpJsonRpcConnection(proc, workspaceRoot, sandboxMode, progress, onTokenUsage));
            }
            catch
            {
                proc.Dispose();
                throw;
            }
        }

        public static Task<AcpJsonRpcConnection> StartAsync(
            ProcessStartInfo startInfo,
            string workspaceRoot,
            ChatSandboxMode sandboxMode,
            IProgress<GroupChatUpdate>? progress,
            CancellationToken token) =>
            StartAsync(startInfo, workspaceRoot, sandboxMode, progress, null, token);

        public async Task InitializeAsync(CancellationToken token)
        {
            var writable = _sandboxMode == ChatSandboxMode.WorkspaceWrite;
            using var _ = await SendRequestAsync("initialize", new
            {
                protocolVersion = 1,
                clientCapabilities = new
                {
                    fs = new { readTextFile = true, writeTextFile = writable },
                    terminal = false,
                },
                clientInfo = new { name = "Hyperion", version = "0.0.1" },
            }, token).ConfigureAwait(false);
        }

        public Task<JsonDocument> CreateSessionAsync(string cwd, CancellationToken token) =>
            SendRequestAsync("session/new", new
            {
                cwd = Path.GetFullPath(cwd),
                mcpServers = Array.Empty<object>(),
            }, token);

        public async Task ApplyRequestedConfigOptionsAsync(
            string sessionId,
            JsonElement sessionNewResult,
            ChatSandboxMode sandboxMode,
            string? requestedModel,
            string? requestedEffort,
            CancellationToken token)
        {
            _activeSessionId = sessionId;
            if (!sessionNewResult.TryGetProperty("configOptions", out var currentOpts) || currentOpts.ValueKind != JsonValueKind.Array)
                return;

            JsonDocument? latestOptsDoc = null;
            try
            {
                // 1. Set Model first (changing model in `agy acp` dynamically updates the available `thought_level` options)
                if (!string.IsNullOrWhiteSpace(requestedModel))
                {
                    var modelOpt = FindConfigOption(currentOpts, static (cat, id) =>
                        string.Equals(cat, "model", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(id, "model", StringComparison.OrdinalIgnoreCase));
                    if (modelOpt is { } mOpt)
                    {
                        var (mId, mType) = ReadConfigIdAndType(mOpt);
                        var cleanReq = NormalizeLegacyAntigravityModelSlug(requestedModel!.Trim());
                        var modelCandidates = !string.IsNullOrWhiteSpace(requestedEffort)
                            ? new[] { $"{cleanReq}-{requestedEffort!.Trim().ToLowerInvariant()}", cleanReq }
                            : new[] { cleanReq };
                        var matchedModel = MatchOptionValue(mOpt, modelCandidates) ?? cleanReq;
                        var updated = await TrySetConfigOptionAsync(sessionId, mId, mType, matchedModel, token).ConfigureAwait(false);
                        if (updated is not null)
                        {
                            latestOptsDoc?.Dispose();
                            latestOptsDoc = updated;
                            if (latestOptsDoc.RootElement.TryGetProperty("configOptions", out var refreshed) && refreshed.ValueKind == JsonValueKind.Array)
                                currentOpts = refreshed;
                        }
                    }
                }

                // 2. Set Reasoning Effort / Thinking Level (`thought_level` in `agy acp` v1.5+, or `effort` / `thinking`)
                if (!string.IsNullOrWhiteSpace(requestedEffort))
                {
                    var effOpt = FindConfigOption(currentOpts, static (cat, id) =>
                        string.Equals(cat, "thought_level", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(cat, "effort", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(cat, "thinking", StringComparison.OrdinalIgnoreCase) ||
                        id.Contains("thought", StringComparison.OrdinalIgnoreCase) ||
                        id.Contains("effort", StringComparison.OrdinalIgnoreCase) ||
                        id.Contains("thinking", StringComparison.OrdinalIgnoreCase));
                    if (effOpt is { } eOpt)
                    {
                        var (eId, eType) = ReadConfigIdAndType(eOpt);
                        var reqEff = requestedEffort!.Trim().ToLowerInvariant();
                        var effCandidates = reqEff switch
                        {
                            "max" => new[] { "max", "xhigh", "high" },
                            "xhigh" => new[] { "xhigh", "max", "high" },
                            _ => new[] { reqEff },
                        };
                        var matchedEff = MatchOptionValue(eOpt, effCandidates);
                        if (matchedEff is not null)
                        {
                            using var _ = await TrySetConfigOptionAsync(sessionId, eId, eType, matchedEff, token).ConfigureAwait(false);
                        }
                    }
                }

                // 3. Set Session Execution Mode (`auto` / `code` for WorkspaceWrite vs `plan` / `ask` for ReadOnly)
                var modeOpt = FindConfigOption(currentOpts, static (cat, id) =>
                    string.Equals(cat, "mode", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, "mode", StringComparison.OrdinalIgnoreCase));
                if (modeOpt is { } mdOpt)
                {
                    var (mdId, mdType) = ReadConfigIdAndType(mdOpt);
                    var desiredModes = sandboxMode == ChatSandboxMode.WorkspaceWrite
                        ? new[] { "auto", "code", "accept-edits", "agent" }
                        : new[] { "plan", "ask", "read-only" };
                    var matchedMode = MatchOptionValue(mdOpt, desiredModes);
                    if (matchedMode is not null)
                    {
                        using var _ = await TrySetConfigOptionAsync(sessionId, mdId, mdType, matchedMode, token).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                latestOptsDoc?.Dispose();
            }
        }

        private static string NormalizeLegacyAntigravityModelSlug(string slug) => slug.ToLowerInvariant() switch
        {
            "orchestrator-neon" => "gemini-pro-orchestrator-neon",
            "orchestrator-carbon" => "gemini-pro-orchestrator",
            "orchestrator-argon" => "gemini-pro-orchestrator-argon",
            "claude-opus-5.5" => "claude-opus-5-5",
            "claude-sonnet-5.5" => "claude-sonnet-5-5",
            "gemini-2.5-pro" => "gemini-4-carbon",
            _ => slug,
        };

        private static JsonElement? FindConfigOption(JsonElement optsArray, Func<string, string, bool> predicate)
        {
            foreach (var opt in optsArray.EnumerateArray())
            {
                var cid = opt.TryGetProperty("configId", out var cp) && cp.ValueKind == JsonValueKind.String
                    ? cp.GetString()
                    : (opt.TryGetProperty("id", out var ip) && ip.ValueKind == JsonValueKind.String ? ip.GetString() : null);
                if (string.IsNullOrWhiteSpace(cid)) continue;
                var cat = opt.TryGetProperty("category", out var catP) && catP.ValueKind == JsonValueKind.String
                    ? catP.GetString() ?? cid!
                    : cid!;
                if (predicate(cat, cid!)) return opt;
            }
            return null;
        }

        private static (string ConfigId, string Type) ReadConfigIdAndType(JsonElement opt)
        {
            var cid = opt.TryGetProperty("configId", out var cp) && cp.ValueKind == JsonValueKind.String
                ? cp.GetString()!
                : opt.GetProperty("id").GetString()!;
            var tp = opt.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString() ?? "select"
                : "select";
            return (cid, tp);
        }

        private async Task<JsonDocument?> TrySetConfigOptionAsync(string sessionId, string configId, string type, string value, CancellationToken token)
        {
            try
            {
                return await SendRequestAsync("session/set_config_option", new
                {
                    sessionId,
                    configId,
                    type,
                    value,
                }, token).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Ignore unsupported option value (-32602) per ACP specification so session stays healthy.
                return null;
            }
        }

        private static string? MatchOptionValue(JsonElement configOption, IReadOnlyList<string> candidates)
        {
            if (!configOption.TryGetProperty("options", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var cand in candidates)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var val = item.TryGetProperty("value", out var vp) && vp.ValueKind == JsonValueKind.String
                        ? vp.GetString()
                        : (item.TryGetProperty("id", out var ip) && ip.ValueKind == JsonValueKind.String ? ip.GetString() : null);
                    if (string.Equals(val, cand, StringComparison.OrdinalIgnoreCase))
                        return val;
                }
            }
            return null;
        }

        public async Task<string> PromptAndStreamAsync(
            string sessionId,
            string promptText,
            IReadOnlyList<ChatImageAttachment>? attachments,
            CancellationToken token)
        {
            _activeSessionId = sessionId;
            _steps.Clear();
            _steps.Add(new ChatExecutionStep("acp-session", "system", $"ACP 会话已就绪 ({sessionId})", "completed"));
            ReportCurrentProgress("正在等待 Antigravity ACP 响应…");

            await using var cancelReg = token.Register(() =>
            {
                _cancelled = true;
                _ = SendNotificationFireAndForgetAsync("session/cancel", new { sessionId });
            });

            var promptBlocks = new List<object> { new { type = "text", text = promptText } };
            if (attachments is { Count: > 0 })
            {
                foreach (var att in attachments)
                {
                    if (string.IsNullOrWhiteSpace(att.Base64Data)) continue;
                    promptBlocks.Add(new
                    {
                        type = "image",
                        mimeType = string.IsNullOrWhiteSpace(att.MimeType) ? "image/png" : att.MimeType,
                        data = att.Base64Data,
                    });
                }
            }

            using var resp = await SendRequestAsync("session/prompt", new
            {
                sessionId,
                prompt = promptBlocks,
            }, token).ConfigureAwait(false);

            if (resp.RootElement.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            {
                var inTok = (usageEl.TryGetProperty("inputTokens", out var it) && it.TryGetInt64(out var iv))
                    ? iv
                    : (usageEl.TryGetProperty("input_tokens", out var it2) && it2.TryGetInt64(out var iv2) ? iv2 : 0L);
                var outTok = (usageEl.TryGetProperty("outputTokens", out var ot) && ot.TryGetInt64(out var ov))
                    ? ov
                    : (usageEl.TryGetProperty("output_tokens", out var ot2) && ot2.TryGetInt64(out var ov2) ? ov2 : 0L);
                if (inTok > 0 || outTok > 0)
                    _onTokenUsage?.Invoke(inTok, outTok);
            }

            var finalAnswer = _answer.ToString().Trim();
            if (string.IsNullOrEmpty(finalAnswer) && resp.RootElement.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                finalAnswer = txt.GetString()?.Trim() ?? string.Empty;

            return string.IsNullOrEmpty(finalAnswer) ? "（ACP 会话已完成，未返回文本内容）" : finalAnswer;
        }

        private async Task<JsonDocument> SendRequestAsync(string method, object parameters, CancellationToken token)
        {
            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingGate) _pending[id] = tcs;

            await using var reg = token.Register(() =>
            {
                lock (_pendingGate) _pending.Remove(id);
                tcs.TrySetCanceled(token);
            });

            var json = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters,
            });
            await WriteFrameAsync(json, token).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }

        private async Task SendNotificationFireAndForgetAsync(string method, object parameters)
        {
            try
            {
                var json = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    method,
                    @params = parameters,
                });
                await WriteFrameAsync(json, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        private async Task WriteFrameAsync(string jsonPayload, CancellationToken token)
        {
            await _writeLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await WriteJsonRpcMessageAsync(_stdinStream, jsonPayload, token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadStdoutLoopAsync()
        {
            try
            {
                while (await ReadNextJsonRpcMessageAsync(_stdoutStream, CancellationToken.None).ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); }
                    catch (JsonException) { continue; }

                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        doc.Dispose();
                        continue;
                    }

                    var hasMethod = root.TryGetProperty("method", out var methodProp) && methodProp.ValueKind == JsonValueKind.String;
                    var hasId = root.TryGetProperty("id", out var idProp) && idProp.ValueKind is JsonValueKind.Number or JsonValueKind.String;

                    // 1. Response to a client->agent request
                    if (!hasMethod && hasId && idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt32(out var respId))
                    {
                        TaskCompletionSource<JsonDocument>? tcs;
                        lock (_pendingGate)
                        {
                            _pending.TryGetValue(respId, out tcs);
                            _pending.Remove(respId);
                        }
                        if (tcs is null)
                        {
                            doc.Dispose();
                            continue;
                        }

                        if (root.TryGetProperty("error", out var errObj) && errObj.ValueKind == JsonValueKind.Object)
                        {
                            var msg = errObj.TryGetProperty("message", out var mp) && mp.ValueKind == JsonValueKind.String
                                ? mp.GetString()
                                : errObj.GetRawText();
                            doc.Dispose();
                            tcs.TrySetException(new InvalidOperationException($"ACP 错误：{msg}"));
                        }
                        else if (root.TryGetProperty("result", out var resEl))
                        {
                            var cloned = JsonDocument.Parse(resEl.GetRawText());
                            doc.Dispose();
                            tcs.TrySetResult(cloned);
                        }
                        else
                        {
                            doc.Dispose();
                            tcs.TrySetResult(JsonDocument.Parse("{}"));
                        }
                        continue;
                    }

                    // 2. Notification from agent (`session/update`)
                    if (hasMethod && !hasId)
                    {
                        try
                        {
                            if (methodProp.GetString() == "session/update" &&
                                root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                            {
                                HandleSessionUpdate(p);
                            }
                        }
                        finally
                        {
                            doc.Dispose();
                        }
                        continue;
                    }

                    // 3. Bidirectional request from agent -> client (`session/request_permission`, `fs/read_text_file`, `fs/write_text_file`)
                    if (hasMethod && hasId)
                    {
                        _ = Task.Run(async () =>
                        {
                            using (doc)
                            {
                                await HandleAgentRequestAsync(methodProp.GetString()!, idProp.Clone(), root).ConfigureAwait(false);
                            }
                        });
                        continue;
                    }

                    doc.Dispose();
                }

                var stderr = (await _stderrTask.ConfigureAwait(false)).Trim();
                var fault = new InvalidOperationException(
                    string.IsNullOrWhiteSpace(stderr)
                        ? "ACP 子进程已退出。若尚未登录，请在内置终端执行 `agy acp --login`。"
                        : $"ACP 进程退出：{stderr}");
                FailAllPending(fault);
            }
            catch (Exception ex)
            {
                FailAllPending(ex);
            }
        }

        private void HandleSessionUpdate(JsonElement parameters)
        {
            var updateObj = parameters.TryGetProperty("update", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : parameters;

            var kind = updateObj.TryGetProperty("sessionUpdate", out var su) && su.ValueKind == JsonValueKind.String
                ? su.GetString()
                : null;

            switch (kind)
            {
                case "agent_thought_chunk":
                {
                    var chunk = ExtractChunkText(updateObj);
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        _thinking.Append(chunk);
                        ReportCurrentProgress("正在深度思考…");
                    }
                    break;
                }

                case "agent_message_chunk":
                {
                    var chunk = ExtractChunkText(updateObj);
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        _answer.Append(chunk);
                        ReportCurrentProgress("正在生成回复…");
                    }
                    break;
                }

                case "tool_call":
                case "tool_call_update":
                {
                    var callId = updateObj.TryGetProperty("toolCallId", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString() ?? Guid.NewGuid().ToString("N")[..8]
                        : Guid.NewGuid().ToString("N")[..8];
                    var title = updateObj.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
                        ? tEl.GetString() ?? "工具调用"
                        : "工具调用";
                    var toolKind = updateObj.TryGetProperty("kind", out var kEl) && kEl.ValueKind == JsonValueKind.String
                        ? kEl.GetString() ?? "tool"
                        : "tool";
                    var rawStatus = updateObj.TryGetProperty("status", out var sEl) && sEl.ValueKind == JsonValueKind.String
                        ? sEl.GetString()
                        : null;
                    var mappedStatus = rawStatus is "completed" or "done" or "succeeded"
                        ? "completed"
                        : rawStatus is "failed" or "error" or "rejected"
                            ? "failed"
                            : "running";

                    var step = new ChatExecutionStep(callId, toolKind, title, mappedStatus);
                    var existingIdx = _steps.FindIndex(x => string.Equals(x.Id, callId, StringComparison.Ordinal));
                    if (existingIdx >= 0) _steps[existingIdx] = step;
                    else _steps.Add(step);

                    ReportCurrentProgress(title);
                    break;
                }
            }
        }

        private static string? ExtractChunkText(JsonElement updateObj)
        {
            if (updateObj.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                    return content.GetString();
                if (content.ValueKind == JsonValueKind.Object &&
                    content.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                {
                    return txt.GetString();
                }
            }
            if (updateObj.TryGetProperty("text", out var directTxt) && directTxt.ValueKind == JsonValueKind.String)
                return directTxt.GetString();
            return null;
        }

        private async Task HandleAgentRequestAsync(string method, JsonElement requestId, JsonElement root)
        {
            object? idValue = requestId.ValueKind == JsonValueKind.Number && requestId.TryGetInt64(out var num)
                ? num
                : requestId.GetString();

            var parameters = root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;

            try
            {
                switch (method)
                {
                    case "session/request_permission":
                    {
                        if (_cancelled)
                        {
                            await SendAgentResponseAsync(idValue, new { outcome = new { outcome = "cancelled" } }).ConfigureAwait(false);
                            return;
                        }

                        var allow = _sandboxMode == ChatSandboxMode.WorkspaceWrite;
                        string? chosenOptionId = null;
                        if (parameters.ValueKind == JsonValueKind.Object &&
                            parameters.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var opt in opts.EnumerateArray())
                            {
                                var optId = opt.TryGetProperty("optionId", out var oid) && oid.ValueKind == JsonValueKind.String ? oid.GetString() : null;
                                var optKind = opt.TryGetProperty("kind", out var ok) && ok.ValueKind == JsonValueKind.String ? ok.GetString() ?? string.Empty : string.Empty;
                                if (string.IsNullOrWhiteSpace(optId)) continue;
                                if (allow && optKind.StartsWith("allow", StringComparison.OrdinalIgnoreCase))
                                {
                                    chosenOptionId = optId;
                                    break;
                                }
                                if (!allow && optKind.StartsWith("reject", StringComparison.OrdinalIgnoreCase))
                                {
                                    chosenOptionId = optId;
                                    break;
                                }
                            }

                            if (chosenOptionId is null && opts.GetArrayLength() > 0)
                            {
                                var fallbackEl = allow ? opts[0] : opts[opts.GetArrayLength() - 1];
                                if (fallbackEl.TryGetProperty("optionId", out var fId))
                                    chosenOptionId = fId.GetString();
                            }
                        }

                        var resultObj = chosenOptionId is not null
                            ? (object)new { outcome = new { outcome = "selected", optionId = chosenOptionId } }
                            : new { outcome = new { outcome = "cancelled" } };
                        await SendAgentResponseAsync(idValue, resultObj).ConfigureAwait(false);
                        return;
                    }

                    case "fs/read_text_file":
                    {
                        var pathArg = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("path", out var pp) ? pp.GetString() : null;
                        var safePath = ResolveContainedPath(pathArg);
                        var content = await File.ReadAllTextAsync(safePath).ConfigureAwait(false);
                        await SendAgentResponseAsync(idValue, new { content }).ConfigureAwait(false);
                        return;
                    }

                    case "fs/write_text_file":
                    {
                        if (_sandboxMode != ChatSandboxMode.WorkspaceWrite)
                            throw new InvalidOperationException("当前会话处于 Ask 只读模式，拒绝写入文件。");
                        var pathArg = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("path", out var pp) ? pp.GetString() : null;
                        var contentArg = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("content", out var cp) ? cp.GetString() ?? string.Empty : string.Empty;
                        var safePath = ResolveContainedPath(pathArg);
                        Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
                        ProfilePathSafety.ThrowIfReparsePath(safePath);
                        await File.WriteAllTextAsync(safePath, contentArg, new UTF8Encoding(false)).ConfigureAwait(false);
                        await SendAgentResponseAsync(idValue, new { }).ConfigureAwait(false);
                        return;
                    }

                    default:
                        await SendAgentErrorAsync(idValue, -32601, $"Unsupported client method: {method}").ConfigureAwait(false);
                        return;
                }
            }
            catch (Exception ex)
            {
                await SendAgentErrorAsync(idValue, -32603, ex.Message).ConfigureAwait(false);
            }
        }

        private string ResolveContainedPath(string? userPath)
        {
            if (string.IsNullOrWhiteSpace(userPath))
                throw new InvalidOperationException("缺少文件路径参数。");
            var candidate = Path.IsPathRooted(userPath)
                ? Path.GetFullPath(userPath)
                : Path.GetFullPath(Path.Combine(_workspaceRoot, userPath));
            var rootWithSep = _workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!string.Equals(candidate, _workspaceRoot, StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("安全拦截：禁止访问当前项目工作区目录以外的文件。");
            }
            ProfilePathSafety.ThrowIfReparsePath(candidate);
            return candidate;
        }

        private async Task SendAgentResponseAsync(object? idValue, object result)
        {
            var payload = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idValue is long l ? JsonValue.Create(l) : JsonValue.Create(idValue?.ToString()),
                ["result"] = JsonSerializer.SerializeToNode(result),
            };
            await WriteFrameAsync(payload.ToJsonString(), CancellationToken.None).ConfigureAwait(false);
        }

        private async Task SendAgentErrorAsync(object? idValue, int code, string message)
        {
            var payload = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idValue is long l ? JsonValue.Create(l) : JsonValue.Create(idValue?.ToString()),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
            };
            await WriteFrameAsync(payload.ToJsonString(), CancellationToken.None).ConfigureAwait(false);
        }

        private void ReportCurrentProgress(string statusText)
        {
            _progress?.Report(new GroupChatUpdate(
                _answer.ToString(),
                statusText,
                _steps.ToArray(),
                _thinking.Length > 0 ? _thinking.ToString() : null));
        }

        private void FailAllPending(Exception ex)
        {
            List<TaskCompletionSource<JsonDocument>> list;
            lock (_pendingGate)
            {
                list = _pending.Values.ToList();
                _pending.Clear();
            }
            foreach (var tcs in list)
                tcs.TrySetException(ex);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    try { _process.StandardInput.Close(); } catch { }
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
                    try { await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                    catch { _process.Kill(entireProcessTree: true); }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                _process.Dispose();
                _writeLock.Dispose();
            }
            try { await _readLoopTask.ConfigureAwait(false); } catch { }
        }
    }
}
