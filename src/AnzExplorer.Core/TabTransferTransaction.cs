namespace AnzExplorer.Core;

public readonly record struct TabTransferStep<T>(T Tab, int OriginalIndex);

public static class TabTransferTransaction
{
    public static bool Execute<T>(
        IReadOnlyList<TabTransferStep<T>> steps,
        Func<TabTransferStep<T>, bool> move,
        Action<TabTransferStep<T>> rollback)
    {
        var moved = new List<TabTransferStep<T>>(steps.Count);
        try
        {
            foreach (var step in steps)
            {
                if (!move(step))
                {
                    RollBack(moved, rollback);
                    return false;
                }
                moved.Add(step);
            }
            return true;
        }
        catch
        {
            RollBack(moved, rollback);
            return false;
        }
    }

    public static int ClampDropIndex(int requestedIndex, int tabCount) =>
        Math.Clamp(requestedIndex, 0, Math.Max(0, tabCount));

    public static bool ShouldCloseSource(int remainingTabCount) => remainingTabCount == 0;

    private static void RollBack<T>(
        IReadOnlyList<TabTransferStep<T>> moved,
        Action<TabTransferStep<T>> rollback)
    {
        for (var index = moved.Count - 1; index >= 0; index--)
        {
            try
            {
                rollback(moved[index]);
            }
            catch
            {
                // Continue restoring the rest of the batch after an individual rollback failure.
            }
        }
    }
}
