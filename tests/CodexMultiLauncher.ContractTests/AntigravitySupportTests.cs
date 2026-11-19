using System.Diagnostics;
using System.Text;
using CodexMultiLauncher;

internal static class AntigravitySupportTests
{
    public static async Task RunAsync()
    {
        var root = NewRoot("Contracts");
        try
        {
            var service = new ProfileService(root);
            var codex = service.LoadOrCreateDefaults()[0];
            var codexConfig = Path.Combine(service.GetPaths(codex).CodexHome, "config.toml");
            var originalConfig = File.ReadAllBytes(codexConfig);
            var claude = service.CreateProfile("Claude", ClientKind.Claude);
            var one = service.CreateProfile("反重力 一", ClientKind.Antigravity);
            var two = service.CreateProfile("反重力 二", ClientKind.Antigravity);
            var profiles = service.LoadOrCreateDefaults();
            Check(profiles.Count == 5 && profiles.Count(profile => profile.Client == ClientKind.Antigravity) == 2, "All three client types must persist independently.");
            Check(File.ReadAllBytes(codexConfig).SequenceEqual(originalConfig), "Adding Antigravity must preserve existing Codex config bytes.");
            var paths = service.GetPaths(one);
            Check(Directory.Exists(paths.ElectronUserData) && Directory.Exists(Path.Combine(paths.AntigravityHome, ".gemini")), "Antigravity needs separate UI and native home directories.");
            Check(!Directory.Exists(paths.CodexHome) && !Directory.Exists(paths.ClaudeHome) && !Directory.Exists(paths.RuntimeUserHome), "Antigravity must not create another client's data.");
            var catalog = new WorkspaceCatalog(root); catalog.Reload(profiles);
            var first = catalog.FindById(one.Id)!; var second = catalog.FindById(two.Id)!;
            Check(first.IsLaunchable && !first.CanReadUsage && first.CodexHome is null && first.ClaudeHome is null, "Antigravity must launch without Codex endpoints.");
            Check(catalog.ResolveObservedElectronDirectory(paths.ElectronUserData, ClientKind.Antigravity).Id == one.Id, "Antigravity paths must identify their own profile.");
            foreach (var other in new[] { ClientKind.Codex, ClientKind.Claude }) Throws(() => catalog.ResolveObservedElectronDirectory(paths.ElectronUserData, other));
            Throws(() => catalog.ResolveObservedElectronDirectory(service.GetPaths(claude).ElectronUserData, ClientKind.Antigravity));
            Throws(() => catalog.ResolveObservedElectronDirectory(service.GetPaths(codex).ElectronUserData, ClientKind.Antigravity));
            var unknown = catalog.ResolveObservedElectronDirectory(Path.Combine(root, "unknown"), ClientKind.Antigravity);
            Check(!unknown.IsLaunchable && !unknown.CanReadUsage, "Unknown external Antigravity directories must stay unresolved.");
            var original = catalog.ResolveObservedElectronDirectory(AntigravityInstallation.GetOriginalDataDirectory(), ClientKind.Antigravity);
            Check(original.Kind == CodexWorkspaceKind.Original && original.IsLaunchable && AntigravityInstallation.CreateEnvironment(original.DataPaths).Count == 0, "Original Antigravity must inherit the user's existing environment.");
            var parentHome = Environment.GetEnvironmentVariable("USERPROFILE");
            var environment = PackageEnvironmentBlock.CreateValues(first.DataPaths);
            Check(environment.Count == 6 && environment["USERPROFILE"] == paths.AntigravityHome && environment["HOME"] == paths.AntigravityHome
                && environment["APPDATA"] == Path.Combine(paths.AntigravityHome, "AppData", "Roaming")
                && environment["XDG_CONFIG_HOME"] == Path.Combine(paths.AntigravityHome, ".config")
                && environment["ANTIGRAVITY_GEMINI_DIR"] == Path.Combine(paths.AntigravityHome, ".gemini"), "UI, AppData, and backend homes must belong to the same account.");
            var secondEnvironment = PackageEnvironmentBlock.CreateValues(second.DataPaths);
            Check(environment["ANTIGRAVITY_CREDENTIAL_SCOPE"] != secondEnvironment["ANTIGRAVITY_CREDENTIAL_SCOPE"]
                && environment["APPDATA"] != secondEnvironment["APPDATA"], "Two profiles must never share their native credential scope or AppData secret overflow directory.");
            var info = AntigravityInstallation.CreateStartInfo(Path.Combine(root, "app", "Antigravity.exe"), first.DataPaths);
            Check(!info.UseShellExecute && !info.CreateNoWindow && !info.Environment.ContainsKey("ELECTRON_OZONE_PLATFORM_HINT")
                && info.ArgumentList.Single() == "--user-data-dir=" + paths.ElectronUserData, "Desktop launch must never inject headless spoofing flags and must pass a single unescaped argument.");
            Check(info.Environment["USERPROFILE"] == paths.AntigravityHome && Environment.GetEnvironmentVariable("USERPROFILE") == parentHome, "Launch environment must not modify the parent process.");
            Throws(() => AntigravityInstallation.CreateEnvironment(first.DataPaths with { AntigravityHome = second.AntigravityHome }));
            Throws(() => AntigravityInstallation.CreateEnvironment(first.DataPaths with { AntigravityHome = Path.Combine(root, "..", "outside") }));
            Throws(() => AntigravityInstallation.CreateEnvironment(first.DataPaths with { CodexHome = paths.CodexHome }));
            Throws(() => AntigravityInstallation.CreateEnvironment(first.DataPaths with { ClaudeHome = paths.ClaudeHome }));
            Throws(() => AntigravityInstallation.CreateEnvironment(first.DataPaths with { RuntimeUserHome = paths.RuntimeUserHome }));
            Throws(() => AntigravityInstallation.CreateEnvironment(first.DataPaths with { AntigravityHome = null }));
            File.WriteAllBytes(Path.Combine(paths.ElectronUserData, "sentinel"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(paths.AntigravityHome, ".gemini", "sentinel"), [1, 2, 3, 4, 5]);
            Check((await ProfileStorageInspector.InspectAsync(paths, ClientKind.Antigravity)).TotalBytes == 8, "Storage must include native Antigravity data.");
            var association = new ManagedInstanceAssociation(one.Id, 777, DateTimeOffset.UtcNow);
            new ManagedInstanceStore(root, ClientKind.Antigravity).Save([association]);
            Check(new ManagedInstanceStore(root, ClientKind.Antigravity).Load().Single() == association
                && new ManagedInstanceStore(root).Load().Count == 0 && new ManagedInstanceStore(root, ClientKind.Claude).Load().Count == 0, "Antigravity process ownership must stay separate from other clients.");
            Throws(() => WorkspaceSandboxService.PrepareAsync(first).GetAwaiter().GetResult());
            Throws(() => WorkspaceRuntimeMarketplaceService.PrepareAsync(first).GetAwaiter().GetResult());
            Check(AntigravityInstallation.IsDesktopProduct("Antigravity", "Google") && !AntigravityInstallation.IsDesktopProduct("Other", "Google")
                && !AntigravityInstallation.IsDesktopProduct("Antigravity", "Other"), "Executable names alone cannot identify Antigravity.");
            var expectedExe = Path.Combine(root, "安装, 目录", "Antigravity.exe");
            Check(AntigravityInstallation.TryParseDisplayIcon('"' + expectedExe + "\",0", out var parsed) && parsed == expectedExe, "Registered icon parsing must preserve commas, spaces and Unicode.");
            Check(!AntigravityInstallation.TryParseDisplayIcon("Antigravity.exe,0", out _) && !AntigravityInstallation.TryParseDisplayIcon(Path.Combine(root, "Other.exe"), out _), "Relative and unrelated executable paths must be rejected.");
            using var capabilities = new MemoryStream(Encoding.ASCII.GetBytes(new string(' ', 65526) + "ANTIGRAVITY_GEMINI_DIR padding ANTIGRAVITY_CREDENTIAL_SCOPE"));
            Check(AntigravityInstallation.ContainsIsolationCapabilities(capabilities), "Capability detection must handle a marker across a read boundary.");
            using var unsupported = new MemoryStream(Encoding.ASCII.GetBytes("ANTIGRAVITY_GEMINI_DIR only"));
            Check(!AntigravityInstallation.ContainsIsolationCapabilities(unsupported), "Missing credential isolation must be rejected.");
            Console.WriteLine("Antigravity profiles, path ownership, native homes/scopes, environment, storage and installation contracts passed.");
        }
        finally { DeleteFixture(root); }
    }

    public static async Task RunDesktopSmokeAsync()
    {
        var root = NewRoot("DesktopSmoke");
        var executable = AntigravityInstallation.GetExecutablePath();
        AntigravityInstallation.RequireIsolationSupport(executable);
        var service = new ProfileService(root);
        var profiles = new[] { service.CreateProfile("Smoke Antigravity A", ClientKind.Antigravity), service.CreateProfile("Smoke Antigravity B", ClientKind.Antigravity) };
        var catalog = new WorkspaceCatalog(root); catalog.Reload(profiles);
        using var monitor = new CodexInstanceMonitor(root, ClientKind.Antigravity);
        var owned = new List<ProcessIdentity>();
        try
        {
            Console.WriteLine("Antigravity desktop: " + FileVersionInfo.GetVersionInfo(executable).ProductVersion);
            foreach (var profile in profiles)
            {
                var workspace = catalog.FindById(profile.Id)!;
                var requestedAt = DateTimeOffset.UtcNow;
                var pid = new PackagedCodexLauncher().Launch(workspace.DataPaths);
                using var process = Process.GetProcessById((int)pid);
                var identity = new ProcessIdentity(pid, new DateTimeOffset(process.StartTime.ToUniversalTime()));
                Check(identity.StartedAtUtc >= requestedAt && OfficialCodexInstallation.IsExactOfficialExecutable(process.MainModule?.FileName, executable), "Smoke activation must return a new Antigravity root.");
                owned.Add(identity);
                monitor.RegisterLaunched(workspace, pid, requestedAt);
                Console.WriteLine($"Launched {profile.DisplayName}, PID {pid}.");
            }
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(35))
            {
                await monitor.RefreshAllAsync(catalog);
                if (profiles.All(profile => monitor.Instances.Any(instance => instance.ProfileId == profile.Id && instance.HasMainWindow && !instance.IsStopped)
                    && Directory.Exists(Path.Combine(service.GetPaths(profile).AntigravityHome, ".gemini", "antigravity")))) break;
                await Task.Delay(500);
            }
            foreach (var profile in profiles)
            {
                var instance = monitor.Instances.Single(instance => instance.ProfileId == profile.Id && !instance.IsStopped);
                Check(instance.HasMainWindow && owned.Any(identity => identity.ProcessId == instance.ProcessId && identity.StartedAtUtc == instance.StartedAtUtc), "Each account must own a separate window and root.");
                var observation = CodexInstanceMonitor.ObserveElectronDirectory([new(instance.ProcessId, instance.StartedAtUtc)]);
                Check(observation.Directory is { } observed && PathIdentity.Equals(observed, service.GetPaths(profile).ElectronUserData), "Each root must retain its own UI directory.");
                Check(Directory.EnumerateFileSystemEntries(service.GetPaths(profile).ElectronUserData).Any(), "UI data must be written to the account directory.");
                Check(Directory.Exists(Path.Combine(service.GetPaths(profile).AntigravityHome, ".gemini", "antigravity")), "The native backend must use the account's own Gemini directory.");
                Console.WriteLine($"Verified {profile.DisplayName}: PID {instance.ProcessId}, independent window, UI data, Gemini home and configured credential scope.");
            }
            Check(owned.Select(identity => identity.ProcessId).Distinct().Count() == 2, "Two accounts must use different root processes.");
            Console.WriteLine("Antigravity dual-profile smoke passed. No login or model request was performed.");
        }
        finally
        {
            foreach (var identity in owned)
            {
                try
                {
                    using var process = Process.GetProcessById((int)identity.ProcessId);
                    if (process.HasExited || new DateTimeOffset(process.StartTime.ToUniversalTime()) != identity.StartedAtUtc
                        || !OfficialCodexInstallation.IsExactOfficialExecutable(process.MainModule?.FileName, executable)) continue;
                    process.Kill(true); process.WaitForExit(3000);
                }
                catch (ArgumentException) { }
            }
            DeleteFixture(root);
        }
    }

    private static string NewRoot(string purpose) => Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.AntigravityTests", purpose, Guid.NewGuid().ToString("N"), "中文 空格 🧪");
    private static void DeleteFixture(string root)
    {
        var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.AntigravityTests"));
        var full = Path.GetFullPath(root);
        Check(full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fixture cleanup outside temporary boundary rejected.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected operation to be rejected.");
    }
}
