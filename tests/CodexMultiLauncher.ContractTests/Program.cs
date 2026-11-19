using CodexMultiLauncher;

if (PackagedAppResume.IsResumeInvocation(args)) { Environment.ExitCode = PackagedAppResume.Resume(args) ? 0 : 1; return; }
if (args.Contains("--claude-smoke")) { ClaudeSupportTests.RunDesktopSmokeAsync().GetAwaiter().GetResult(); return; }
if (args.Contains("--antigravity-smoke")) { AntigravitySupportTests.RunDesktopSmokeAsync().GetAwaiter().GetResult(); return; }
if (args.Contains("--user-sim")) { RealUserSimulationTests.Run(); return; }
if (AdeFeaturesTests.TryRunAcpMockChildAsync(args).GetAwaiter().GetResult()) return;
if (StdioLifecycleTests.TryRunChildAsync(args).GetAwaiter().GetResult()) return;
if (GroupChatTests.TryRunChildAsync(args).GetAwaiter().GetResult()) return;

var suite = new ContractTestSuite();
suite.RunAll();
ClaudeSupportTests.RunAsync().GetAwaiter().GetResult();
AntigravitySupportTests.RunAsync().GetAwaiter().GetResult();
GroupChatTests.RunAsync().GetAwaiter().GetResult();
AdeFeaturesTests.RunAsync().GetAwaiter().GetResult();
RuntimeIsolationTests.Run();
RuntimeMarketplaceMigrationTests.RunAsync().GetAwaiter().GetResult();
RegressionTests.RunAsync().GetAwaiter().GetResult();
WindowLayoutCheck.Run();
RealUserSimulationTests.RunIsolatedProcess();
Console.WriteLine("All CodexMultiLauncher contract tests passed.");

internal sealed class ContractTestSuite
{
    private readonly string _temporaryRoot = Path.Combine(Path.GetTempPath(), "CodexMultiLauncher.ContractTests", Guid.NewGuid().ToString("N"));

    public void RunAll()
    {
        Directory.CreateDirectory(_temporaryRoot);
        try
        {
            ValidProfileIdAndDerivedPathsStayContained();
            TraversalAndSiblingPrefixEscapesAreRejected();
            RootLocatorUsesTheNearestAncestorMarker();
            RootLocatorFallsBackToTheNormalizedStartDirectory();
            DefaultsAndAddedProfilePersist();
            DisplayNameValidationNormalizesAndRejectsInvalidNames();
            ProfileStorageCreatesIndependentDataAndRuntimeDirectories();
            ExistingConfigIsNotOverwritten();
            PackageSelectionAcceptsTheHighestValidMainPackage();
            PackageSelectionRejectsWrongFamilyArchitectureResourceAndFormat();
            OfficialExecutableRequiresExactRegisteredPath();
            OfficialProcessTreesGroupDescendantsUnderOnlyOfficialRoots();
            LaunchAssociationRequiresFreshRootInsideBoundedWindow();
            ManagedAssociationPersistenceRejectsPidReuse();
            InvalidManagedAssociationFileIsIgnored();
            EnvironmentBlockUsesExactSortedKeysAndDoubleNul();
            ElectronArgumentsQuotePathsContainingSpaces();
            ElectronCommandLineParsingAcceptsQuotedAndUnquotedAndRejectsMissing();
            ConflictingElectronDirectoriesFailClosed();
            WorkspaceCatalogMapsManagedAndDefaultPathsExactly();
            WorkspaceCatalogRejectsDuplicatePathOwnership();
            UnknownExternalWorkspaceCannotLaunchOrReadUsage();
            ObservedAssociationResolutionIsPathFirst();
            UsageParserHandlesOfficialNestedShape();
            UsageParserPreservesMissingAndNullValues();
            DetailedUsageStageRequiresAuthenticatedAccount();
            AppServerUsesOnlyReadMethodContract();
            CandidateSelectionPrefersDirectThenNewestVersion();
            WorkspaceSelectionPreservesIdentity();
            DebuggerCommandLineQuotesTheActualExecutablePath();
            ResumeArgumentParsingAcceptsOnlyTheDocumentedShape();
            RuntimeFormattingHandlesByteAndDurationEdges();
            ProfileStorageInspectorCountsOnlyManagedRoots();
            CodexInstanceMonitorDisposalNeverStopsProcesses();
        }
        finally
        {
            DeleteTemporaryRootSafely();
        }
    }

