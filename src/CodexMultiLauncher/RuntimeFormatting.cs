using System.Globalization;

namespace CodexMultiLauncher;

public static class RuntimeFormatting
{
    public const string UnavailableText = "不可用";

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return UnavailableText;

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        decimal value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes.ToString(CultureInfo.InvariantCulture)} {units[unitIndex]}"
            : $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return duration.Days > 0
            ? duration.Days.ToString(CultureInfo.InvariantCulture) + "." + duration.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture)
            : duration.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
    }
}
