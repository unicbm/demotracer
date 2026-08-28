using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Microsoft.Extensions.Logging;

namespace BotRandomizer;

internal sealed class CosmeticApplicator
{
    private readonly MemoryFunctionWithReturn<nint, string, float, int>? _setAttributeByName;
    private readonly ILogger _logger;
    private readonly Dictionary<int, AppliedKnifeCosmetic> _appliedKnives = [];
    private readonly Dictionary<int, AppliedGloveCosmetic> _appliedGloves = [];

    internal CosmeticApplicator(
        MemoryFunctionWithReturn<nint, string, float, int>? setAttributeByName,
        ILogger logger)
    {
        _setAttributeByName = setAttributeByName;
        _logger = logger;
    }

    internal bool NativeAvailable => _setAttributeByName is not null;

    internal void ClearSlot(int slot)
    {
        _appliedKnives.Remove(slot);
        _appliedGloves.Remove(slot);
    }

    internal void Reset()
    {
        _appliedKnives.Clear();
        _appliedGloves.Clear();
    }

    internal void ApplyAgent(CCSPlayerPawn pawn, string model)
    {
        if (!pawn.IsValid)
            return;

        pawn.SetModel(model);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
        var color = pawn.Render;
        pawn.Render = Color.FromArgb(255, color.R, color.G, color.B);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
    }

    internal void ApplyKnife(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        KnifeSelection selection)
    {
        if (!pawn.IsValid)
            return;

        try
        {
            var weapons = pawn.WeaponServices?.MyWeapons;
            if (weapons is null)
                return;

            var fingerprint = KnifeCosmeticFingerprint.From(selection);
            foreach (var handle in weapons)
            {
                var weapon = handle.Value;
                if (weapon is not { IsValid: true })
                    continue;

                var item = weapon.AttributeManager?.Item;
                if (item is null)
                    continue;

                var isDefaultKnife = weapon.DesignerName is "weapon_knife" or "weapon_knife_t";
                var isAppliedEntity = _appliedKnives.TryGetValue(player.Slot, out var applied)
                    && applied.PawnHandle == pawn.EntityHandle.Raw
                    && applied.WeaponHandle == weapon.EntityHandle.Raw
                    && applied.Fingerprint == fingerprint;
                var isPreparedEntity = item.ItemDefinitionIndex == selection.DefIndex && item.Initialized;
                if (!isDefaultKnife && !isAppliedEntity && !isPreparedEntity)
                    continue;

                var econIsCurrent = item.ItemDefinitionIndex == selection.DefIndex
                    && (isPreparedEntity || isAppliedEntity && item.ItemID == applied.ItemId);

                // ChangeSubclass may be ignored while the fresh knife entity is still
                // settling. Reassert it on the scheduled passes even when econ metadata
                // is already current so model and animations cannot remain stale.
                weapon.AcceptInput("ChangeSubclass", value: selection.DefIndex.ToString());
                item.ItemDefinitionIndex = selection.DefIndex;
                item.EntityQuality = selection.Identity?.Quality ?? 3;
                if (_setAttributeByName is not null)
                {
                    if (!econIsCurrent)
                    {
                        item.AttributeList.Attributes.RemoveAll();
                        item.NetworkedDynamicAttributes.Attributes.RemoveAll();
                        AssignReplayIdentity(item, selection.Identity, player.SteamID);
                        SetTextureAttributes(
                            item.NetworkedDynamicAttributes,
                            item.AttributeList,
                            selection.PaintKit,
                            selection.Seed,
                            selection.Wear);
                    }
                    weapon.FallbackPaintKit = selection.PaintKit;
                    weapon.FallbackSeed = selection.Seed;
                    weapon.FallbackWear = selection.Wear;
                    MarkWeaponPaintStateChanged(weapon);
                }
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
                _appliedKnives[player.Slot] = new AppliedKnifeCosmetic(
                    pawn.EntityHandle.Raw,
                    weapon.EntityHandle.Raw,
                    fingerprint,
                    item.ItemID);
                return;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "[BotRandomizer] Failed to apply knife cosmetics");
        }
    }

    internal void ApplyKnife(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayItemSelection selection)
        => ApplyKnife(
            player,
            pawn,
            new KnifeSelection(
                selection.DefIndex,
                selection.PaintKit,
                selection.Wear,
                selection.Seed,
                selection.Identity));