    private void ValidProfileIdAndDerivedPathsStayContained()
    {
        var paths = new ProfilePaths(NewRoot(), "profile-0123456789abcdef0123456789abcdef");
        Assert(paths.CodexHome.StartsWith(paths.ProfileDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "codex-home must be below the profile root.");
        Assert(paths.ElectronUserData.StartsWith(paths.ProfileDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Electron user data must be below the profile root.");
        Assert(paths.RuntimeUserHome == Path.Combine(paths.ProfileDirectory, "runtime-user-home"), "Runtime USERPROFILE must have one stable profile-local path.");
        Assert(paths.PrimaryRuntimeInstallRoot == Path.Combine(paths.RuntimeUserHome, ".cache", "codex-runtimes"), "Primary runtime installers must have a profile-local cache root.");
    }

    private void TraversalAndSiblingPrefixEscapesAreRejected()
    {
        Throws<InvalidOperationException>(() => _ = new ProfilePaths(NewRoot(), "profile-..\\outside"));
        var root = Path.Combine(NewRoot(), "profiles");
        var siblingPrefix = root + "-sibling";
        Throws<InvalidOperationException>(() => ProfilePathSafety.GetContainedFullPath(root, "..", Path.GetFileName(siblingPrefix)));
    }

    private void RootLocatorUsesTheNearestAncestorMarker()
    {
        var outerRoot = NewRoot();
        var markedRoot = Path.Combine(outerRoot, "marked-root");
        var start = Path.Combine(markedRoot, "nested", "child");
        Directory.CreateDirectory(start);
        File.WriteAllText(Path.Combine(outerRoot, RootLocator.MarkerFileName), string.Empty);
        Directory.CreateDirectory(markedRoot);
        File.WriteAllText(Path.Combine(markedRoot, RootLocator.MarkerFileName), string.Empty);

        Assert(RootLocator.FindProjectRoot(start) == Path.GetFullPath(markedRoot), "Root locator must return the closest ancestor containing the root marker.");
    }

    private void RootLocatorFallsBackToTheNormalizedStartDirectory()
    {
        var root = NewRoot();
        var siblingWithMarker = Path.Combine(root, "marked-sibling");
        var start = Path.Combine(root, "portable", "nested");
        Directory.CreateDirectory(siblingWithMarker);
        Directory.CreateDirectory(start);
        File.WriteAllText(Path.Combine(siblingWithMarker, RootLocator.MarkerFileName), string.Empty);
        var suppliedStart = Path.Combine(start, ".") + Path.DirectorySeparatorChar;
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(suppliedStart));

        Assert(RootLocator.FindProjectRoot(suppliedStart) == expected, "Root locator must ignore adjacent markers and use the normalized start directory when no ancestor is marked.");
    }

    private void DefaultsAndAddedProfilePersist()
    {
        var root = NewRoot();
        var service = new ProfileService(root);
        var defaults = service.LoadOrCreateDefaults();
        Assert(defaults.Count == 2, "First launch must create exactly two default profiles.");
        Assert(defaults[0].DisplayName == "账号 1" && defaults[1].DisplayName == "账号 2", "Default profile names must be stable.");
        var added = service.CreateProfile("工作账号");
        var persisted = new ProfileService(root).LoadOrCreateDefaults();
        Assert(persisted.Count == 3 && persisted.Any(profile => profile.Id == added.Id && profile.DisplayName == "工作账号"), "Added profile must persist in profiles.json.");
    }

    private static void DisplayNameValidationNormalizesAndRejectsInvalidNames()
    {
        Assert(ProfileStore.ValidateDisplayName("  工作账号  ") == "工作账号", "Display names must be trimmed before persistence.");
        Throws<InvalidOperationException>(() => ProfileStore.ValidateDisplayName("   "));
        Throws<InvalidOperationException>(() => ProfileStore.ValidateDisplayName(new string('a', 81)));
    }

    private void ProfileStorageCreatesIndependentDataAndRuntimeDirectories()
    {
        var service = new ProfileService(NewRoot());
        var profile = service.LoadOrCreateDefaults()[0];
        var paths = service.GetPaths(profile);
        Assert(Directory.Exists(paths.CodexHome), "Profile storage must create CODEX_HOME.");
        Assert(Directory.Exists(paths.ElectronUserData), "Profile storage must create Electron user data.");
        Assert(Directory.Exists(paths.RuntimeUserHome) && Directory.Exists(paths.PrimaryRuntimeInstallRoot), "Profile storage must create an independent desktop runtime home and cache root.");
        Assert(File.ReadAllText(Path.Combine(paths.CodexHome, "config.toml")) == WorkspaceSandboxService.NewProfileConfig, "New profiles must use independent credentials and the compatible native sandbox.");
    }

    private void ExistingConfigIsNotOverwritten()
    {
        var root = NewRoot();
        var service = new ProfileService(root);
        var profile = service.LoadOrCreateDefaults()[0];
        var config = Path.Combine(service.GetPaths(profile).CodexHome, "config.toml");
        File.WriteAllText(config, "custom_setting = true\n");
        service.EnsureProfileStorage(profile);
        Assert(File.ReadAllText(config) == "custom_setting = true\n", "Existing config.toml must not be overwritten.");
    }

    private static void PackageSelectionAcceptsTheHighestValidMainPackage()
    {
        var selected = PackageFullNameSelector.SelectOrThrow(new[]
        {
            "OpenAI.Codex_1.2.3.4_x64__2p2nqsd0c76g0",
            "OpenAI.Codex_26.825.51511.0_x64__2p2nqsd0c76g0",
        });
        Assert(selected.FullName == "OpenAI.Codex_26.825.51511.0_x64__2p2nqsd0c76g0", "The highest valid package version must win.");
    }

    private static void PackageSelectionRejectsWrongFamilyArchitectureResourceAndFormat()
    {
        var invalid = new[]
        {
            "Other.Codex_26.825.51511.0_x64__2p2nqsd0c76g0",
            "OpenAI.Codex_26.825.51511.0_arm64__2p2nqsd0c76g0",
            "OpenAI.Codex_26.825.51511.0_x64_resources_2p2nqsd0c76g0",
            "OpenAI.Codex_not-a-version_x64__2p2nqsd0c76g0",
            "OpenAI.Codex_26.825.51511.0_x64__wrongpublisher",
            "OpenAI.Codex_26.825.51511.0_x64_2p2nqsd0c76g0",
        };
        Throws<InvalidOperationException>(() => PackageFullNameSelector.SelectOrThrow(invalid));
    }

    private static void OfficialExecutableRequiresExactRegisteredPath()
    {
        const string registered = "C:\\Program Files\\WindowsApps\\OpenAI.Codex_1_x64__2p2nqsd0c76g0\\app\\ChatGPT.exe";
        Assert(OfficialCodexInstallation.IsExactOfficialExecutable(registered.ToLowerInvariant(), registered), "The registered app\\ChatGPT.exe path must be accepted case-insensitively.");
        Assert(!OfficialCodexInstallation.IsExactOfficialExecutable("C:\\temp\\ChatGPT.exe", registered), "A same-named executable outside the registered package must be rejected.");
        Assert(!OfficialCodexInstallation.IsExactOfficialExecutable("C:\\Program Files\\WindowsApps\\OpenAI.Codex_1_x64__2p2nqsd0c76g0\\ChatGPT.exe", registered), "A path missing the exact app directory must be rejected.");
    }

    private static void OfficialProcessTreesGroupDescendantsUnderOnlyOfficialRoots()
    {
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var root = new ProcessIdentity(10, started);
        var nestedOfficial = new ProcessIdentity(11, started.AddSeconds(1));
        var nestedOfficialGrandchild = new ProcessIdentity(12, started.AddSeconds(2));
        var nonOfficialBridge = new ProcessIdentity(13, started.AddSeconds(3));
        var nonOfficialDescendant = new ProcessIdentity(14, started.AddSeconds(4));
        var officialBelowBridge = new ProcessIdentity(15, started.AddSeconds(5));
        var groups = OfficialProcessTree.GroupRoots(new[]
        {
            (root, 1u, true), (nestedOfficial, 10u, true), (nestedOfficialGrandchild, 11u, true),
            (nonOfficialBridge, 12u, false), (nonOfficialDescendant, 13u, false), (officialBelowBridge, 13u, true),
        });
        Assert(groups.Count == 2, "An official process below a non-official bridge must become a separate root.");
        Assert(groups[root].Select(value => value.ProcessId).OrderBy(value => value).SequenceEqual(new uint[] { 10, 11, 12 }), "Only continuous official descendants may belong to an official root.");
        Assert(!groups[root].Any(value => value.ProcessId is 13 or 14 or 15), "Non-official children and every descendant beyond that boundary must not enter the client tree.");
        Assert(groups[officialBelowBridge].Single().StartedAtUtc == officialBelowBridge.StartedAtUtc, "An official process below a non-official bridge must retain its independent root identity.");
    }

    private static void LaunchAssociationRequiresFreshRootInsideBoundedWindow()
    {
        var requested = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        Assert(CodexInstanceMonitor.IsEligibleLaunchRoot(new ProcessIdentity(42, requested.AddSeconds(1)), requested, requested.AddSeconds(2)), "A root started after a launch request and observed within the named window must be eligible.");
        Assert(!CodexInstanceMonitor.IsEligibleLaunchRoot(new ProcessIdentity(42, requested.AddSeconds(-1)), requested, requested.AddSeconds(2)), "A pre-existing PID returned by activation must not receive a profile association.");
        Assert(!CodexInstanceMonitor.IsEligibleLaunchRoot(new ProcessIdentity(42, requested.AddSeconds(1)), requested, requested + CodexInstanceMonitor.PendingLaunchAssociationWindow.Add(TimeSpan.FromSeconds(1))), "A root discovered after the pending-launch window expires must not receive a profile association.");
    }

    private void ManagedAssociationPersistenceRejectsPidReuse()
    {
        var root = NewRoot();
        var firstStart = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var store = new ManagedInstanceStore(root);
        store.Save(new[] { new ManagedInstanceAssociation("profile-a", 42, firstStart) });
        var loaded = new ManagedInstanceStore(root).Load().Single();
        Assert(loaded.RootProcessId == 42 && loaded.RootStartedAtUtc == firstStart, "A persisted association must retain PID and start time.");
        Assert(!new ProcessIdentity(loaded.RootProcessId, loaded.RootStartedAtUtc).Matches(42, firstStart.AddSeconds(1)), "A reused PID with a different start time must not match a recovered association.");
    }

    private void InvalidManagedAssociationFileIsIgnored()
    {
        var root = NewRoot();
        var directory = Path.Combine(root, "data");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "managed-instances.json"), "{ invalid json");
        var store = new ManagedInstanceStore(root);
        Assert(store.Load().Count == 0, "Malformed managed-instance persistence must safely degrade to no recovered associations.");
        Assert(!string.IsNullOrWhiteSpace(store.LastWarning), "Malformed managed-instance persistence must expose a non-fatal warning to the UI.");
    }

