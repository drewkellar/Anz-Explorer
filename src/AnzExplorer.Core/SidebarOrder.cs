namespace AnzExplorer.Core;

public static class SidebarOrder
{
    public static IEnumerable<Guid> GetSavedAndNew(IReadOnlyList<Guid> saved, IEnumerable<Guid> visible) =>
        saved.Union(visible);

    public static Guid[] ApplyVisibleOrder(IReadOnlyList<Guid> saved, IReadOnlyList<Guid> visibleOrder)
    {
        var visible = visibleOrder.ToHashSet();
        var merged = GetSavedAndNew(saved, visibleOrder).ToArray();
        var nextVisible = 0;
        for (var index = 0; index < merged.Length; index++)
        {
            if (visible.Contains(merged[index])) merged[index] = visibleOrder[nextVisible++];
        }
        return merged;
    }

    public static bool TryMove<T>(List<T> items, T source, T target, bool after)
    {
        var sourceIndex = items.IndexOf(source);
        var targetIndex = items.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex) return false;

        var insertionIndex = targetIndex + (after ? 1 : 0);
        if (sourceIndex < insertionIndex) insertionIndex--;
        if (insertionIndex == sourceIndex) return false;

        items.RemoveAt(sourceIndex);
        items.Insert(insertionIndex, source);
        return true;
    }
}
