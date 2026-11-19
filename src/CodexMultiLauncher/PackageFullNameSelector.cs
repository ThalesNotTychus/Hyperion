namespace CodexMultiLauncher;

public sealed record CodexPackage(string FullName, Version Version);

public static class PackageFullNameSelector
{
    public const string PackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";
    public const string PackageName = "OpenAI.Codex";
    public const string PublisherId = "2p2nqsd0c76g0";
    public const string RequiredArchitecture = "x64";

    public static CodexPackage SelectOrThrow(IEnumerable<string> packageFullNames)
    {
        ArgumentNullException.ThrowIfNull(packageFullNames);
        var candidates = packageFullNames
            .Select(TryParse)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderByDescending(candidate => candidate.Version)
            .ThenBy(candidate => candidate.FullName, StringComparer.Ordinal)
            .ToList();

        return candidates.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "未找到可用的官方 Codex x64 主包。请从 Microsoft Store 安装或更新官方 Codex Windows 客户端。");
    }

    public static CodexPackage? TryParse(string? packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName)) return null;

        var parts = packageFullName.Split('_');
        if (parts.Length != 5
            || !string.Equals(parts[0], PackageName, StringComparison.Ordinal)
            || !string.Equals(parts[2], RequiredArchitecture, StringComparison.OrdinalIgnoreCase)
            || parts[3].Length != 0
            || !string.Equals(parts[4], PublisherId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals($"{parts[0]}_{parts[4]}", PackageFamilyName, StringComparison.OrdinalIgnoreCase)
            || !TryParseVersion(parts[1], out var version))
        {
            return null;
        }

        return new CodexPackage(packageFullName, version);
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        version = new Version();
        var components = value.Split('.');
        if (components.Length != 4 || components.Any(component => !ushort.TryParse(component, out _))) return false;
        if (!Version.TryParse(value, out var parsed) || parsed is null) return false;
        version = parsed;
        return true;
    }
}