    private static void EnvironmentBlockUsesExactSortedKeysAndDoubleNul()
    {
        var text = PackageEnvironmentBlock.BuildText(new Dictionary<string, string>
        {
            ["CODEX_SQLITE_HOME"] = "sqlite",
            ["CODEX_HOME"] = "home",
            ["CODEX_ELECTRON_USER_DATA_PATH"] = "electron",
        });
        Assert(text == "CODEX_ELECTRON_USER_DATA_PATH=electron\0CODEX_HOME=home\0CODEX_SQLITE_HOME=sqlite\0\0", "PZZ environment must use the exact sorted Codex keys and a double NUL terminator.");
    }

    private static void ElectronArgumentsQuotePathsContainingSpaces()
    {
        var arguments = WindowsCommandLine.CreateElectronArguments("C:\\Users\\Example User\\Codex Data\\electron-user-data");
        Assert(arguments == "--user-data-dir=\"C:\\Users\\Example User\\Codex Data\\electron-user-data\"", "Electron user-data paths containing spaces must be correctly quoted.");
    }

    private static void ElectronCommandLineParsingAcceptsQuotedAndUnquotedAndRejectsMissing()
    {
        Assert(WindowsCommandLine.TryGetElectronUserDataDirectory("ChatGPT.exe --user-data-dir=\"C:\\Users\\Example User\\Codex\"", out var quoted) && quoted == "C:\\Users\\Example User\\Codex", "Quoted --user-data-dir must be parsed.");
        Assert(WindowsCommandLine.TryGetElectronUserDataDirectory("ChatGPT.exe --user-data-dir C:\\CodexData", out var unquoted) && unquoted == "C:\\CodexData", "Unquoted --user-data-dir must be parsed.");
        Assert(!WindowsCommandLine.TryGetElectronUserDataDirectory("ChatGPT.exe --other=value", out _), "Missing --user-data-dir must not produce an identity.");
        Assert(!WindowsCommandLine.TryGetElectronUserDataDirectory("ChatGPT.exe --user-data-dir=relative", out _), "Relative --user-data-dir values must not be promoted from the launcher's current directory.");
        Assert(!WindowsCommandLine.TryGetElectronUserDataDirectory("ChatGPT.exe --user-data-dir relative", out _), "Relative separate-switch --user-data-dir values must fail closed.");
        Assert(!WindowsCommandLine.TryGetElectronUserDataDirectory("ChatGPT.exe --user-data-dir=C:\\one --user-data-dir=C:\\two", out _), "Conflicting switches in one command line must not produce an identity.");
    }

