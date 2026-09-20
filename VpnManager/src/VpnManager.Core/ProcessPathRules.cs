namespace VpnManager.Core;

public static class ProcessPathRules
{
    public static bool IsWithinDirectory(string path, string directory)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(directory)) return false;
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length <= (Path.GetPathRoot(root)?.TrimEnd('\\', '/').Length ?? 0)) return false;
        return Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
