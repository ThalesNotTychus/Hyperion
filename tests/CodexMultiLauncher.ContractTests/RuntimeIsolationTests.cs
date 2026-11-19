using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodexMultiLauncher;

internal static class RuntimeIsolationTests
{
    public static void Run()
    {
        var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.RuntimeIsolation"));
        var root = ProfilePathSafety.GetContainedFullPath(boundary, "中文 😀 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ManagedLaunchUsesDistinctRuntimeHomes(root);
            OriginalAndQueryEnvironmentsKeepTheRealUserHome(root);
            OptionalNodeHomeProbe(root);
            RuntimeHomeCannotEscapeItsProfile(root);
            ExistingProfileBytesRemainUnchanged(root);
            StorageDoesNotIncludeAnotherRuntimeHome(root);
            RuntimeJunctionsAreRejected(root, linkRuntimeHome: true);
            RuntimeJunctionsAreRejected(root, linkRuntimeHome: false);
            RuntimeJunctionsAreRejected(root, linkRuntimeHome: false, linkFinalRuntime: true);
        }
        finally
        {
            RequireBelow(boundary, root);
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("Desktop runtime isolation contracts passed.");
    }

    private static void ManagedLaunchUsesDistinctRuntimeHomes(string root)
    {
        var project = ProfilePathSafety.GetContainedFullPath(root, "managed");
        var service = new ProfileService(project);
        var profiles = service.LoadOrCreateDefaults();
        var catalog = new WorkspaceCatalog(project);
        catalog.Reload(profiles);
        var first = catalog.Workspaces.Single(workspace => workspace.Id == profiles[0].Id);
        var second = catalog.Workspaces.Single(workspace => workspace.Id == profiles[1].Id);
        var paths = service.GetPaths(profiles[0]);
        Check(first.RuntimeUserHome == paths.RuntimeUserHome && first.DataPaths.RuntimeUserHome == paths.RuntimeUserHome, "The managed catalog must propagate the runtime home to the actual launch boundary.");
        var firstValues = PackageEnvironmentBlock.CreateValues(first.DataPaths);
        var secondValues = PackageEnvironmentBlock.CreateValues(second.DataPaths);
        Check(firstValues.Count == 4 && firstValues["USERPROFILE"] == paths.RuntimeUserHome, "Managed desktop launches must add only a profile-local USERPROFILE to the three existing Codex overrides.");
        Check(firstValues["USERPROFILE"] != secondValues["USERPROFILE"], "Two managed accounts must never select the same runtime USERPROFILE.");
        Check(PackageEnvironmentBlock.CreateValues(first.DataPaths)["USERPROFILE"] == firstValues["USERPROFILE"], "Repeated launches of one account must reuse its own runtime home.");
        Check(firstValues["CODEX_HOME"] == paths.CodexHome && firstValues["CODEX_SQLITE_HOME"] == paths.CodexHome && firstValues["CODEX_ELECTRON_USER_DATA_PATH"] == paths.ElectronUserData, "Runtime isolation must preserve all existing absolute account data paths.");
        var expected = $"CODEX_ELECTRON_USER_DATA_PATH={paths.ElectronUserData}\0CODEX_HOME={paths.CodexHome}\0CODEX_SQLITE_HOME={paths.CodexHome}\0USERPROFILE={paths.RuntimeUserHome}\0\0";
        using var block = PackageEnvironmentBlock.Create(first.DataPaths);
        Check(ReadPzz(block.Pointer, expected.Length) == expected, "The real UTF-16 PZZ sent to package activation must contain the isolated runtime home, exact sorted keys and a double NUL, including Unicode paths.");
        var secondPaths = service.GetPaths(profiles[1]);
        var secondExpected = $"CODEX_ELECTRON_USER_DATA_PATH={secondPaths.ElectronUserData}\0CODEX_HOME={secondPaths.CodexHome}\0CODEX_SQLITE_HOME={secondPaths.CodexHome}\0USERPROFILE={secondPaths.RuntimeUserHome}\0\0";
        using var secondBlock = PackageEnvironmentBlock.Create(second.DataPaths);
        Check(ReadPzz(secondBlock.Pointer, secondExpected.Length) == secondExpected, "The second real package environment block must contain only the second account's paths.");
    }

    private static void OriginalAndQueryEnvironmentsKeepTheRealUserHome(string root)
    {
        var original = new CodexWorkspace("original", "original", CodexWorkspaceKind.Original, Path.Combine(root, "original-electron"), Path.Combine(root, "original-codex"), Path.Combine(root, "unused-runtime-home"));
        var originalPaths = original.DataPaths;
        Check(originalPaths.RuntimeUserHome is null, "Original workspace mapping must not propagate a managed desktop runtime override.");
        var originalValues = PackageEnvironmentBlock.CreateValues(originalPaths);
        Check(originalValues.Count == 3 && !originalValues.ContainsKey("USERPROFILE"), "The original official workspace must retain its default Windows user home.");
        Reject(() => PackageEnvironmentBlock.CreateValues(new WorkspaceDataPaths(Path.Combine(root, "unknown"), null)));
        var inheritedKeys = new[] { "USERPROFILE", "HOME", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "HOMEDRIVE", "HOMEPATH" };
        var ambient = inheritedKeys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var paths = new ProfilePaths(root, ProfilePaths.CreateProfileId());
        var values = PackageEnvironmentBlock.CreateValues(new WorkspaceDataPaths(paths.ElectronUserData, paths.CodexHome, paths.RuntimeUserHome));
        var query = CodexAppServerSession.CreateStartInfo("codex.exe", paths.CodexHome);
        foreach (var key in inheritedKeys)
        {
            Check(Environment.GetEnvironmentVariable(key) == ambient[key], $"Desktop launch environment generation must not change the parent process {key}.");
            Check(key == "USERPROFILE" || !values.ContainsKey(key), $"Desktop isolation must not override {key}.");
            query.Environment.TryGetValue(key, out var queryValue);
            Check(queryValue == ambient[key], $"Background account/config queries must retain the real inherited {key}.");
        }
        Check(query.Environment["CODEX_HOME"] == paths.CodexHome && query.Environment["CODEX_SQLITE_HOME"] == paths.CodexHome, "Background queries must keep the selected account's explicit Codex paths.");
    }

    private static void RuntimeHomeCannotEscapeItsProfile(string root)
    {
        var first = new ProfilePaths(root, ProfilePaths.CreateProfileId());
        var second = new ProfilePaths(root, ProfilePaths.CreateProfileId());
        foreach (var invalid in new[] { "runtime-user-home", second.RuntimeUserHome, first.RuntimeUserHome + "-sibling", Path.Combine(root, "outside") })
            Reject(() => PackageEnvironmentBlock.CreateValues(new WorkspaceDataPaths(first.ElectronUserData, first.CodexHome, invalid)));
        Reject(() => PackageEnvironmentBlock.CreateValues(new WorkspaceDataPaths(second.ElectronUserData, first.CodexHome, first.RuntimeUserHome)));
        Reject(() => PackageEnvironmentBlock.CreateValues(new WorkspaceDataPaths(first.ElectronUserData, "codex-home", first.RuntimeUserHome)));
    }

    private static void OptionalNodeHomeProbe(string root)
    {
        const string nodeVariable = "CODEX_MULTI_LAUNCHER_TEST_NODE";
        var node = Environment.GetEnvironmentVariable(nodeVariable);
        if (string.IsNullOrWhiteSpace(node))
        {
            Console.WriteLine($"SKIP actual Node os.homedir probe: {nodeVariable} was not supplied; no Node download or desktop launch attempted.");
            return;
        }
        Check(Path.IsPathFullyQualified(node) && File.Exists(node), "The optional Node probe requires an existing absolute node.exe path.");
        var project = Path.Combine(root, "node probe 中文 😀");
        var service = new ProfileService(project);
        var profile = service.LoadOrCreateDefaults()[0];
        var paths = service.GetPaths(profile);
        var start = new ProcessStartInfo(node)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = paths.CodexHome
        };
        foreach (var pair in PackageEnvironmentBlock.CreateValues(new WorkspaceDataPaths(paths.ElectronUserData, paths.CodexHome, paths.RuntimeUserHome)))
            start.Environment[pair.Key] = pair.Value;
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("""const os=require("node:os"),path=require("node:path");process.stdout.write(JSON.stringify({home:os.homedir(),runtimeRoot:path.join(os.homedir(),".cache","codex-runtimes","codex-primary-runtime")}));""");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the explicitly supplied Node probe.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20_000))
        {
            process.Kill(entireProcessTree: true);
            Check(process.WaitForExit(2_000), "The owned Node probe did not exit after timeout cleanup.");
            throw new InvalidOperationException("The actual Node home probe exceeded its 20 second test budget.");
        }
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        Check(process.ExitCode == 0, "The explicitly supplied Node probe failed: " + error.Result);
        using var observed = JsonDocument.Parse(output.Result);
        Check(string.Equals(observed.RootElement.GetProperty("home").GetString(), paths.RuntimeUserHome, StringComparison.OrdinalIgnoreCase), "Actual Node os.homedir must honor the managed desktop USERPROFILE, including spaces and Unicode.");
        Check(string.Equals(observed.RootElement.GetProperty("runtimeRoot").GetString(), Path.Combine(paths.PrimaryRuntimeInstallRoot, "codex-primary-runtime"), StringComparison.OrdinalIgnoreCase), "The actual Node-derived runtime path must stay below the selected profile's runtime home.");
        Console.WriteLine("Actual Node os.homedir and runtime-root probe passed.");
    }

    private static void ExistingProfileBytesRemainUnchanged(string root)
    {
        var service = new ProfileService(Path.Combine(root, "existing"));
        var profile = service.LoadOrCreateDefaults()[0];
        var paths = service.GetPaths(profile);
        var configPath = Path.Combine(paths.CodexHome, "config.toml");
        var configBytes = Encoding.UTF8.GetBytes("# preserve 中文 😀\r\ncustom_setting = true\r\n");
        var electronPath = Path.Combine(paths.ElectronUserData, "existing-data.bin");
        var accountPath = Path.Combine(paths.CodexHome, "existing-data.bin");
        var cachePath = Path.Combine(paths.PrimaryRuntimeInstallRoot, "existing-runtime.bin");
        File.WriteAllBytes(configPath, configBytes);
        File.WriteAllBytes(electronPath, [1, 2, 3]);
        File.WriteAllBytes(accountPath, [4, 5, 6]);
        File.WriteAllBytes(cachePath, [7, 8, 9]);
        service.EnsureProfileStorage(profile);
        Check(File.ReadAllBytes(configPath).SequenceEqual(configBytes), "Creating or reusing the runtime home must preserve existing config bytes.");
        Check(File.ReadAllBytes(electronPath).SequenceEqual(new byte[] { 1, 2, 3 }) && File.ReadAllBytes(accountPath).SequenceEqual(new byte[] { 4, 5, 6 }) && File.ReadAllBytes(cachePath).SequenceEqual(new byte[] { 7, 8, 9 }), "Storage initialization must preserve existing account, Electron and runtime data.");
    }

    private static void StorageDoesNotIncludeAnotherRuntimeHome(string root)
    {
        var project = Path.Combine(root, "storage");
        var first = new ProfilePaths(project, ProfilePaths.CreateProfileId());
        var second = new ProfilePaths(project, ProfilePaths.CreateProfileId());
        Directory.CreateDirectory(first.PrimaryRuntimeInstallRoot);
        Directory.CreateDirectory(second.PrimaryRuntimeInstallRoot);
        File.WriteAllBytes(Path.Combine(first.PrimaryRuntimeInstallRoot, "runtime.bin"), new byte[7]);
        File.WriteAllBytes(Path.Combine(second.PrimaryRuntimeInstallRoot, "runtime.bin"), new byte[11]);
        File.WriteAllBytes(Path.Combine(first.ProfileDirectory, "adjacent.bin"), new byte[13]);
        var firstSnapshot = ProfileStorageInspector.Inspect(first);
        var secondSnapshot = ProfileStorageInspector.Inspect(second);
        Check(firstSnapshot.RuntimeUserHome.Bytes == 7 && firstSnapshot.TotalBytes == 7 && firstSnapshot.RuntimeUserHome.FileCount == 1, "A profile's runtime storage total must exclude sibling accounts and adjacent profile files.");
        Check(secondSnapshot.RuntimeUserHome.Bytes == 11 && secondSnapshot.TotalBytes == 11, "The other profile must retain its own independently measured runtime storage.");
    }

    private static void RuntimeJunctionsAreRejected(string root, bool linkRuntimeHome, bool linkFinalRuntime = false)
    {
        var fixtureName = linkFinalRuntime ? "install-root" : linkRuntimeHome ? "home" : "cache";
        var project = Path.Combine(root, fixtureName + "-junction");
        var profile = new Profile(ProfilePaths.CreateProfileId(), "junction fixture");
        var paths = new ProfilePaths(project, profile.Id);
        var target = ProfilePathSafety.GetContainedFullPath(root, fixtureName + "-target");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(paths.ProfileDirectory);
        var link = linkFinalRuntime ? Path.Combine(paths.PrimaryRuntimeInstallRoot, "codex-primary-runtime") : linkRuntimeHome ? paths.RuntimeUserHome : Path.Combine(paths.RuntimeUserHome, ".cache");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        RequireBelow(root, link);
        RequireBelow(root, target);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot create the temporary junction fixture.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                Check(process.WaitForExit(2_000), "The owned junction helper did not exit after timeout cleanup.");
                throw new InvalidOperationException("Temporary junction creation exceeded its test budget.");
            }
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            Check(process.ExitCode == 0 && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "The test must create an actual temporary junction, rather than silently skipping reparse-point coverage.");
            Reject(() => PackageEnvironmentBlock.CreateValues(new WorkspaceDataPaths(paths.ElectronUserData, paths.CodexHome, paths.RuntimeUserHome)));
            Reject(() => new ProfileService(project).EnsureProfileStorage(profile));
            var snapshot = ProfileStorageInspector.Inspect(paths);
            Check(snapshot.RuntimeUserHome.Bytes == 0 && snapshot.SkippedEntries > 0, "Runtime storage inspection must skip a linked home or cache without following its target.");
        }
        finally
        {
            RequireBelow(root, link);
            if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) Directory.Delete(link, recursive: false);
        }
    }

    private static void RequireBelow(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        Check(!Path.IsPathRooted(relative) && relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Runtime isolation tests refuse to operate outside their explicitly verified temporary fixture.");
    }

    private static string ReadPzz(IntPtr pointer, int maximumCharacters)
    {
        var text = new StringBuilder();
        for (var index = 0; index < maximumCharacters; index++)
        {
            var character = unchecked((char)Marshal.ReadInt16(pointer, checked(index * sizeof(char))));
            text.Append(character);
            if (character == '\0' && text.Length >= 2 && text[text.Length - 2] == '\0') return text.ToString();
        }
        throw new InvalidOperationException("The package environment block did not terminate within its expected bound.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected runtime isolation rejection.");
    }
}