    private static void ConflictingElectronDirectoriesFailClosed()
    {
        var conflict = CodexInstanceMonitor.ObserveElectronCommandLines(new[] { "ChatGPT.exe --user-data-dir=C:\\one", "ChatGPT.exe --user-data-dir=C:\\two" });
        Assert(conflict.Kind == ElectronDirectoryObservationKind.Conflict && conflict.Directory is null, "A tree with conflicting paths must have no selected directory.");
    }

    private void WorkspaceCatalogMapsManagedAndDefaultPathsExactly()
    {
        var root = NewRoot(); var profile = new Profile(ProfilePaths.CreateProfileId(), "受管");
        var catalog = new WorkspaceCatalog(root); catalog.Reload(new[] { profile });
        var managed = new ProfilePaths(root, profile.Id);
        Assert(catalog.ResolveObservedElectronDirectory(managed.ElectronUserData).Id == profile.Id, "An exact managed Electron path must map to its managed workspace.");
        var defaultPaths = OriginalWorkspacePaths.GetCurrentUserDefaultPair();
        Assert(catalog.ResolveObservedElectronDirectory(defaultPaths.ElectronUserData).Kind == CodexWorkspaceKind.Original, "The exact current-user default Electron path must map to the original workspace.");
        var reloaded = new WorkspaceCatalog(root); reloaded.Reload(new[] { profile });
        Assert(reloaded.Workspaces.Any(workspace => workspace.Kind == CodexWorkspaceKind.Original), "Only the verified default path pair must persist as an original workspace identity.");
        Assert(catalog.ResolveObservedElectronDirectory(Path.Combine(root, "custom")).CodexHome is null, "Unknown external Electron paths must not guess CODEX_HOME.");
    }

