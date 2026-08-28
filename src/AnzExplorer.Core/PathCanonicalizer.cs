namespace AnzExplorer.Core;

public static class PathCanonicalizer
{
    public static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        var fullPath = Path.GetFullPath(expanded);

        if (fullPath.Length > Path.GetPathRoot(fullPath)?.Length)
        {
            fullPath = Path.TrimEndingDirectorySeparator(fullPath);
        }

        return fullPath;
    }

    public static bool IsSameOrDescendant(string candidate, string root)
    {
        var canonicalCandidate = Canonicalize(candidate);
        var canonicalRoot = Canonicalize(root);
        if (string.Equals(canonicalCandidate, canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = Path.EndsInDirectorySeparator(canonicalRoot)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        return canonicalCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}

