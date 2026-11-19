namespace CodexMultiLauncher;

public static class WindowsCommandLine
{
    public static string QuoteArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0) return "\"\"";

        var builder = new System.Text.StringBuilder(value.Length + 2);
        builder.Append('"');
        var slashCount = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', slashCount * 2 + 1);
                builder.Append(character);
                slashCount = 0;
                continue;
            }

            builder.Append('\\', slashCount);
            slashCount = 0;
            builder.Append(character);
        }

        builder.Append('\\', slashCount * 2);
        builder.Append('"');
        return builder.ToString();
    }

    public static string CreateDebuggerCommandLine(string executablePath) =>
        $"{QuoteArgument(Path.GetFullPath(executablePath))} {PackagedAppResume.ResumeArgument}";

    public static string CreateElectronArguments(string electronUserDataPath) =>
        $"--user-data-dir={QuoteArgument(Path.GetFullPath(electronUserDataPath))}";

    /// <summary>Reads only the documented Electron switch; supports --name=value and --name value forms.</summary>
    public static bool TryGetElectronUserDataDirectory(string? commandLine, out string directory)
    {
        directory = string.Empty;
        var values = GetElectronUserDataDirectories(commandLine);
        if (values.Count == 0 || values.Any(value => value is null)) return false;
        var distinct = values.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinct.Length != 1) return false;
        directory = distinct[0]; return true;
    }

    /// <summary>Returns every documented switch occurrence; a null item is a malformed path.</summary>
    public static IReadOnlyList<string?> GetElectronUserDataDirectories(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var arguments = Split(commandLine);
        var values = new List<string?>();
        for (var index = 0; index < arguments.Count; index++)
        {
            const string prefix = "--user-data-dir=";
            if (arguments[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = arguments[index][prefix.Length..];
                values.Add(PathIdentity.TryCanonicalize(value, out var canonical) ? canonical : null);
            }
            else if (string.Equals(arguments[index], "--user-data-dir", StringComparison.OrdinalIgnoreCase))
            {
                values.Add(index + 1 < arguments.Count && PathIdentity.TryCanonicalize(arguments[++index], out var nextCanonical) ? nextCanonical : null);
            }
        }
        return values;
    }

    // CommandLineToArgvW-compatible enough for extracting a single switch. It preserves
    // Windows backslash-before-quote rules and never invokes a shell.
    private static IReadOnlyList<string> Split(string commandLine)
    {
        var result = new List<string>();
        var index = 0;
        while (index < commandLine.Length)
        {
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index])) index++;
            if (index == commandLine.Length) break;
            var value = new System.Text.StringBuilder(); var quoted = false;
            while (index < commandLine.Length)
            {
                var slashes = 0;
                while (index < commandLine.Length && commandLine[index] == '\\') { slashes++; index++; }
                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    value.Append('\\', slashes / 2);
                    if (slashes % 2 == 0) quoted = !quoted; else value.Append('"');
                    index++; continue;
                }
                value.Append('\\', slashes);
                if (index == commandLine.Length || (!quoted && char.IsWhiteSpace(commandLine[index]))) break;
                value.Append(commandLine[index++]);
            }
            result.Add(value.ToString());
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index])) index++;
        }
        return result;
    }
}