    private void WorkspaceCatalogRejectsDuplicatePathOwnership()
    {
        var root = NewRoot(); var id = ProfilePaths.CreateProfileId();
        var catalog = new WorkspaceCatalog(root);
        Throws<InvalidOperationException>(() => catalog.Reload(new[] { new Profile(id, "一"), new Profile(id, "二") }));
    }

    private static void UnknownExternalWorkspaceCannotLaunchOrReadUsage()
    {
        var workspace = new CodexWorkspace("external:C:\\custom", "外部", CodexWorkspaceKind.ExternalUnresolved, "C:\\custom", null);
        Assert(!workspace.IsLaunchable && !workspace.CanReadUsage, "Unknown external workspaces must not be launchable or usage-queryable.");
        Throws<InvalidOperationException>(() => workspace.DataPaths.RequireCodexHome());
    }

    private static void ObservedAssociationResolutionIsPathFirst()
    {
        var association = new ManagedInstanceAssociation("profile-a", 99, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var same = new CodexWorkspace("profile-a", "A", CodexWorkspaceKind.Managed, "C:\\electron-a", "C:\\home-a");
        var different = new CodexWorkspace("profile-b", "B", CodexWorkspaceKind.Managed, "C:\\electron-b", "C:\\home-b");
        var original = new CodexWorkspace("original", "原始", CodexWorkspaceKind.Original, "C:\\electron-original", "C:\\home-original");
        Assert(CodexInstanceMonitor.ResolveObservedAssociation(same, association) == ManagedAssociationResolution.Keep, "A matching observed managed path must retain its association.");
        Assert(CodexInstanceMonitor.ResolveObservedAssociation(different, association) == ManagedAssociationResolution.Remove && CodexInstanceMonitor.ResolveObservedAssociation(original, association) == ManagedAssociationResolution.Remove, "Observed mismatched or non-managed paths must remove stale PID associations.");
    }

    private static void UsageParserHandlesOfficialNestedShape()
    {
        using var account = System.Text.Json.JsonDocument.Parse("{\"id\":2,\"result\":{\"account\":{\"type\":\"chatgpt\",\"email\":\"person@example.test\",\"planType\":\"plus\"}}}");
        using var limits = System.Text.Json.JsonDocument.Parse("{\"id\":3,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":25.5,\"resetsAt\":1760000000},\"secondary\":{\"usedPercent\":\"50\",\"resetsAt\":\"2026-01-01T00:00:00Z\"},\"credits\":{\"hasCredits\":true,\"unlimited\":false,\"balance\":\"12.75\"}},\"rateLimitResetCredits\":{\"availableCount\":3}}}");
        using var usage = System.Text.Json.JsonDocument.Parse("{\"id\":4,\"result\":{\"summary\":{\"lifetimeTokens\":123456}}}");
        var snapshot = CodexUsageResponseParser.Parse(new[] { account, limits, usage }, DateTimeOffset.UtcNow);
        Assert(snapshot.State == CodexUsageState.Available && snapshot.AccountType == "chatgpt" && snapshot.Email == "person@example.test" && snapshot.Plan == "plus", "Nested account fields must parse from account/read.");
        Assert(snapshot.PrimaryUsedPercent == 25.5m && snapshot.SecondaryUsedPercent == 50m && snapshot.WorkspaceHasCredits == true && snapshot.WorkspaceCredits == 12.75m && snapshot.RateLimitResetCredits == 3m && snapshot.LifetimeTokens == 123456m, "Nested rate-limit numeric and string balances must parse exactly.");
        using var unlimited = System.Text.Json.JsonDocument.Parse("{\"result\":{\"rateLimits\":{\"credits\":{\"hasCredits\":true,\"unlimited\":true,\"balance\":\"99\"}}}}");
        var unlimitedSnapshot = CodexUsageResponseParser.Parse(new[] { unlimited }, DateTimeOffset.UtcNow);
        Assert(unlimitedSnapshot.WorkspaceHasCredits == true && unlimitedSnapshot.WorkspaceCreditsUnlimited == true && unlimitedSnapshot.WorkspaceCredits is null, "Unlimited credits must be explicit and must not invent a numeric balance.");
        using var disabled = System.Text.Json.JsonDocument.Parse("{\"result\":{\"rateLimits\":{\"credits\":{\"hasCredits\":false,\"unlimited\":false,\"balance\":\"99\"}}}}");
        var disabledSnapshot = CodexUsageResponseParser.Parse(new[] { disabled }, DateTimeOffset.UtcNow);
        Assert(disabledSnapshot.WorkspaceHasCredits == false && disabledSnapshot.WorkspaceCreditsUnlimited == false && disabledSnapshot.WorkspaceCredits is null, "Disabled credits must remain distinguishable from unavailable or unlimited credits.");
    }

    private static void UsageParserPreservesMissingAndNullValues()
    {
        using var response = System.Text.Json.JsonDocument.Parse("{\"result\":{\"account\":null,\"rateLimits\":{\"primary\":{\"usedPercent\":null},\"credits\":{\"hasCredits\":null,\"unlimited\":null,\"balance\":null}}}}");
        var snapshot = CodexUsageResponseParser.Parse(new[] { response }, DateTimeOffset.UtcNow);
        Assert(snapshot.State == CodexUsageState.Unauthenticated && snapshot.Email is null && snapshot.PrimaryUsedPercent is null && snapshot.WorkspaceCredits is null && snapshot.WorkspaceHasCredits is null && snapshot.WorkspaceCreditsUnlimited is null, "Missing and null server values must remain unavailable rather than becoming zero.");
    }

    private static void DetailedUsageStageRequiresAuthenticatedAccount()
    {
        using var unauthenticatedResponse = System.Text.Json.JsonDocument.Parse("{\"result\":{\"account\":null}}");
        using var authenticatedResponse = System.Text.Json.JsonDocument.Parse("{\"result\":{\"account\":{\"type\":\"chatgpt\",\"email\":\"person@example.test\",\"planType\":\"plus\"}}}");
        var unauthenticated = CodexUsageResponseParser.Parse(new[] { unauthenticatedResponse }, DateTimeOffset.UtcNow);
        var authenticated = CodexUsageResponseParser.Parse(new[] { authenticatedResponse }, DateTimeOffset.UtcNow);
        Assert(!CodexAppServerUsageClient.ShouldReadDetailedUsage(unauthenticated) && CodexAppServerUsageClient.ShouldReadDetailedUsage(authenticated), "Only an authenticated account/read snapshot may enter the detailed usage stage.");
    }

    private static void AppServerUsesOnlyReadMethodContract()
    {
        var initialize = CodexAppServerUsageClient.CreateInitializeRequestJson(1); var account = CodexAppServerUsageClient.CreateAccountReadRequestJson(2); var limits = CodexAppServerUsageClient.CreateRequestJson(3, "account/rateLimits/read", null); var usage = CodexAppServerUsageClient.CreateRequestJson(4, "account/usage/read", null);
        var initialized = CodexAppServerUsageClient.CreateInitializedNotificationJson();
        Assert(initialize.Contains("\"title\":\"Codex Multi Launcher\"", StringComparison.Ordinal) && account.Contains("\"refreshToken\":false", StringComparison.Ordinal) && !limits.Contains("\"params\"", StringComparison.Ordinal) && !usage.Contains("\"params\"", StringComparison.Ordinal) && initialized.Contains("\"method\":\"initialized\"", StringComparison.Ordinal) && initialized.Contains("\"params\":{}", StringComparison.Ordinal), "The app-server request shapes must use the read-only official contract.");
    }

    private static void CandidateSelectionPrefersDirectThenNewestVersion()
    {
        var older = new CodexExecutableCandidate("C:\\bin\\1\\codex.exe", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), false); var newer = new CodexExecutableCandidate("C:\\bin\\2\\codex.exe", DateTimeOffset.Parse("2026-02-01T00:00:00Z"), false); var direct = new CodexExecutableCandidate("C:\\bin\\codex.exe", DateTimeOffset.Parse("2025-01-01T00:00:00Z"), true);
        Assert(CodexExecutableLocator.SelectCurrentCandidate(new[] { older, newer, direct }) == direct && CodexExecutableLocator.SelectCurrentCandidate(new[] { older, newer }) == newer, "CLI candidate selection must prefer the direct binary, then the newest version directory candidate.");
    }

