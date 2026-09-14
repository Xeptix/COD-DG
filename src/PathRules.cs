namespace CODDowngrader;

/// <summary>How paths compare on this OS: case-insensitively on Windows, exactly elsewhere.</summary>
public static class PathRules
{
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Full path without a trailing separator, except for a drive or filesystem root.</summary>
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool Same(string a, string b) => Comparer.Equals(Normalize(a), Normalize(b));

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or anywhere below it.</summary>
    public static bool IsInside(string path, string folder)
    {
        var p = WithSeparator(Path.GetFullPath(path));
        var f = WithSeparator(Path.GetFullPath(folder));
        return p.StartsWith(f, Comparison);
    }

    static string WithSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}
