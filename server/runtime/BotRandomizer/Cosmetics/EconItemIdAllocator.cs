namespace BotRandomizer;

internal static class EconItemIdAllocator
{
    private const ulong MinimumCustomItemId = 65155030971;
    private static ulong _nextItemId = MinimumCustomItemId;

    internal static ulong Next()
        => unchecked(_nextItemId++);

    internal static bool IsAllocated(ulong itemId)
        => itemId >= MinimumCustomItemId && itemId < _nextItemId;
}
