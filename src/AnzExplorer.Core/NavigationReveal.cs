namespace AnzExplorer.Core;

/// <summary>
/// Chooses which item to re-select after navigating back or up, so the user can see
/// where they came from (File Explorer behavior).
/// </summary>
public static class NavigationReveal
{
    /// <summary>
    /// Returns the path among <paramref name="candidatePaths"/> that is, or contains,
    /// <paramref name="previousPath"/>. Going back from <c>C:\a\b\c</c> to <c>C:\a</c>
    /// reveals <c>C:\a\b</c>. Returns null when the previous location is not inside
    /// the new folder (for example, going back to an unrelated folder).
    /// </summary>
    public static string? FindItemToReveal(string? previousPath, IEnumerable<string> candidatePaths)
    {
        if (string.IsNullOrWhiteSpace(previousPath) || !Path.IsPathFullyQualified(previousPath))
        {
            return null;
        }

        string previous;
        try
        {
            previous = PathCanonicalizer.Canonicalize(previousPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        string? best = null;
        foreach (var candidate in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) continue;
            try
            {
                if (!PathCanonicalizer.IsSameOrDescendant(previous, candidate)) continue;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            // Prefer the deepest match (an exact hit beats a containing drive or folder).
            if (best is null || candidate.Length > best.Length) best = candidate;
        }

        return best;
    }
}
