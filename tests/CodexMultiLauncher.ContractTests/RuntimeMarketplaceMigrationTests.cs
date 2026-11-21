using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexMultiLauncher;

internal static class RuntimeMarketplaceMigrationTests
{
    public static async Task RunAsync()
    {
        MigrationMatchesOnlyTheLegacyLocalSource();
        WriteContractChangesOneVersionedKey();
        await RealAppServerMigrationPreservesTheRestAsync();
        Console.WriteLine("Runtime marketplace migration contracts and real app-server integration passed.");
    }

    private static void MigrationMatchesOnlyTheLegacyLocalSource()
    {
        const string legacy = @"C:\Users\Example\.cache\codex-runtimes\codex-primary-runtime\plugins\openai-primary-runtime";
        const string target = @"C:\profiles\example\runtime-user-home\.cache\codex-runtimes\codex-primary-runtime\plugins\openai-primary-runtime";
        using var oldLocal = Config("local", legacy);
        Check(WorkspaceRuntimeMarketplaceService.NeedsMigration(oldLocal.RootElement, legacy, target), "The known shared local runtime marketplace must migrate.");
        using var extendedLocal = Config("local", @"\\?\" + legacy);
        Check(WorkspaceRuntimeMarketplaceService.NeedsMigration(extendedLocal.RootElement, legacy, target), "The known old local marketplace must match an extended Windows path prefix.");
        using var caseAndSeparator = Config("local", legacy.ToUpperInvariant() + Path.DirectorySeparatorChar);
        Check(WorkspaceRuntimeMarketplaceService.NeedsMigration(caseAndSeparator.RootElement, legacy, target), "Legacy local source comparison must normalize Windows case and trailing separators.");
        using var current = Config("local", target);
        Check(!WorkspaceRuntimeMarketplaceService.NeedsMigration(current.RootElement, legacy, target), "An already isolated marketplace must be idempotent.");
        using var custom = Config("local", @"C:\Users\Example\custom-marketplace");
        Check(!WorkspaceRuntimeMarketplaceService.NeedsMigration(custom.RootElement, legacy, target), "A custom local marketplace source must not be replaced.");
        using var git = Config("git", legacy);
        Check(!WorkspaceRuntimeMarketplaceService.NeedsMigration(git.RootElement, legacy, target), "Only source_type=local may migrate, even when another source type contains the same text.");
        using var nullMetadata = ConfigWithMetadata(legacy, null, null);
        Check(WorkspaceRuntimeMarketplaceService.NeedsMigration(nullMetadata.RootElement, legacy, target), "Explicit null ref and sparse_paths must retain the standard bundled local source contract.");
        using var emptySparse = ConfigWithMetadata(legacy, null, Array.Empty<string>());
        Check(WorkspaceRuntimeMarketplaceService.NeedsMigration(emptySparse.RootElement, legacy, target), "An empty sparse_paths array must remain equivalent to the standard bundled local source.");
        foreach (var reference in new[] { "custom-ref", string.Empty })
        {
            using var customRef = ConfigWithMetadata(legacy, reference, Array.Empty<string>());
            Check(!WorkspaceRuntimeMarketplaceService.NeedsMigration(customRef.RootElement, legacy, target), "Any non-null ref marks a custom source and must prevent bundled marketplace migration.");
        }
        using var customSparse = ConfigWithMetadata(legacy, null, new[] { "custom/plugin-subset" });
        Check(!WorkspaceRuntimeMarketplaceService.NeedsMigration(customSparse.RootElement, legacy, target), "Nonempty sparse_paths marks a custom source even when its local directory matches the legacy runtime.");
        var knownFolderLegacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "plugins", WorkspaceRuntimeMarketplaceService.MarketplaceName);
        Check(SameWindowsDirectory(WorkspaceRuntimeMarketplaceService.GetLegacyMarketplaceSource(), knownFolderLegacy), "Legacy marketplace identity must use the real Windows known folder, independent of a desktop runtime USERPROFILE override.");
        foreach (var input in new[] { "{}", "{\"marketplaces\":{}}", "{\"marketplaces\":{\"openai-primary-runtime\":{\"source\":\"custom\"}}}", "{\"marketplaces\":{\"openai-primary-runtime\":{\"source_type\":\"local\",\"source\":null}}}" })
        {
            using var unrelated = JsonDocument.Parse(input);
            Check(!WorkspaceRuntimeMarketplaceService.NeedsMigration(unrelated.RootElement, legacy, target), "Missing or incomplete marketplace settings must not be promoted into a migration.");
        }
    }

    private static JsonDocument Config(string sourceType, string source) => JsonSerializer.SerializeToDocument(new
    {
        marketplaces = new Dictionary<string, object>
        {
            [WorkspaceRuntimeMarketplaceService.MarketplaceName] = new { source_type = sourceType, source }
        }
    });

