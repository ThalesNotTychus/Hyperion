namespace CodexMultiLauncher;

public static class ProfilePathSafety
{
    /// <summary>Reject existing junctions/symlinks in any ancestor before managing a path.</summary>
    public static void ThrowIfReparsePath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"受管路径包含链接或联接，拒绝访问：{current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    public static string GetContainedFullPath(string rootDirectory, params string[] segments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(segments);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var candidate = Path.GetFullPath(Path.Combine(new[] { root }.Concat(segments).ToArray()));
        var relative = Path.GetRelativePath(root, candidate);

        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("派生的数据路径超出了受管理的 profiles 目录。");
        }

        return candidate;
    }
}