    internal bool ApplyGloves(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        GloveSelection selection)
    {
        if (_setAttributeByName is null || !pawn.IsValid)
            return false;

        try
        {
            var item = pawn.EconGloves;
            var fingerprint = GloveCosmeticFingerprint.From(selection);
            if (_appliedGloves.TryGetValue(player.Slot, out var applied)
                && applied.PawnHandle == pawn.EntityHandle.Raw
                && applied.Fingerprint == fingerprint
                && item.Initialized
                && item.ItemID == applied.ItemId
                && item.ItemDefinitionIndex == applied.ItemDefinitionIndex
                && item.AccountID == applied.AccountId)
            {
                return true;
            }

            item.ItemDefinitionIndex = selection.DefIndex;
            item.Initialized = true;
            AssignReplayIdentity(item, selection.Identity, player.SteamID);
            item.NetworkedDynamicAttributes.Attributes.RemoveAll();
            item.AttributeList.Attributes.RemoveAll();
            SetTextureAttributes(
                item.NetworkedDynamicAttributes,
                item.AttributeList,
                selection.PaintKit,
                selection.Seed,
                selection.Wear);
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_EconGloves");
            pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
            _appliedGloves[player.Slot] = new AppliedGloveCosmetic(
                pawn.EntityHandle.Raw,
                fingerprint,
                item.ItemID,
                item.ItemDefinitionIndex,
                item.AccountID);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "[BotRandomizer] Failed to apply glove cosmetics");
            return false;
        }
    }

    internal bool ApplyGloves(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayItemSelection selection)
        => ApplyGloves(
            player,
            pawn,
            new GloveSelection(
                selection.DefIndex,
                selection.PaintKit,
                selection.Wear,
                selection.Seed,
                selection.Identity));

    internal void ShowGloves(CCSPlayerPawn pawn)
    {
        if (pawn.IsValid)
            pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
    }

    internal void SyncPickedUpKnife(CCSPlayerPawn pawn)
    {
        if (!pawn.IsValid)
            return;

        try
        {
            var weapons = pawn.WeaponServices?.MyWeapons;
            if (weapons is null)
                return;

            foreach (var handle in weapons)
            {
                var weapon = handle.Value;
                if (weapon is not { IsValid: true }
                    || !RandomizerAssets.KnifeDefIndexByName.TryGetValue(
                        weapon.DesignerName,
                        out var defIndex))
                {
                    continue;
                }

                var item = weapon.AttributeManager?.Item;
                if (item is null)
                    continue;

                weapon.AcceptInput("ChangeSubclass", value: defIndex.ToString());
                item.ItemDefinitionIndex = defIndex;
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "[BotRandomizer] Failed to synchronize a picked-up knife");
        }
    }

    private void SetTextureAttributes(
        CAttributeList networkedAttributes,
        CAttributeList attributeList,
        int paintKit,
        int seed,
        float wear)
    {
        SetAttribute(networkedAttributes, "set item texture prefab", paintKit);
        SetAttribute(networkedAttributes, "set item texture seed", seed);
        SetAttribute(networkedAttributes, "set item texture wear", wear);
        SetAttribute(attributeList, "set item texture prefab", paintKit);
        SetAttribute(attributeList, "set item texture seed", seed);
        SetAttribute(attributeList, "set item texture wear", wear);
    }

    private static void MarkWeaponPaintStateChanged(CBasePlayerWeapon weapon)
    {
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackPaintKit");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackSeed");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_flFallbackWear");
    }

    private void SetAttribute(CAttributeList attributes, string name, float value)
    {
        if (_setAttributeByName is not null && attributes.Handle != IntPtr.Zero)
            _setAttributeByName.Invoke(attributes.Handle, name, value);
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
        ReplayEconIdentity? identity,
        ulong fallbackSteamId)
    {
        if (identity is null)
        {
            item.AccountID = AccountIdFromSteamId(fallbackSteamId);
            AssignItemId(item);
            return;
        }

        var owner = identity.OriginalOwnerSteamId.GetValueOrDefault(fallbackSteamId);
        item.AccountID = identity.ItemAccountId ?? AccountIdFromSteamId(owner);
        item.EntityQuality = identity.Quality ?? (identity.StattrakCounter is not null ? 9 : 4);
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
        const ulong steamId64AccountBase = 76_561_197_960_265_728;
        if (steamId >= steamId64AccountBase)
        {
            var accountId = steamId - steamId64AccountBase;
            return accountId <= uint.MaxValue ? (uint)accountId : 0;
        }
        return steamId <= uint.MaxValue ? (uint)steamId : 0;
    }

    private readonly record struct KnifeCosmeticFingerprint(
        ushort DefIndex,
        int PaintKit,
        int Seed,
        int WearBits)
    {
        internal static KnifeCosmeticFingerprint From(KnifeSelection selection)
            => new(
                selection.DefIndex,
                selection.PaintKit,
                selection.Seed,
                BitConverter.SingleToInt32Bits(selection.Wear));
    }

    private readonly record struct GloveCosmeticFingerprint(
        ushort DefIndex,
        int PaintKit,
        int Seed,
        int WearBits)
    {
        internal static GloveCosmeticFingerprint From(GloveSelection selection)
            => new(
                selection.DefIndex,
                selection.PaintKit,
                selection.Seed,
                BitConverter.SingleToInt32Bits(selection.Wear));
    }

    private readonly record struct AppliedKnifeCosmetic(
        uint PawnHandle,
        uint WeaponHandle,
        KnifeCosmeticFingerprint Fingerprint,
        ulong ItemId);

    private readonly record struct AppliedGloveCosmetic(
        uint PawnHandle,
        GloveCosmeticFingerprint Fingerprint,
        ulong ItemId,
        ushort ItemDefinitionIndex,
        uint AccountId);
}
