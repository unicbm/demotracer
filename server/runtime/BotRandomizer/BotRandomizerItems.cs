using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Microsoft.Extensions.Logging;

namespace BotRandomizer;

// Native item creation stays in one pre/post hook pair for both random and replay cosmetics.
public sealed partial class BotRandomizerPlugin
{
    private void LoadAttributeWriter()
    {
        MemoryFunctionWithReturn<nint, string, float, int>? writer = null;
        try
        {
            writer = new MemoryFunctionWithReturn<nint, string, float, int>(
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? "55 48 89 E5 41 57 41 56 49 89 FE 41 55 41 54 53 48 89 F3 48 83 EC ? F3 0F 11 85"
                    : "48 89 4C 24 08 53 41 55 41 56 48 81 EC A0 00 00 00 0F 29 74 24 70 48 8B DA 0F 28 F2 4C 8B E9 E8 ? ? ? ?");
        }
        catch (Exception exception)
        {
            Logger.LogError(
                exception,
                "[BotRandomizer] SetOrAddAttributeValueByName signature failed; economic cosmetics disabled");
        }

        _applicator = new CosmeticApplicator(writer, Logger);
        MemoryFunctionWithReturn<nint, nint>? itemViewConstructor = null;
        if (writer is not null)
        {
            try
            {
                itemViewConstructor = new MemoryFunctionWithReturn<nint, nint>(
                    RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                        ? "55 48 8D 05 ? ? ? ? 66 0F EF C0 48 89 E5 41 57 45 31 FF"
                        : "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 41 54 41 55 41 56 41 57 48 83 EC ? 48 8B F9 48 8D 05");
            }
            catch (Exception exception)
            {
                Logger.LogError(
                    exception,
                    "[BotRandomizer] CEconItemView constructor signature failed; weapon cosmetics disabled");
            }
        }
        _weaponItemViews = new WeaponItemViewStore(itemViewConstructor, writer, Logger);
    }

    private HookResult OnGiveNamedItemPre(DynamicHook hook)
    {
        if (_draining || _catalog is null
            || _roller is null
            || _weaponItemViews is null)
        {
            return HookResult.Continue;
        }

        try
        {
            var itemServices = hook.GetParam<CCSPlayer_ItemServices>(0);
            var designerName = hook.GetParam<string>(1);
            if (string.IsNullOrWhiteSpace(designerName))
                return HookResult.Continue;

            var player = GetPlayerFromItemServices(itemServices);
            if (player is null)
                return HookResult.Continue;

            var state = GetOrCreateState(player);
            if (state is null)
                return HookResult.Continue;

            TryGetWritePolicy(state, out var writePolicy);
            if (designerName is "weapon_knife" or "weapon_knife_t")
            {
                if (writePolicy?.Knife is null && !_options.Knives)
                    return HookResult.Continue;

                var prepared = writePolicy?.Knife is { } replayKnife
                    ? _weaponItemViews.TryPrepareReplayKnife(
                        state, replayKnife, player.SteamID, out var knifeItemViewHandle)
                    : _weaponItemViews.TryPrepareKnife(
                        state, state.Loadout.Knife, player.SteamID, out knifeItemViewHandle);
                if (prepared)
                {
                    hook.SetParam(3, knifeItemViewHandle);
                }
                return HookResult.Continue;
            }

            if (!_catalog.TryGetWeapon(designerName, out var weapon))
            {
                return HookResult.Continue;
            }

            if (writePolicy is not null
                && writePolicy.TryGetWeapon(weapon.DefIndex, out var replayWeapon))
            {
                if (_weaponItemViews.TryPrepareReplayWeapon(
                        state,
                        replayWeapon,
                        player.SteamID,
                        out var replayItemViewHandle))
                {
                    hook.SetParam(3, replayItemViewHandle);
                }
                return HookResult.Continue;
            }

            if (!_options.HasWeaponCosmetics)
                return HookResult.Continue;

            var selection = _options.ResolveWeapon(
                weapon, _roller.GetOrCreateWeapon(state.Loadout, weapon.DefIndex));
            if (selection is not null && _weaponItemViews.TryPrepare(
                    state,
                    weapon,
                    selection,
                    player.SteamID,
                    out var itemViewHandle))
            {
                hook.SetParam(3, itemViewHandle);
            }
        }
        catch (Exception exception)
        {
            if (!_giveNamedItemErrorLogged)
            {
                _giveNamedItemErrorLogged = true;
                Logger.LogError(exception, "[BotRandomizer] GiveNamedItem pre-hook failed");
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnGiveNamedItemPost(DynamicHook hook)
    {
        try
        {
            var weaponHandle = hook.GetReturn<nint>();
            if (weaponHandle == nint.Zero)
                return HookResult.Continue;

            var itemServices = hook.GetParam<CCSPlayer_ItemServices>(0);
            var player = GetPlayerFromItemServices(itemServices);
            var state = GetOrCreateState(player);
            if (player is null || state is null ||
                !TryGetWritePolicy(state, out var writePolicy))
            {
                return HookResult.Continue;
            }

            var weapon = new CBasePlayerWeapon(weaponHandle);
            var item = weapon.AttributeManager?.Item;
            if (!weapon.IsValid || item is null)
                return HookResult.Continue;

            ReplayEconIdentity? identity = null;
            if (hook.GetParam<string>(1) is "weapon_knife" or "weapon_knife_t")
                identity = writePolicy.Knife?.Identity;
            else if (writePolicy.TryGetWeapon(item.ItemDefinitionIndex, out var replayWeapon))
                identity = replayWeapon.Identity;

            if (identity is not null)
                ApplyReplayOriginalOwner(weapon, identity, player.SteamID);
        }
        catch (Exception exception)
        {
            if (!_giveNamedItemErrorLogged)
            {
                _giveNamedItemErrorLogged = true;
                Logger.LogError(exception, "[BotRandomizer] GiveNamedItem post-hook failed");
            }
        }

        return HookResult.Continue;
    }

    private static void ApplyReplayOriginalOwner(
        CBasePlayerWeapon weapon,
        ReplayEconIdentity identity,
        ulong fallbackSteamId)
    {
        var owner = ReplayOriginalOwner.ResolveSteamId(identity, fallbackSteamId);
        if (owner == 0)
            return;

        var (low, high) = ReplayOriginalOwner.SplitSteamId(owner);
        weapon.OriginalOwnerXuidLow = low;
        weapon.OriginalOwnerXuidHigh = high;
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidLow");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidHigh");
    }

    private static CCSPlayerController? GetPlayerFromItemServices(CCSPlayer_ItemServices itemServices)
    {
        var pawn = itemServices.Pawn.Value;
        if (pawn is not { IsValid: true } || pawn.Controller.Value is not { IsValid: true } controller)
            return null;

        var player = new CCSPlayerController(controller.Handle);
        return player is { IsValid: true, IsBot: true, IsHLTV: false } ? player : null;
    }
}
