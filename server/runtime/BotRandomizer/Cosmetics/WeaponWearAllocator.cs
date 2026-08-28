using System.Globalization;

namespace BotRandomizer;

internal sealed class WeaponWearAllocator
{
    private const int WearScale = 1000;
    private readonly Dictionary<(int PaintKit, int WearTick), (ushort DefIndex, string Stickers)> _cache = new();

    internal float Reserve(
        ushort defIndex,
        PaintCatalogEntry paint,
        IReadOnlyList<StickerSelection> stickers)
    {
        var minimumTick = Math.Clamp((int)Math.Ceiling(paint.WearMin * WearScale), 0, WearScale);
        var maximumTick = Math.Clamp((int)Math.Floor(paint.WearMax * WearScale), 0, WearScale);
        if (minimumTick > maximumTick)
            throw new InvalidOperationException($"Paint {paint.PaintKit} has no representable wear tick.");

        var preferredTick = Math.Clamp(10, minimumTick, maximumTick);
        var signature = BuildStickerSignature(stickers);
        for (var tick = preferredTick; tick <= maximumTick; tick++)
        {
            if (TryReserve(defIndex, paint.PaintKit, tick, signature))
                return tick / (float)WearScale;
        }

        for (var tick = preferredTick - 1; tick >= minimumTick; tick--)
        {
            if (TryReserve(defIndex, paint.PaintKit, tick, signature))
                return tick / (float)WearScale;
        }

        throw new InvalidOperationException($"Wear cache exhausted for paint kit {paint.PaintKit}.");
    }

    internal void Reset() => _cache.Clear();

    private bool TryReserve(ushort defIndex, int paintKit, int wearTick, string signature)
    {
        var key = (paintKit, wearTick);
        if (_cache.TryGetValue(key, out var cached)
            && (cached.DefIndex != defIndex || cached.Stickers != signature))
        {
            return false;
        }

        _cache[key] = (defIndex, signature);
        return true;
    }

    private static string BuildStickerSignature(IReadOnlyList<StickerSelection> stickers)
        => string.Join(
            "_",
            stickers
                .OrderBy(sticker => sticker.Slot)
                .Select(sticker => string.Join(
                    ":",
                    sticker.Slot.ToString(CultureInfo.InvariantCulture),
                    sticker.DefIndex.ToString(CultureInfo.InvariantCulture),
                    sticker.Schema.ToString(CultureInfo.InvariantCulture),
                    sticker.Wear.ToString("R", CultureInfo.InvariantCulture),
                    sticker.Rotation?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                    sticker.X?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                    sticker.Y?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty)));
}