    private static void WorkspaceSelectionPreservesIdentity()
    {
        var workspaces = new[] { new CodexWorkspace("a", "A", CodexWorkspaceKind.Managed, "C:\\a", "C:\\ha"), new CodexWorkspace("external:C:\\b", "B", CodexWorkspaceKind.ExternalUnresolved, "C:\\b", null) };
        Assert(MainWindow.SelectWorkspaceById(workspaces, "external:C:\\b")?.Id == "external:C:\\b" && MainWindow.SelectWorkspaceById(workspaces, "missing") is null, "Workspace replacement must preserve a selected known workspace ID and reject unknown IDs.");
    }

    private static void DebuggerCommandLineQuotesTheActualExecutablePath()
    {
        var commandLine = WindowsCommandLine.CreateDebuggerCommandLine("C:\\Program Files\\Codex Multi Launcher\\CodexMultiLauncher.exe");
        Assert(commandLine == "\"C:\\Program Files\\Codex Multi Launcher\\CodexMultiLauncher.exe\" --resume-packaged-app", "The dummy debugger command must quote the launcher executable path.");
    }

    private static void ResumeArgumentParsingAcceptsOnlyTheDocumentedShape()
    {
        Assert(PackagedAppResume.TryParse(new[] { "--resume-packaged-app", "-p", "123", "-tid", "456" }, out var processId, out var threadId) && processId == 123 && threadId == 456, "Documented debugger arguments must parse.");
        Assert(!PackagedAppResume.TryParse(new[] { "--resume-packaged-app", "-p", "0", "-tid", "456" }, out _, out _), "A zero process ID must be rejected.");
        Assert(!PackagedAppResume.TryParse(new[] { "--resume-packaged-app", "-p", "123", "-tid", "0" }, out _, out _), "A zero thread ID must be rejected.");
        Assert(!PackagedAppResume.TryParse(new[] { "--resume-packaged-app", "-tid", "456", "-p", "123" }, out _, out _), "Reordered debugger arguments must be rejected.");
        Assert(!PackagedAppResume.TryParse(new[] { "--resume-packaged-app", "-p", "123", "-tid" }, out _, out _), "Incomplete debugger arguments must be rejected.");
    }