    private static JsonDocument ConfigWithMetadata(string source, string? reference, object? sparsePaths) => JsonSerializer.SerializeToDocument(new
    {
        marketplaces = new Dictionary<string, object>
        {
            [WorkspaceRuntimeMarketplaceService.MarketplaceName] = new { source_type = "local", source, @ref = reference, sparse_paths = sparsePaths }
        }
    });

    private static void WriteContractChangesOneVersionedKey()
    {
        const string configPath = @"C:\profiles\example\codex-home\config.toml";
        const string target = @"C:\profiles\example\runtime-user-home\.cache\codex-runtimes\codex-primary-runtime\plugins\openai-primary-runtime";
        using var write = JsonSerializer.SerializeToDocument(WorkspaceRuntimeMarketplaceService.CreateWriteParameters(configPath, "exact-version", target));
        var parameters = write.RootElement;
        var registeredTarget = @"\\?\" + target;
        Check(parameters.GetProperty("keyPath").GetString() == "marketplaces.openai-primary-runtime.source" && parameters.GetProperty("value").GetString() == registeredTarget, "Migration must write only the bundled runtime marketplace source using the Windows canonical registration form.");
        Check(parameters.GetProperty("expectedVersion").GetString() == "exact-version" && parameters.GetProperty("mergeStrategy").GetString() == "replace" && parameters.GetProperty("filePath").GetString() == Path.GetFullPath(configPath), "The official writer must receive an exact configuration file, optimistic version, and replace strategy.");
        Check(parameters.EnumerateObject().Count() == 5, "Source migration must not add plugin toggles, marketplace registration, or unrelated configuration writes.");
        using var alreadyPrefixed = JsonSerializer.SerializeToDocument(WorkspaceRuntimeMarketplaceService.CreateWriteParameters(configPath, "exact-version", registeredTarget));
        Check(alreadyPrefixed.RootElement.GetProperty("value").GetString() == registeredTarget, "An already verbatim source must not acquire a second prefix.");
        const string unc = @"\\server\share\runtime\plugins\openai-primary-runtime";
        const string registeredUnc = @"\\?\UNC\server\share\runtime\plugins\openai-primary-runtime";
        foreach (var uncInput in new[] { unc, registeredUnc })
        {
            using var uncWrite = JsonSerializer.SerializeToDocument(WorkspaceRuntimeMarketplaceService.CreateWriteParameters(configPath, "exact-version", uncInput));
            Check(uncWrite.RootElement.GetProperty("value").GetString() == registeredUnc, "UNC sources must use one verbatim UNC prefix, including an already prefixed input.");
        }
    }

