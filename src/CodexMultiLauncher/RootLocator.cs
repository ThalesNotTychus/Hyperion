namespace CodexMultiLauncher;

public static class RootLocator
{
    public const string MarkerFileName = "CodexMultiLauncher.root";

    public static string FindProjectRoot(string? baseDirectory = null)
    {
        var start = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory));
        var current = new DirectoryInfo(start);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, MarkerFileName)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return start;
    }
}
