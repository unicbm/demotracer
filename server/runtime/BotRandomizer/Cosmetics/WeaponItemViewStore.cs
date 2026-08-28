using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Microsoft.Extensions.Logging;

namespace BotRandomizer;

internal sealed class WeaponItemViewStore : IDisposable
{
    private const ulong SteamId64AccountBase = 76_561_197_960_265_728;

    private readonly MemoryFunctionWithReturn<nint, nint>? _constructor;
    private readonly MemoryFunctionWithReturn<nint, string, float, int>? _setAttributeByName;
    private readonly ILogger _logger;
    private readonly Dictionary<(int Slot, int UserId, ushort DefIndex), nint> _views = [];
    private bool _errorLogged;
    private bool _disposed;

    internal WeaponItemViewStore(
        MemoryFunctionWithReturn<nint, nint>? constructor,
        MemoryFunctionWithReturn<nint, string, float, int>? setAttributeByName,
        ILogger logger)
    {
        _constructor = constructor;
        _setAttributeByName = setAttributeByName;
        _logger = logger;
    }

    internal bool NativeAvailable => _constructor is not null && _setAttributeByName is not null;

    internal bool TryPrepareKnife(
        SlotCosmeticState state,
        KnifeSelection selection,
        ulong steamId,
        out nint itemViewHandle)
    {
        itemViewHandle = nint.Zero;
        if (_disposed || _constructor is null || _setAttributeByName is null)
            return false;

        try
        {
            if (!TryGetOrCreateItemView(state, selection.DefIndex, out itemViewHandle))
                return false;

            var item = new CEconItemView(itemViewHandle);
            var networkedAttributes = item.NetworkedDynamicAttributes;
            var attributeList = item.AttributeList;
            if (networkedAttributes.Handle == nint.Zero || attributeList.Handle == nint.Zero)
                return false;

            item.Initialized = true;
            item.ItemDefinitionIndex = selection.DefIndex;
            AssignItemId(item);
            item.AccountID = AccountIdFromSteamId(steamId);
            item.EntityQuality = 3;

            networkedAttributes.Attributes.RemoveAll();
            attributeList.Attributes.RemoveAll();
            SetTextureAttributes(networkedAttributes, selection.PaintKit, 0, selection.Wear);
            SetTextureAttributes(attributeList, selection.PaintKit, 0, selection.Wear);
            return true;
        }
        catch (Exception exception)
        {
            LogPreparationError(exception);
            itemViewHandle = nint.Zero;
            return false;
        }
    }

    internal bool TryPrepareReplayKnife(
        SlotCosmeticState state,
        ReplayItemSelection selection,
        ulong steamId,
        out nint itemViewHandle)
        => TryPrepareReplayItem(state, selection, steamId, defaultQuality: 3, out itemViewHandle);

    internal bool TryPrepareReplayWeapon(
        SlotCosmeticState state,
        ReplayWeaponSelection selection,
        ulong steamId,
        out nint itemViewHandle)
    {
        if (!TryPrepareReplayItem(
                state,
                new ReplayItemSelection(
                    selection.DefIndex,
                    selection.PaintKit,
                    selection.Seed,
                    selection.Wear,
                    selection.Identity),
                steamId,
                defaultQuality: 4,
                out itemViewHandle))
            return false;

        try
        {
            var item = new CEconItemView(itemViewHandle);
            var networked = item.NetworkedDynamicAttributes;
            var attributes = item.AttributeList;
            foreach (var sticker in selection.Stickers)
            {
                SetStickerAttributes(networked, sticker);
                SetStickerAttributes(attributes, sticker);
            }
            foreach (var keychain in selection.Keychains)
            {
                SetKeychainAttributes(networked, keychain);
                SetKeychainAttributes(attributes, keychain);
            }
            return true;
        }
        catch (Exception exception)
        {
            LogPreparationError(exception);
            itemViewHandle = nint.Zero;
            return false;
        }
    }