    private static async Task RealAppServerMigrationPreservesTheRestAsync()
    {
        if (Environment.GetEnvironmentVariable("CI") == "true" && CodexExecutableLocator.TryFindCurrentCandidate() is null)
        {
            Console.WriteLine("SKIP RealAppServerMigrationPreservesTheRestAsync on CI runner without local Codex binary.");
            return;
        }
        var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.RuntimeMarketplace"));
        var root = ProfilePathSafety.GetContainedFullPath(boundary, "中文 😀 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Exception? testFailure = null;
        try
        {
            var service = new ProfileService(root);
            var profiles = service.LoadOrCreateDefaults();
            var catalog = new WorkspaceCatalog(root);
            catalog.Reload(profiles);
            var first = catalog.Workspaces.Single(workspace => workspace.Id == profiles[0].Id);
            var second = catalog.Workspaces.Single(workspace => workspace.Id == profiles[1].Id);
            var firstPaths = service.GetPaths(profiles[0]);
            var configPath = Path.Combine(first.CodexHome!, "config.toml");
            var otherConfigPath = Path.Combine(second.CodexHome!, "config.toml");
            var otherBytes = File.ReadAllBytes(otherConfigPath);
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "plugins", WorkspaceRuntimeMarketplaceService.MarketplaceName);
            var target = WorkspaceRuntimeMarketplaceService.GetMarketplaceSource(first.DataPaths);
            Check(target == Path.Combine(firstPaths.PrimaryRuntimeInstallRoot, "codex-primary-runtime", "plugins", WorkspaceRuntimeMarketplaceService.MarketplaceName), "The migration target must be exactly the validated account-local runtime plugin marketplace.");
            Check(!Directory.Exists(target), "This cold-start fixture must begin before its runtime or marketplace has been installed.");
            var custom = Path.Combine(root, "custom marketplace 中文");
            Directory.CreateDirectory(custom);
            var old = "# preserve marketplace migration comment 中文 😀\r\ncli_auth_credentials_store = \"file\"\r\nmodel_reasoning_effort = \"high\"\r\n\r\n[features]\r\nplugins = true\r\n\r\n[windows]\r\nsandbox = \"unelevated\"\r\n\r\n[marketplaces.openai-primary-runtime]\r\nsource_type = \"local\"\r\nsource = " + QuoteToml(legacy) + "\r\n\r\n[marketplaces.custom-market]\r\nsource_type = \"local\"\r\nsource = " + QuoteToml(custom) + "\r\n\r\n[plugins.\"documents@openai-primary-runtime\"]\r\nenabled = true\r\n\r\n[plugins.\"pdf@openai-primary-runtime\"]\r\nenabled = false\r\n";
            var oldBytes = Encoding.UTF8.GetBytes(old);
            File.WriteAllBytes(configPath, oldBytes);
            var expected = await ReadUserConfigAsync(first, configPath);
            expected["marketplaces"]![WorkspaceRuntimeMarketplaceService.MarketplaceName]!["source"] = target;
            var caseVariant = first with
            {
                ElectronUserData = first.ElectronUserData.ToUpperInvariant(),
                CodexHome = first.CodexHome!.ToUpperInvariant(),
                RuntimeUserHome = first.RuntimeUserHome!.ToUpperInvariant()
            };
            var result = await WorkspaceRuntimeMarketplaceService.PrepareAsync(caseVariant);
            Check(result.Changed && result.BackupPath is not null && File.ReadAllBytes(result.BackupPath).SequenceEqual(oldBytes), "Migration must save the original configuration bytes exactly before the one-key write.");
            Check(SameWindowsDirectory(Path.GetDirectoryName(result.BackupPath!)!, first.CodexHome!), "The backup must remain beside the same temporary account's configuration despite input path casing.");
            var actual = await ReadUserConfigAsync(first, configPath);
            var actualSource = actual["marketplaces"]![WorkspaceRuntimeMarketplaceService.MarketplaceName]!["source"]!.GetValue<string>();
            Check(SameWindowsDirectory(actualSource, target), "The real app-server must confirm the account-local marketplace source, including an equivalent verbatim Windows path representation.");
            actual["marketplaces"]![WorkspaceRuntimeMarketplaceService.MarketplaceName]!["source"] = target;
            Check(JsonNode.DeepEquals(expected, actual), "Only the source may change: plugin enabled and disabled states, source_type, the other marketplace, features and all unrelated user configuration must remain identical.");
            Check(File.ReadAllText(configPath).Contains("# preserve marketplace migration comment 中文 😀", StringComparison.Ordinal), "The official writer must retain the unrelated configuration comment.");
            Check(File.ReadAllBytes(otherConfigPath).SequenceEqual(otherBytes), "Runtime marketplace migration must not edit another account's configuration.");
            Check(!Directory.Exists(target), "The official configuration writer must support a future marketplace path without installing, downloading or registering it.");
            var migratedBytes = File.ReadAllBytes(configPath);
            var repeat = await WorkspaceRuntimeMarketplaceService.PrepareAsync(first);
            Check(!repeat.Changed && repeat.BackupPath is null && File.ReadAllBytes(configPath).SequenceEqual(migratedBytes), "A second preparation must create no backup or write and preserve the migrated bytes.");
            var untouched = await WorkspaceRuntimeMarketplaceService.PrepareAsync(second);
            Check(!untouched.Changed && untouched.BackupPath is null && File.ReadAllBytes(otherConfigPath).SequenceEqual(otherBytes), "An account without the legacy marketplace must remain unchanged.");
            Check(!Directory.Exists(target), "Idempotent preparation must still leave the nonexistent marketplace installation to the desktop client.");
            var original = new CodexWorkspace("original", "original fixture", CodexWorkspaceKind.Original, first.ElectronUserData, first.CodexHome);
            try
            {
                await WorkspaceRuntimeMarketplaceService.PrepareAsync(original);
                throw new Exception("An original workspace unexpectedly entered managed runtime marketplace preparation.");
            }
            catch (InvalidOperationException) { }
            Check(File.ReadAllBytes(configPath).SequenceEqual(migratedBytes), "Rejecting an original workspace must not touch its configuration.");
            // The cold-start checks above deliberately ran with no runtime installed.
            // Supply only a synthetic, empty local marketplace for the registration
            // comparison performed by the real desktop after its archive is installed.
            var manifestDirectory = Path.Combine(target, ".agents", "plugins");
            Directory.CreateDirectory(manifestDirectory);
            File.WriteAllText(Path.Combine(manifestDirectory, "marketplace.json"), JsonSerializer.Serialize(new { name = WorkspaceRuntimeMarketplaceService.MarketplaceName, plugins = Array.Empty<object>() }), new UTF8Encoding(false));
            Check(WindowsCanonicalDirectory.Resolve(target.ToUpperInvariant()) == WindowsCanonicalDirectory.Resolve(target), "Existing runtime marketplace canonical paths must recover actual filesystem casing rather than preserve caller casing.");
            await VerifySyntheticMarketplaceAlreadyAddedAsync(first, target);
            var afterRegistration = await ReadUserConfigAsync(first, configPath);
            Check(SameWindowsDirectory(afterRegistration["marketplaces"]![WorkspaceRuntimeMarketplaceService.MarketplaceName]!["source"]!.GetValue<string>(), target), "Synthetic desktop registration must retain the isolated source.");
            afterRegistration["marketplaces"]![WorkspaceRuntimeMarketplaceService.MarketplaceName]!["source"] = target;
            Check(JsonNode.DeepEquals(expected, afterRegistration), "An already-added synthetic runtime marketplace must preserve every plugin state and unrelated user configuration.");
        }
        catch (Exception exception) { testFailure = exception; throw; }
        Exception? cleanupFailure = null;
        try
        {
            var boundaryCheck = Path.GetRelativePath(boundary, Path.GetFullPath(root));
            Check(!Path.IsPathRooted(boundaryCheck) && boundaryCheck != "." && boundaryCheck != ".." && !boundaryCheck.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Runtime marketplace tests refuse cleanup outside the verified temporary fixture.");
        }
        catch (Exception ex)
        {
            cleanupFailure = ex;
        }
        finally
        {
            var relative = Path.GetRelativePath(boundary, Path.GetFullPath(root));
            if (!Path.IsPathRooted(relative) && relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception cleanup) { cleanupFailure ??= cleanup; }
            }
        }
        if (testFailure is not null && cleanupFailure is not null)
            throw new AggregateException("Runtime marketplace integration and fixture cleanup both failed.", testFailure, cleanupFailure);
        if (cleanupFailure is not null)
            throw cleanupFailure;
    }