    private static void RuntimeFormattingHandlesByteAndDurationEdges()
    {
        Assert(RuntimeFormatting.FormatBytes(-1) == RuntimeFormatting.UnavailableText, "Negative byte counts must be unavailable.");
        Assert(RuntimeFormatting.FormatBytes(0) == "0 B", "Zero bytes must be formatted deterministically.");
        Assert(RuntimeFormatting.FormatBytes(1023) == "1023 B", "Bytes below one KiB must retain byte units.");
        Assert(RuntimeFormatting.FormatBytes(1024) == "1.0 KiB", "One KiB must use the binary KiB unit.");
        Assert(RuntimeFormatting.FormatBytes(1536) == "1.5 KiB", "Byte formatting must use one deterministic decimal place.");
        Assert(RuntimeFormatting.FormatDuration(TimeSpan.FromSeconds(-1)) == "00:00:00", "Negative durations must clamp to zero.");
        Assert(RuntimeFormatting.FormatDuration(new TimeSpan(1, 2, 3, 4)) == "1.02:03:04", "Durations of one day or more must include days.");
        Assert(RuntimeFormatting.FormatDuration(new TimeSpan(2, 3, 4)) == "02:03:04", "Sub-day durations must retain zero-padded hours.");
    }

    private void ProfileStorageInspectorCountsOnlyManagedRoots()
    {
        var paths = new ProfilePaths(NewRoot(), ProfilePaths.CreateProfileId());
        Directory.CreateDirectory(Path.Combine(paths.CodexHome, "nested"));
        Directory.CreateDirectory(paths.ElectronUserData);
        Directory.CreateDirectory(paths.PrimaryRuntimeInstallRoot);
        File.WriteAllBytes(Path.Combine(paths.CodexHome, "nested", "codex.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(paths.ElectronUserData, "electron.bin"), [4, 5, 6, 7, 8]);
        File.WriteAllBytes(Path.Combine(paths.PrimaryRuntimeInstallRoot, "runtime.bin"), new byte[7]);
        File.WriteAllBytes(Path.Combine(paths.ProfileDirectory, "outside-managed-roots.bin"), new byte[11]);

        var snapshot = ProfileStorageInspector.Inspect(paths);
        Assert(snapshot.CodexHome.Bytes == 3 && snapshot.CodexHome.FileCount == 1, "Storage inspection must count CodexHome files separately.");
        Assert(snapshot.ElectronUserData.Bytes == 5 && snapshot.ElectronUserData.FileCount == 1, "Storage inspection must count Electron user-data files separately.");
        Assert(snapshot.RuntimeUserHome.Bytes == 7 && snapshot.RuntimeUserHome.FileCount == 1, "Storage inspection must count the runtime home separately, including its nested cache.");
        Assert(snapshot.TotalBytes == 15, "Storage inspection must include all three managed roots and exclude adjacent files.");
        Assert(snapshot.SkippedEntries == 0, "The deterministic fixture contains no skipped entries.");
        Assert(snapshot.MeasuredAt <= DateTimeOffset.UtcNow, "Storage inspection must publish its measurement timestamp.");
    }

    private void CodexInstanceMonitorDisposalNeverStopsProcesses()
    {
        using (var monitor = new CodexInstanceMonitor(NewRoot())) { monitor.Dispose(); }
        using var currentProcess = System.Diagnostics.Process.GetProcessById(Environment.ProcessId);
        Assert(!currentProcess.HasExited, "Disposing a monitor must not stop the tracked process.");
    }

    private string NewRoot()
    {
        var root = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private void DeleteTemporaryRootSafely()
    {
        if (!Directory.Exists(_temporaryRoot)) return;
        var temporaryBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var target = Path.GetFullPath(_temporaryRoot);
        var relative = Path.GetRelativePath(temporaryBase, target);
        if (Path.IsPathRooted(relative) || relative == "." || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException("Contract tests refuse to delete outside the temporary directory.");
        Directory.Delete(target, recursive: true);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }
}