    internal bool TryPrepare(
        SlotCosmeticState state,
        WeaponCatalogEntry weapon,
        WeaponCosmeticSelection? selection,
        bool includeRandomPaint,
        bool includeStickers,
        bool includeKeychain,
        int stickerSchemaCount,
        ulong steamId,
        out nint itemViewHandle)
    {
        itemViewHandle = nint.Zero;
        if (_disposed || _constructor is null || _setAttributeByName is null)
            return false;

        try
        {
            if (!TryGetOrCreateItemView(state, weapon.DefIndex, out itemViewHandle))
                return false;

            var item = new CEconItemView(itemViewHandle);
            var attributes = item.NetworkedDynamicAttributes;
            if (attributes.Handle == nint.Zero)
                return false;

            item.Initialized = true;
            item.ItemDefinitionIndex = weapon.DefIndex;
            AssignItemId(item);
            item.AccountID = AccountIdFromSteamId(steamId);
            item.EntityQuality = 4;

            attributes.Attributes.RemoveAll();
            if (includeRandomPaint && selection is not null)
            {
                SetAttribute(attributes, "set item texture prefab", selection.PaintKit);
                SetAttribute(attributes, "set item texture seed", selection.Seed);
                SetAttribute(attributes, "set item texture wear", selection.Wear);
            }

            if (includeStickers && selection is not null)
            {
                foreach (var sticker in selection.Stickers.Where(sticker =>
                             sticker.Schema < stickerSchemaCount))
                {
                    SetStickerAttributes(attributes, sticker);
                }
            }

            if (includeKeychain && selection?.Keychain is { } keychain)
                SetKeychainAttributes(attributes, keychain);

            return true;
        }
        catch (Exception exception)
        {
            LogPreparationError(exception);
            itemViewHandle = nint.Zero;
            return false;
        }
    }

    private bool TryPrepareReplayItem(
        SlotCosmeticState state,
        ReplayItemSelection selection,
        ulong steamId,
        int defaultQuality,
        out nint itemViewHandle)
    {
        itemViewHandle = nint.Zero;
        if (_disposed || _constructor is null || _setAttributeByName is null)
            return false;

        try
        {
            if (!TryGetOrCreateItemView(state, selection.DefIndex, out itemViewHandle))
                return false;

            var item = new CEconItemView(itemViewHandle);
            var networked = item.NetworkedDynamicAttributes;
            var attributes = item.AttributeList;
            if (networked.Handle == nint.Zero || attributes.Handle == nint.Zero)
                return false;

            item.Initialized = true;
            item.ItemDefinitionIndex = selection.DefIndex;
            AssignReplayIdentity(item, selection.Identity, steamId, defaultQuality);
            networked.Attributes.RemoveAll();
            attributes.Attributes.RemoveAll();
            SetTextureAttributes(networked, selection.PaintKit, selection.Seed, selection.Wear);
            SetTextureAttributes(attributes, selection.PaintKit, selection.Seed, selection.Wear);
            if (selection.Identity.StattrakCounter is { } counter)
            {
                SetStattrakAttributes(networked, counter);
                SetStattrakAttributes(attributes, counter);
            }
            return true;
        }
        catch (Exception exception)
        {
            LogPreparationError(exception);
            itemViewHandle = nint.Zero;
            return false;
        }
    }

    internal void ClearSlot(int slot)
    {
        foreach (var key in _views.Keys.Where(key => key.Slot == slot).ToArray())
        {
            if (_views.Remove(key, out var handle))
                Marshal.FreeHGlobal(handle);
        }
    }

    internal void Clear()
    {
        foreach (var handle in _views.Values)
            Marshal.FreeHGlobal(handle);
        _views.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Clear();
    }

    private void SetStickerAttributes(CAttributeList attributes, StickerSelection sticker)
    {
        var slot = $"sticker slot {sticker.Slot}";
        SetAttribute(attributes, $"{slot} id", AttributeEncoding.UInt32BitsToSingle(sticker.DefIndex));
        SetAttribute(attributes, $"{slot} schema", AttributeEncoding.UInt32BitsToSingle(sticker.Schema));
        SetAttribute(attributes, $"{slot} wear", sticker.Wear);
        if (sticker.Rotation is float rotation)
            SetAttribute(attributes, $"{slot} rotation", rotation);
        if (sticker.X is float x)
            SetAttribute(attributes, $"{slot} offset x", x);
        if (sticker.Y is float y)
            SetAttribute(attributes, $"{slot} offset y", y);
        if (sticker.Scale is float scale)
            SetAttribute(attributes, $"{slot} scale", scale);
    }