    private static string QuoteToml(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static bool SameWindowsDirectory(string first, string second)
    {
        static string Normalize(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        return string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonNode> ReadUserConfigAsync(CodexWorkspace workspace, string configPath)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var session = await CodexAppServerSession.StartAsync(workspace.DataPaths, deadline.Token);
        JsonNode config;
        await using (session)
        {
            using var read = await session.RequestAsync("config/read", new { includeLayers = true }, deadline.Token);
            var user = WorkspaceSandboxService.GetUserLayer(read.RootElement.GetProperty("result"), configPath);
            config = JsonNode.Parse(user.GetProperty("config").GetRawText()) ?? throw new InvalidOperationException("The temporary user layer returned an empty configuration.");
        }
        Check(!session.WasForceTerminated && session.ExitCode == 0, "Marketplace integration query must drain output and exit normally after stdin EOF.");
        return config;
    }

    private static async Task VerifySyntheticMarketplaceAlreadyAddedAsync(CodexWorkspace workspace, string target)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var parentHome = Environment.GetEnvironmentVariable("USERPROFILE");
        var start = CodexAppServerSession.CreateStartInfo(CodexExecutableLocator.FindCurrentCandidateOrThrow(), workspace.DataPaths.RequireCodexHome());
        // This owned registration fixture mirrors the managed desktop's policy
        // environment. Normal config/account query sessions retain the real home.
        start.Environment["USERPROFILE"] = workspace.RuntimeUserHome ?? throw new InvalidOperationException("Synthetic registration requires a validated runtime home.");
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Cannot start the synthetic local marketplace registration fixture.");
        }
        catch { process.Dispose(); throw; }
        var session = new CodexAppServerSession(process);
        await using (session)
        {
            using var initialized = await session.RequestAsync("initialize", new { clientInfo = new { name = "CodexMultiLauncherRuntimeMarketplaceContract", version = "1" }, capabilities = new { experimentalApi = true } }, deadline.Token);
            await process.StandardInput.WriteLineAsync(CodexAppServerUsageClient.CreateInitializedNotificationJson().AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            using var added = await session.RequestAsync("marketplace/add", new { source = target }, deadline.Token);
            var response = added.RootElement.GetProperty("result");
            Check(response.GetProperty("alreadyAdded").GetBoolean(), "The real marketplace/add must accept the migrated registration as already added rather than report a different source.");
            Check(response.GetProperty("marketplaceName").GetString() == WorkspaceRuntimeMarketplaceService.MarketplaceName, "Synthetic registration must identify only the bundled runtime marketplace.");
            Check(SameWindowsDirectory(response.GetProperty("installedRoot").GetString()!, target), "The real registration response must identify the isolated synthetic installation directory.");
        }
        Check(!session.WasForceTerminated && session.ExitCode == 0, "The owned registration fixture must drain output and exit normally after stdin EOF.");
        Check(Environment.GetEnvironmentVariable("USERPROFILE") == parentHome, "The synthetic registration's desktop home override must never change the parent process environment.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
