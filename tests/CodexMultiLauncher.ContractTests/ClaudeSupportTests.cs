using System.Diagnostics;
using CodexMultiLauncher;

internal static class ClaudeSupportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.ClaudeTests", Guid.NewGuid().ToString("N"), "中文 空格 🧪");
        Directory.CreateDirectory(Path.Combine(root, "data"));
        try
        {
            var oldId = ProfilePaths.CreateProfileId();
            File.WriteAllText(Path.Combine(root, "data", "profiles.json"), "[{\"Id\":\"" + oldId + "\",\"DisplayName\":\"旧账号\"}]");
            var service = new ProfileService(root);
            Check(service.LoadOrCreateDefaults().Single().Client == ClientKind.Codex, "Old profiles without a client must remain Codex.");
            var one = service.CreateProfile("Claude 一", ClientKind.Claude);
            var two = service.CreateProfile("Claude 二", ClientKind.Claude);
            var profiles = service.LoadOrCreateDefaults();
            Check(profiles.Count == 3 && profiles.Count(profile => profile.Client == ClientKind.Claude) == 2, "Mixed profiles must survive a reload.");
            var paths = service.GetPaths(one);
            Check(Directory.Exists(paths.ElectronUserData) && Directory.Exists(paths.ClaudeHome), "Claude directories must be created.");
            Check(!Directory.Exists(paths.CodexHome) && !Directory.Exists(paths.RuntimeUserHome), "Claude must not create Codex settings or runtimes.");
            var catalog = new WorkspaceCatalog(root); catalog.Reload(profiles);
            var first = catalog.FindById(one.Id)!; var second = catalog.FindById(two.Id)!;
            Check(first.IsLaunchable && !first.CanReadUsage && first.CodexHome is null, "Claude must launch without Codex account endpoints.");
            Check(catalog.ResolveObservedElectronDirectory(paths.ElectronUserData, ClientKind.Claude).Id == one.Id, "Observed Claude paths must identify their own account.");
            Throws(() => catalog.ResolveObservedElectronDirectory(paths.ElectronUserData, ClientKind.Codex));
            Throws(() => catalog.ResolveObservedElectronDirectory(service.GetPaths(profiles[0]).ElectronUserData, ClientKind.Claude));
            var external = catalog.ResolveObservedElectronDirectory(Path.Combine(root, "external"), ClientKind.Claude);
            Check(external.Client == ClientKind.Claude && !external.IsLaunchable && !external.CanReadUsage, "Unknown Claude data must stay unresolved.");
            var environment = PackageEnvironmentBlock.CreateValues(first.DataPaths);
            Check(environment.Count == 1 && environment["CLAUDE_CONFIG_DIR"] == paths.ClaudeHome, "Claude environment must contain only its own configuration override.");
            Throws(() => PackageEnvironmentBlock.CreateValues(first.DataPaths with { ClaudeHome = second.ClaudeHome }));
            Throws(() => PackageEnvironmentBlock.CreateValues(first.DataPaths with { CodexHome = paths.CodexHome }));
            Throws(() => PackageEnvironmentBlock.CreateValues(first.DataPaths with { RuntimeUserHome = paths.RuntimeUserHome }));
            File.WriteAllBytes(Path.Combine(paths.ClaudeHome, "sentinel"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(paths.ElectronUserData, "sentinel"), [1, 2, 3, 4, 5]);
            var snapshot = await ProfileStorageInspector.InspectAsync(paths, ClientKind.Claude);
            Check(snapshot.TotalBytes == 8, "Claude storage must count both isolated directories.");
            var association = new ManagedInstanceAssociation(one.Id, 777, DateTimeOffset.UtcNow);
            new ManagedInstanceStore(root, ClientKind.Claude).Save([association]);
            Check(new ManagedInstanceStore(root).Load().Count == 0, "Claude associations must not enter Codex persistence.");
            Check(new ManagedInstanceStore(root, ClientKind.Claude).Load().Single() == association, "Claude associations must persist.");
            Throws(() => WorkspaceSandboxService.PrepareAsync(first).GetAwaiter().GetResult());
            Throws(() => WorkspaceRuntimeMarketplaceService.PrepareAsync(first).GetAwaiter().GetResult());
            Check(ClaudeInstallation.SelectPackage(["Claude_3.1.0.0_x64__pzs8sxrjxfjjc", "Claude_3.2.0.0_x64__pzs8sxrjxfjjc"]).Version == new Version(3, 2, 0, 0), "Claude must select the highest registered main package.");
            foreach (var bad in new[] { "Claude_3.2.0.0_x64__other", "Claude_3.2.0.0_x86__pzs8sxrjxfjjc", "Claude_3.2.0.0_x64_resources_pzs8sxrjxfjjc", "Other_3.2.0.0_x64__pzs8sxrjxfjjc", "Claude_3.2_x64__pzs8sxrjxfjjc" })
                Check(ClaudeInstallation.TryParsePackage(bad) is null, "Reject untrusted Claude package: " + bad);
            File.WriteAllText(Path.Combine(root, "data", "profiles.json"), "[{\"Id\":\"" + oldId + "\",\"DisplayName\":\"坏配置\",\"Client\":99}]");
            Throws(() => new ProfileStore(root).Load());
            Console.WriteLine("Claude compatibility, account boundaries, environment, storage, package identity and persistence contracts passed.");
        }
        finally { DeleteFixture(root, "CodexMultiLauncher.ClaudeTests"); }
    }

    public static async Task RunDesktopSmokeAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.ClaudeSmoke", Guid.NewGuid().ToString("N"), "双账号 smoke 🧪");
        var service = new ProfileService(root);
        var profiles = new[] { service.CreateProfile("Smoke Claude A", ClientKind.Claude), service.CreateProfile("Smoke Claude B", ClientKind.Claude) };
        var catalog = new WorkspaceCatalog(root); catalog.Reload(profiles);
        using var monitor = new CodexInstanceMonitor(root, ClientKind.Claude);
        var owned = new List<ProcessIdentity>();
        try
        {
            Console.WriteLine("Claude package: " + ClaudeInstallation.GetCurrentPackage().FullName);
            foreach (var profile in profiles)
            {
                var workspace = catalog.FindById(profile.Id)!;
                var requestedAt = DateTimeOffset.UtcNow;
                var pid = new PackagedCodexLauncher().Launch(workspace.DataPaths);
                using var process = Process.GetProcessById((int)pid);
                var identity = new ProcessIdentity(pid, new DateTimeOffset(process.StartTime.ToUniversalTime()));
                Check(identity.StartedAtUtc >= requestedAt && OfficialCodexInstallation.IsExactOfficialExecutable(process.MainModule?.FileName, ClaudeInstallation.GetExecutablePath()), "Smoke activation must return a fresh official root.");
                owned.Add(identity);
                monitor.RegisterLaunched(workspace, pid, requestedAt);
                Console.WriteLine($"Launched {profile.DisplayName}, PID {pid}.");
            }
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                await monitor.RefreshAllAsync(catalog);
                if (profiles.All(profile => monitor.Instances.Any(instance => instance.ProfileId == profile.Id && instance.HasMainWindow && !instance.IsStopped))) break;
                await Task.Delay(500);
            }
            foreach (var profile in profiles)
            {
                var instance = monitor.Instances.Single(instance => instance.ProfileId == profile.Id && !instance.IsStopped);
                Check(instance.HasMainWindow, "Each Claude profile must own a window.");
                Check(owned.Any(identity => identity.ProcessId == instance.ProcessId && identity.StartedAtUtc == instance.StartedAtUtc), "Each window must belong to this smoke run.");
                Check(CodexInstanceMonitor.ObserveElectronDirectory([new(instance.ProcessId, instance.StartedAtUtc)]).Directory is { } observed
                    && PathIdentity.Equals(observed, service.GetPaths(profile).ElectronUserData), "Root command line must retain its own explicit data directory.");
                Check(Directory.EnumerateFileSystemEntries(service.GetPaths(profile).ElectronUserData).Any(), "Each profile must receive its own Electron data.");
                Console.WriteLine($"Verified {profile.DisplayName}: PID {instance.ProcessId}, {instance.ProcessCount} processes, independent data and window.");
            }
            Check(owned.Select(identity => identity.ProcessId).Distinct().Count() == 2, "Two accounts must use different roots.");
            Console.WriteLine("Claude desktop dual-profile smoke passed. No login or model request was performed.");
        }
        finally
        {
            foreach (var identity in owned)
            {
                try
                {
                    using var process = Process.GetProcessById((int)identity.ProcessId);
                    if (process.HasExited || new DateTimeOffset(process.StartTime.ToUniversalTime()) != identity.StartedAtUtc
                        || !OfficialCodexInstallation.IsExactOfficialExecutable(process.MainModule?.FileName, ClaudeInstallation.GetExecutablePath())) continue;
                    process.CloseMainWindow();
                    if (!process.WaitForExit(2000)) { process.Kill(true); process.WaitForExit(2000); }
                }
                catch (ArgumentException) { }
            }
            DeleteFixture(root, "CodexMultiLauncher.ClaudeSmoke");
        }
    }

    private static void DeleteFixture(string root, string directory)
    {
        var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), directory));
        var full = Path.GetFullPath(root);
        Check(full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fixture cleanup must stay within its temporary boundary.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected operation to be rejected.");
    }
}