    private void SetKeychainAttributes(CAttributeList attributes, KeychainSelection keychain)
    {
        var slot = $"keychain slot {keychain.Slot}";
        SetAttribute(attributes, $"{slot} id", AttributeEncoding.UInt32BitsToSingle(keychain.DefIndex));
        SetAttribute(attributes, $"{slot} seed", AttributeEncoding.Int32BitsToSingle(keychain.Seed));
        if (keychain.Sticker is uint sticker)
            SetAttribute(attributes, $"{slot} sticker", AttributeEncoding.UInt32BitsToSingle(sticker));
        if (keychain.X is float x)
            SetAttribute(attributes, $"{slot} offset x", x);
        if (keychain.Y is float y)
            SetAttribute(attributes, $"{slot} offset y", y);
        if (keychain.Z is float z)
            SetAttribute(attributes, $"{slot} offset z", z);
        if (keychain.Highlight is uint highlight)
            SetAttribute(attributes, $"{slot} highlight", AttributeEncoding.UInt32BitsToSingle(highlight));
    }

    private void SetAttribute(CAttributeList attributes, string name, float value)
        => _setAttributeByName!.Invoke(attributes.Handle, name, value);

    private void SetTextureAttributes(
        CAttributeList attributes,
        int paintKit,
        int seed,
        float wear)
    {
        SetAttribute(attributes, "set item texture prefab", paintKit);
        SetAttribute(attributes, "set item texture seed", seed);
        SetAttribute(attributes, "set item texture wear", wear);
    }

    private void SetStattrakAttributes(CAttributeList attributes, int counter)
    {
        SetAttribute(attributes, "kill eater", AttributeEncoding.Int32BitsToSingle(counter));
        SetAttribute(attributes, "kill eater score type", 0.0f);
    }

    private bool TryGetOrCreateItemView(
        SlotCosmeticState state,
        ushort defIndex,
        out nint itemViewHandle)
    {
        var key = (state.Slot, state.UserId, defIndex);
        if (_views.TryGetValue(key, out itemViewHandle))
            return true;

        var classSize = Schema.GetClassSize("CEconItemView");
        if (classSize <= 0)
            return false;

        itemViewHandle = Marshal.AllocHGlobal(classSize);
        try
        {
            _constructor!.Invoke(itemViewHandle);
            _views.Add(key, itemViewHandle);
            return true;
        }
        catch
        {
            Marshal.FreeHGlobal(itemViewHandle);
            itemViewHandle = nint.Zero;
            throw;
        }
    }

    private void LogPreparationError(Exception exception)
    {
        if (_errorLogged)
            return;

        _errorLogged = true;
        _logger.LogError(exception, "[BotRandomizer] Failed to prepare an item view");
    }

    private static void AssignItemId(CEconItemView item)
    {
        var itemId = EconItemIdAllocator.Next();
        item.ItemID = itemId;
        item.ItemIDLow = (uint)(itemId & uint.MaxValue);
        item.ItemIDHigh = (uint)(itemId >> 32);
    }

    private static void AssignReplayIdentity(
        CEconItemView item,
        ReplayEconIdentity identity,
        ulong fallbackSteamId,
        int defaultQuality)
    {
        var owner = identity.OriginalOwnerSteamId.GetValueOrDefault(fallbackSteamId);
        item.AccountID = identity.ItemAccountId ?? AccountIdFromSteamId(owner);
        item.EntityQuality = identity.Quality ??
            (identity.StattrakCounter is not null ? 9 : defaultQuality);
        var itemId = identity.ItemId.GetValueOrDefault();
        if (itemId == 0)
            itemId = EconItemIdAllocator.Next();
        item.ItemID = itemId;
        item.ItemIDLow = (uint)(itemId & uint.MaxValue);
        item.ItemIDHigh = (uint)(itemId >> 32);
        if (!string.IsNullOrWhiteSpace(identity.CustomName))
            item.CustomName = identity.CustomName;
    }

    private static uint AccountIdFromSteamId(ulong steamId)
    {
        if (steamId >= SteamId64AccountBase)
        {
            var accountId = steamId - SteamId64AccountBase;
            return accountId <= uint.MaxValue ? (uint)accountId : 0;
        }
        return steamId <= uint.MaxValue ? (uint)steamId : 0;
    }
}
