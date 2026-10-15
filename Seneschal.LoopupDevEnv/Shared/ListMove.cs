namespace DevEnvironment.Shared;

internal static class ListMove
{
    /// <summary>Moves the first item that matches <paramref name="isDragged"/> to just before or just after the one that matches <paramref name="isTarget"/>. Returns false when nothing changed.</summary>
    public static bool Move<T>(List<T> list, Func<T, bool> isDragged, Func<T, bool> isTarget, bool after)
    {
        var from = list.FindIndex(i => isDragged(i));
        var to = list.FindIndex(i => isTarget(i));
        if (from < 0 || to < 0 || from == to) return false;

        var item = list[from];
        list.RemoveAt(from);
        // Taking the item out shifts everything after it up by one
        var index = list.FindIndex(i => isTarget(i));
        list.Insert(after ? index + 1 : index, item);
        return true;
    }
}
