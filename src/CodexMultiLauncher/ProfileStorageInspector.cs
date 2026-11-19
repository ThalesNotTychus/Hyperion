namespace CodexMultiLauncher;

public sealed record ProfileStorageAreaSnapshot(long Bytes, long FileCount);

public sealed record ProfileStorageSnapshot(
    ProfileStorageAreaSnapshot CodexHome,
    ProfileStorageAreaSnapshot ElectronUserData,
    ProfileStorageAreaSnapshot RuntimeUserHome,
    long TotalBytes,
    long SkippedEntries,
    DateTimeOffset MeasuredAt);

public static class ProfileStorageInspector
{
    public static Task<ProfileStorageSnapshot> InspectAsync(ProfilePaths paths, CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(paths, cancellationToken), cancellationToken);

    public static Task<ProfileStorageSnapshot> InspectAsync(ProfilePaths paths, ClientKind client, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (client == ClientKind.Codex) return Inspect(paths, cancellationToken);
            var home = client switch
            {
                ClientKind.Claude => ValidateManagedRoot(paths.ProfileDirectory, paths.ClaudeHome, "claude-home"),
                ClientKind.Antigravity => ValidateManagedRoot(paths.ProfileDirectory, paths.AntigravityHome, "antigravity-home"),
                _ => throw new InvalidOperationException("客户端类型无效。"),
            };
            var electron = ValidateManagedRoot(paths.ProfileDirectory, paths.ElectronUserData, "electron-user-data");
            var skipped = 0L;
            var homeSnapshot = Scan(home, ref skipped, cancellationToken);
            var electronSnapshot = Scan(electron, ref skipped, cancellationToken);
            return new ProfileStorageSnapshot(homeSnapshot, electronSnapshot, new(0, 0),
                checked(homeSnapshot.Bytes + electronSnapshot.Bytes), skipped, DateTimeOffset.UtcNow);
        }, cancellationToken);

    public static ProfileStorageSnapshot Inspect(ProfilePaths paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var codexHome = ValidateManagedRoot(paths.ProfileDirectory, paths.CodexHome, "codex-home");
        var electronUserData = ValidateManagedRoot(paths.ProfileDirectory, paths.ElectronUserData, "electron-user-data");
        var runtimeUserHome = ValidateManagedRoot(paths.ProfileDirectory, paths.RuntimeUserHome, "runtime-user-home");
        var skippedEntries = 0L;
        var codexHomeSnapshot = Scan(codexHome, ref skippedEntries, cancellationToken);
        var electronSnapshot = Scan(electronUserData, ref skippedEntries, cancellationToken);
        var runtimeSnapshot = Scan(runtimeUserHome, ref skippedEntries, cancellationToken);
        return new ProfileStorageSnapshot(
            codexHomeSnapshot,
            electronSnapshot,
            runtimeSnapshot,
            checked(codexHomeSnapshot.Bytes + electronSnapshot.Bytes + runtimeSnapshot.Bytes),
            skippedEntries,
            DateTimeOffset.UtcNow);
    }

    private static string ValidateManagedRoot(string profileDirectory, string root, string expectedDirectoryName)
    {
        var expected = ProfilePathSafety.GetContainedFullPath(profileDirectory, expectedDirectoryName);
        var actual = Path.GetFullPath(root);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("账号存储扫描拒绝访问受管理目录之外的路径。");
        return actual;
    }

    private static ProfileStorageAreaSnapshot Scan(string root, ref long skippedEntries, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            try
            {
                File.GetAttributes(root);
                return new ProfileStorageAreaSnapshot(0, 0);
            }
            catch (DirectoryNotFoundException)
            {
                return new ProfileStorageAreaSnapshot(0, 0);
            }
            catch (Exception exception) when (IsSkippable(exception))
            {
                skippedEntries++;
                return new ProfileStorageAreaSnapshot(0, 0);
            }
        }

        var bytes = 0L;
        var files = 0L;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (IsReparsePoint(directory, ref skippedEntries)) continue;

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception exception) when (IsSkippable(exception))
            {
                skippedEntries++;
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skippedEntries++;
                    }
                    else if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                    else
                    {
                        bytes = checked(bytes + new FileInfo(entry).Length);
                        files++;
                    }
                }
                catch (Exception exception) when (IsSkippable(exception) || exception is OverflowException)
                {
                    skippedEntries++;
                }
            }
        }

        return new ProfileStorageAreaSnapshot(bytes, files);
    }

    private static bool IsReparsePoint(string path, ref long skippedEntries)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return false;
            skippedEntries++;
            return true;
        }
        catch (Exception exception) when (IsSkippable(exception))
        {
            skippedEntries++;
            return true;
        }
    }

    private static bool IsSkippable(Exception exception) => exception is UnauthorizedAccessException or IOException or System.Security.SecurityException;
}
