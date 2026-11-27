using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
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

        var skeleton = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance();
        var currentModel = skeleton is not null && skeleton.Handle != IntPtr.Zero
            ? skeleton.ModelState.ModelName
            : null;
        if (!string.Equals(currentModel?.Replace('\\', '/'), model.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
            // SetModel publishes the skeleton's model handle through the engine.
            pawn.SetModel(model);
        var color = pawn.Render;
        if (color.A != 255)
        {
            pawn.Render = Color.FromArgb(255, color.R, color.G, color.B);
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
        }
    }

    internal void ApplyKnife(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        KnifeSelection selection,
        uint? takeoverWeaponHandle = null)
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
                if (weapon is not { IsValid: true })
                    continue;
                if (takeoverWeaponHandle is { } expected && weapon.EntityHandle.Raw != expected)
                    continue;

                var item = weapon.AttributeManager?.Item;
                if (item is null)
                    continue;

                var isDefaultKnife = weapon.DesignerName is "weapon_knife" or "weapon_knife_t";
                var isAppliedEntity = _appliedKnives.TryGetValue(player.Slot, out var applied)
                    && applied.PawnHandle == pawn.EntityHandle.Raw
                    && applied.WeaponHandle == weapon.EntityHandle.Raw
                    && applied.Selection == selection;
                var isPreparedEntity = item.ItemDefinitionIndex == selection.DefIndex && item.Initialized;
                if (takeoverWeaponHandle is null && !isDefaultKnife && !isAppliedEntity && !isPreparedEntity)
                    continue;

                var econIsCurrent = takeoverWeaponHandle is null && item.ItemDefinitionIndex == selection.DefIndex
                    && (isPreparedEntity || isAppliedEntity && item.ItemID == applied.ItemId);

                if (!econIsCurrent && _setAttributeByName is not null)
                {
                    item.ItemDefinitionIndex = selection.DefIndex;
                    item.Initialized = true;
                    item.AttributeList.Attributes.RemoveAll();
                    item.NetworkedDynamicAttributes.Attributes.RemoveAll();
                    EconItemIdentity.Apply(item, selection.Identity, player.SteamID, defaultQuality: 3);
                    SetTextureAttributes(item.NetworkedDynamicAttributes, item.AttributeList,
                        selection.PaintKit, selection.Seed, selection.Wear);
                    if (selection.Identity?.StattrakCounter is { } counter)
                        EconItemIdentity.ApplyStattrak(item, counter, _setAttributeByName);
                    MarkRebuiltItem(weapon, "CEconEntity", "m_AttributeManager",
                        Schema.GetSchemaOffset("CAttributeContainer", "m_Item"));
                }
                else
                {
                    SetKnifeItemField(weapon, ref item.ItemDefinitionIndex, selection.DefIndex, "m_iItemDefinitionIndex");
                    var quality = selection.Identity?.ResolveQuality(defaultQuality: 3) ?? 3;
                    SetKnifeItemField(weapon, ref item.EntityQuality, quality, "m_iEntityQuality");
                }
                if (_setAttributeByName is not null)
                    ApplyFallbackPaint(weapon, selection);
                // Rebuild subclass/model/animations only after the complete econ
                // identity is in place. Takeover may have left another knife's
                // definition here; rebuilding first observes that stale identity.
                // Reassert on settling passes even when econ is already current.
                weapon.AcceptInput("ChangeSubclass", value: selection.DefIndex.ToString());
                _appliedKnives[player.Slot] = new AppliedKnifeCosmetic(
                    pawn.EntityHandle.Raw,
                    weapon.EntityHandle.Raw,
                    selection,
                    item.ItemID,
                    item.AccountID);
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
            if (_appliedGloves.TryGetValue(player.Slot, out var applied)
                && applied.PawnHandle == pawn.EntityHandle.Raw
                && applied.Selection == selection
                && item.Initialized
                && item.ItemID == applied.ItemId
                && item.ItemDefinitionIndex == applied.ItemDefinitionIndex
                && item.AccountID == applied.AccountId)
            {
                return true;
            }

            item.ItemDefinitionIndex = selection.DefIndex;
            item.Initialized = true;
            EconItemIdentity.Apply(item, selection.Identity, player.SteamID, defaultQuality: 4);
            item.NetworkedDynamicAttributes.Attributes.RemoveAll();
            item.AttributeList.Attributes.RemoveAll();
            SetTextureAttributes(
                item.NetworkedDynamicAttributes,
                item.AttributeList,
                selection.PaintKit,
                selection.Seed,
                selection.Wear);
            MarkRebuiltItem(pawn, "CCSPlayerPawn", "m_EconGloves", itemOffset: 0);
            pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
            _appliedGloves[player.Slot] = new AppliedGloveCosmetic(
                pawn.EntityHandle.Raw,
                selection,
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

    // A surviving bot can enter the new round with the item this provider dressed last round.
    internal void RestoreSurvivingStockKnife(int slot, CCSPlayerPawn pawn, byte team)
    {
        if (!_appliedKnives.TryGetValue(slot, out var previous) || previous.PawnHandle != pawn.EntityHandle.Raw ||
            pawn.WeaponServices?.MyWeapons is not { } weapons)
            return;
        foreach (var handle in weapons)
        {
            if (handle.Raw != previous.WeaponHandle || handle.Value is not { IsValid: true } weapon ||
                weapon.AttributeManager?.Item is not { } item || item.ItemID != previous.ItemId ||
                item.ItemDefinitionIndex != previous.Selection.DefIndex)
                continue;
            ushort stockDef = team == RandomizerAssets.TerroristTeam ? (ushort)59 : (ushort)42;
            ResetItem(item, stockDef);
            MarkRebuiltItem(weapon, "CEconEntity", "m_AttributeManager",
                Schema.GetSchemaOffset("CAttributeContainer", "m_Item"));
            ApplyFallbackPaint(weapon, new KnifeSelection(stockDef, 0, 0, 0));
            weapon.AcceptInput("ChangeSubclass", value: stockDef.ToString());
            return;
        }
    }

    internal void ApplyStockGloves(int slot, CCSPlayerPawn pawn)
    {
        _appliedGloves.Remove(slot);
        var item = pawn.EconGloves;
        if (item.ItemDefinitionIndex is 0 or 5028 or 5029)
            return;
        ResetItem(item, 0);
        MarkRebuiltItem(pawn, "CCSPlayerPawn", "m_EconGloves", itemOffset: 0);
        pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
    }

    private static void ResetItem(CEconItemView item, ushort definition)
    {
        item.AttributeList.Attributes.RemoveAll();
        item.NetworkedDynamicAttributes.Attributes.RemoveAll();
        item.ItemDefinitionIndex = definition;
        item.ItemID = 0;
        item.ItemIDHigh = 0;
        item.ItemIDLow = 0;
        item.AccountID = 0;
        item.EntityQuality = 0;
        item.CustomName = string.Empty;
        // The equipped team knife has an initialized item view. Empty pawn gloves do not.
        item.Initialized = definition != 0;
    }

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

                SetKnifeItemField(weapon, ref item.ItemDefinitionIndex, defIndex, "m_iItemDefinitionIndex");
                weapon.AcceptInput("ChangeSubclass", value: defIndex.ToString());
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "[BotRandomizer] Failed to synchronize a picked-up knife");
        }
    }

    internal TakeoverWearables CaptureTakeoverWearables(int botSlot, uint pawnHandle)
    {
        KnifeSelection? knife = null;
        uint weaponHandle = 0;
        GloveSelection? gloves = null;
        // Use what this provider actually applied to this pawn, not the human's
        // inventory or a newly resolved policy after DTR releases control.
        if (_appliedKnives.TryGetValue(botSlot, out var appliedKnife) && appliedKnife.PawnHandle == pawnHandle)
        {
            weaponHandle = appliedKnife.WeaponHandle;
            var identity = appliedKnife.Selection.Identity ?? new ReplayEconIdentity(null, null, null, null, null, null);
            knife = appliedKnife.Selection with
            {
                Identity = identity with { ItemId = appliedKnife.ItemId, ItemAccountId = appliedKnife.AccountId }
            };
        }
        if (_appliedGloves.TryGetValue(botSlot, out var appliedGloves) && appliedGloves.PawnHandle == pawnHandle)
        {
            var identity = appliedGloves.Selection.Identity ?? new ReplayEconIdentity(null, null, null, null, null, null);
            gloves = appliedGloves.Selection with
            {
                Identity = identity with { ItemId = appliedGloves.ItemId, ItemAccountId = appliedGloves.AccountId }
            };
        }
        return new(weaponHandle, knife, gloves);
    }

    internal bool RestoreTakeoverWearables(CCSPlayerController bot, CCSPlayerPawn pawn, TakeoverWearables wearables)
    {
        // The engine may have rewritten an initialized item in place. Neither
        // Initialized nor an old applied-item cache proves its econ is current.
        ClearSlot(bot.Slot);
        if (wearables.Knife is { } knife)
            ApplyKnife(bot, pawn, knife, wearables.WeaponHandle);
        return wearables.Gloves is { } gloves && ApplyGloves(bot, pawn, gloves);
    }

    private void SetTextureAttributes(
        CAttributeList networkedAttributes,
        CAttributeList attributeList,
        int paintKit,
        int seed,
        float wear)
    {
        if (paintKit == 0)
            return;
        SetAttribute(networkedAttributes, "set item texture prefab", paintKit);
        SetAttribute(networkedAttributes, "set item texture seed", seed);
        SetAttribute(networkedAttributes, "set item texture wear", wear);
        SetAttribute(attributeList, "set item texture prefab", paintKit);
        SetAttribute(attributeList, "set item texture seed", seed);
        SetAttribute(attributeList, "set item texture wear", wear);
    }

    private static void ApplyFallbackPaint(CBasePlayerWeapon weapon, KnifeSelection selection)
    {
        if (weapon.FallbackPaintKit != selection.PaintKit)
        {
            weapon.FallbackPaintKit = selection.PaintKit;
            Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackPaintKit");
        }
        if (weapon.FallbackSeed != selection.Seed)
        {
            weapon.FallbackSeed = selection.Seed;
            Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackSeed");
        }
        if (weapon.FallbackWear != selection.Wear)
        {
            weapon.FallbackWear = selection.Wear;
            Utilities.SetStateChanged(weapon, "CEconEntity", "m_flFallbackWear");
        }
    }

    private static void SetKnifeItemField<T>(CBasePlayerWeapon weapon, ref T current, T value, string field)
        where T : struct, IEquatable<T>
    {
        if (current.Equals(value)) return;
        current = value;
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager",
            Schema.GetSchemaOffset("CAttributeContainer", "m_Item") + Schema.GetSchemaOffset("CEconItemView", field));
    }

    private static void MarkRebuiltItem(CBaseEntity entity, string parentClass, string parentField, int itemOffset)
    {
        // The weapon and glove net tables contain the individual econ properties.
        // A change at the container's base address cannot be resolved by CS2.
        void Mark(string field, int extraOffset = 0) => Utilities.SetStateChanged(entity, parentClass,
            parentField, itemOffset + Schema.GetSchemaOffset("CEconItemView", field) + extraOffset);

        Mark("m_iItemDefinitionIndex");
        Mark("m_iEntityQuality");
        Mark("m_iItemIDHigh");
        Mark("m_iItemIDLow");
        Mark("m_iAccountID");
        Mark("m_bInitialized");
        Mark("m_szCustomName");
        var attributesOffset = Schema.GetSchemaOffset("CAttributeList", "m_Attributes");
        Mark("m_AttributeList", attributesOffset);
        Mark("m_NetworkedDynamicAttributes", attributesOffset);
    }

    private void SetAttribute(CAttributeList attributes, string name, float value)
    {
        if (_setAttributeByName is not null && attributes.Handle != IntPtr.Zero)
            _setAttributeByName.Invoke(attributes.Handle, name, value);
    }

    private readonly record struct AppliedKnifeCosmetic(
        uint PawnHandle,
        uint WeaponHandle,
        KnifeSelection Selection,
        ulong ItemId,
        uint AccountId);

    private readonly record struct AppliedGloveCosmetic(
        uint PawnHandle,
        GloveSelection Selection,
        ulong ItemId,
        ushort ItemDefinitionIndex,
        uint AccountId);
}
