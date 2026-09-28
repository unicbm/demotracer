/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotHiderApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Memory;
using DemoTracerBotHiderApi;
using HiderApi = BotHiderApi.IBotHiderApi;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private sealed class DemoTracerBotHiderBridge
    {
        private static readonly PluginCapability<HiderApi> Capability = new("bothider:api");
        private sealed record ControllerBase(ulong Incarnation, uint Handle, int UserId, BotHiderClan Clan);
        private sealed record ActiveLease(BotHiderPresentationOverride[] Overrides,
            Dictionary<int, ControllerBase> Bases);
        private readonly Dictionary<string, ActiveLease> _active = new(StringComparer.Ordinal);
        private HiderApi? _api;
        private bool _resolved;

        // Invalidates a provider reference after hot reload.
        public void Refresh()
        {
            foreach (var (token, lease) in _active)
            {
                RestoreClans(lease.Bases);
                try { _api?.ReleaseSlots(token); }
                catch { /* The old provider may already be unloaded. */ }
            }
            _api = null;
            _resolved = false;
            _active.Clear();
        }

        public bool IsAvailable() => TryGetApi(out _);

        // Checks the native managed-slot set instead of the controller fake flag.
        public bool IsManagedBot(int slot)
        {
            try { return TryGetApi(out var api) && api.IsManagedBot(slot); }
            catch { Refresh(); return false; }
        }

        // Adapts the unified provider's base and published slot state.
        public bool TryGetManagedSlot(int slot, out BotHiderManagedSlot state)
        {
            state = new BotHiderManagedSlot { Slot = slot };
            if (!TryGetApi(out var api)) return false;
            try
            {
                if (!api.IsManagedBot(slot)) return false;
                ulong incarnation = api.GetSlotIncarnation(slot);
                if (incarnation == 0) return false;
                state.Incarnation = incarnation;
                state.BaseSteamId = api.GetBaseBotSteamId(slot);
                state.PublishedSteamId = api.GetBotSteamId(slot);
                state.BasePlayerName = api.GetBasePersonaName(slot);
                state.BasePing = api.GetPing(slot);
                state.BaseCrosshairCode = api.GetCrosshairCode(slot);
                state.BaseScoreboardFlair = api.GetScoreboardFlair(slot);
                return true;
            }
            catch { Refresh(); return false; }
        }

        // Claims all slots before publishing any replay identity.
        public BotHiderPresentationLeaseResult Acquire(
            string owner, BotHiderPresentationOverride[] overrides, CancellationToken ownerLifetime)
        {
            if (!TryGetApi(out var api)) return Fail("provider_unavailable");
            if (!Validate(overrides, out var reason)) return Fail(reason);
            if (!TryCaptureBases(overrides, null, out var bases, out reason)) return Fail(reason);
            var slots = ToRefs(overrides);
            string token = string.Empty;
            try
            {
                if (!api.TryAcquireSlots(owner, slots, ownerLifetime, out token))
                    return Fail("slot_claim_rejected");
                if (!Apply(api, token, overrides, bases, out reason))
                {
                    RestoreClans(bases);
                    api.ReleaseSlots(token);
                    return Fail(reason);
                }
                _active[token] = new ActiveLease(overrides, bases);
                return Success(api, token, slots);
            }
            catch (Exception ex)
            {
                RestoreClans(bases);
                try { if (token.Length > 0) api.ReleaseSlots(token); }
                catch { /* Provider may already be unloading. */ }
                Refresh();
                return Fail($"provider_error:{ex.Message}");
            }
        }

        // Replaces an active batch and reapplies the previous one on failure.
        public BotHiderPresentationLeaseResult Replace(string leaseToken, BotHiderPresentationOverride[] overrides)
        {
            if (!TryGetApi(out var api)) return Fail("provider_unavailable");
            if (!_active.TryGetValue(leaseToken, out var previous)) return Fail("lease_not_found");
            if (!Validate(overrides, out var reason)) return Fail(reason);
            if (!TryCaptureBases(overrides, previous.Bases, out var bases, out reason)) return Fail(reason);
            var slots = ToRefs(overrides);
            try
            {
                if (!api.TryReplaceSlots(leaseToken, slots))
                    return Fail("slot_claim_rejected");
                if (!RestoreRemovedClans(previous.Bases, bases))
                    reason = "clan_restore_failed";
                if (reason.Length > 0 ||
                    !RestoreDroppedFlairs(api, previous.Overrides, overrides, out reason) ||
                    !Apply(api, leaseToken, overrides, bases, out reason))
                {
                    if (!api.TryReplaceSlots(leaseToken, ToRefs(previous.Overrides)) ||
                        !Apply(api, leaseToken, previous.Overrides, previous.Bases, out _))
                    {
                        RestoreClans(bases);
                        RestoreClans(previous.Bases);
                        api.ReleaseSlots(leaseToken);
                        _active.Remove(leaseToken);
                        return Fail($"rollback_failed:{reason}");
                    }
                    if (!RestoreDroppedFlairs(api, overrides, previous.Overrides, out _))
                        return Fail($"scoreboard_flair_rollback_failed:{reason}");
                    if (!RestoreRemovedClans(bases, previous.Bases))
                        return Fail($"clan_rollback_failed:{reason}");
                    return Fail(reason);
                }
                _active[leaseToken] = new ActiveLease(overrides, bases);
                return Success(api, leaseToken, slots);
            }
            catch (Exception ex)
            {
                RestoreClans(bases);
                RestoreClans(previous.Bases);
                try { api.ReleaseSlots(leaseToken); }
                catch { /* Provider may already be unloading. */ }
                Refresh();
                return Fail($"provider_error:{ex.Message}");
            }
        }

        // Repairs fields changed by a controller lifecycle event.
        public bool Reconcile(string leaseToken)
        {
            if (!_active.TryGetValue(leaseToken, out var lease) || !TryGetApi(out var api))
                return false;
            try { return Apply(api, leaseToken, lease.Overrides, lease.Bases, out _); }
            catch { Refresh(); return false; }
        }

        // Releases one claim and triggers BotHider base restoration.
        public bool Release(string leaseToken)
        {
            if (string.IsNullOrWhiteSpace(leaseToken)) return true;
            _active.Remove(leaseToken, out var lease);
            try
            {
                bool restored = lease == null || RestoreClans(lease.Bases);
                bool released = TryGetApi(out var api) && api.ReleaseSlots(leaseToken);
                return restored && released;
            }
            catch { Refresh(); return false; }
        }

        // Releases every claim from one playback owner.
        public int ReleaseOwner(string owner)
        {
            try
            {
                foreach (var lease in _active.Values)
                    RestoreClans(lease.Bases);
                int count = TryGetApi(out var api) ? api.ReleaseSlotsByOwner(owner) : 0;
                _active.Clear();
                return count;
            }
            catch { Refresh(); return 0; }
        }

        // Supplies provider lifetime information for presentation signatures.
        public BotHiderProviderInfo? GetProviderInfo()
        {
            try
            {
                return !TryGetApi(out var api) ? null : new BotHiderProviderInfo
                {
                    ApiVersion = BotHiderContract.ApiVersion,
                    ProviderEpoch = api.ProviderEpoch,
                    Connected = true
                };
            }
            catch { Refresh(); return null; }
        }

        public BotHiderProviderInfo? ProbeProviderInfo() => GetProviderInfo();

        // Reports claim counts and native signature resolution.
        public BotHiderDiagnostics? GetDiagnostics()
        {
            try
            {
                return !TryGetApi(out var api) ? null : new BotHiderDiagnostics
                {
                    Connected = true,
                    ManagedSlots = api.GetManagedSlots().Length,
                    ActiveLeases = _active.Count,
                    LeasedSlots = _active.Values.Sum(value => value.Overrides.Length),
                    Signatures = api.GetSignatures().Select(value =>
                        $"{value.Name}=0x{value.Addr:X}").ToArray()
                };
            }
            catch { Refresh(); return null; }
        }

        // Validates the complete replay request before the slot claim.
        private bool Validate(BotHiderPresentationOverride[]? overrides, out string reason)
        {
            reason = string.Empty;
            if (overrides is not { Length: > 0 }) { reason = "empty_batch"; return false; }
            var slots = new HashSet<int>();
            var steamIds = new HashSet<ulong>();
            foreach (var request in overrides)
            {
                if (request == null || !slots.Add(request.Slot) ||
                    !TryGetManagedSlot(request.Slot, out var state) ||
                    request.Incarnation != state.Incarnation)
                { reason = "slot_incarnation_changed"; return false; }
                if (request.PlayerName != null &&
                    (request.PlayerName.Length == 0 || request.PlayerName.Contains('\0') ||
                     System.Text.Encoding.UTF8.GetByteCount(request.PlayerName) > DemoTracerBotHiderContract.MaxPlayerNameUtf8Bytes))
                { reason = "invalid_name"; return false; }
                if (request.SteamId is { } sid && (sid == 0 || !steamIds.Add(sid)))
                { reason = "invalid_or_duplicate_steam_id"; return false; }
                if (request.ScoreboardFlair > ushort.MaxValue ||
                    !DemoTracerBotHiderContract.TryNormalizeCrosshairCode(request.CrosshairCode, out _) ||
                    !DemoTracerBotHiderContract.IsValidClan(request.Clan))
                { reason = "invalid_presentation"; return false; }
            }
            return true;
        }

        // Captures the controller clan before this lease changes it.
        private static bool TryCaptureBases(BotHiderPresentationOverride[] overrides,
            Dictionary<int, ControllerBase>? previous, out Dictionary<int, ControllerBase> bases,
            out string reason)
        {
            bases = new Dictionary<int, ControllerBase>();
            reason = string.Empty;
            foreach (var request in overrides)
            {
                if (request.Clan == null &&
                    (previous == null || !previous.ContainsKey(request.Slot))) continue;
                var player = Utilities.GetPlayerFromSlot(request.Slot);
                if (player is not { IsValid: true } || player.UserId is not int userId)
                { reason = $"controller_unavailable:{request.Slot}"; return false; }
                if (previous != null && previous.TryGetValue(request.Slot, out var prior) &&
                    prior.Incarnation == request.Incarnation &&
                    prior.Handle == player.EntityHandle.Raw && prior.UserId == userId)
                {
                    bases.Add(request.Slot, prior);
                    continue;
                }
                if (request.Clan == null) continue;
                try
                {
                    bases.Add(request.Slot, new ControllerBase(request.Incarnation,
                        player.EntityHandle.Raw, userId,
                        new BotHiderClan(player.Clan ?? string.Empty,
                            Schema.GetRef<uint>(player.Handle, "CCSPlayerController", "m_unClanId32bit"))));
                }
                catch (Exception ex)
                { reason = $"clan_baseline_unavailable:{request.Slot}:{ex.Message}"; return false; }
            }
            return true;
        }

        // Restores the clan of slots dropped by a successful batch replacement.
        private static bool RestoreRemovedClans(Dictionary<int, ControllerBase> previous,
            Dictionary<int, ControllerBase> current)
        {
            bool restored = true;
            foreach (var (slot, baseline) in previous)
                if (!current.ContainsKey(slot)) restored &= RestoreClan(slot, baseline);
            return restored;
        }

        // Removes a replay flair when a retained slot no longer requests one.
        private static bool RestoreDroppedFlairs(HiderApi api,
            BotHiderPresentationOverride[] previous, BotHiderPresentationOverride[] current,
            out string reason)
        {
            reason = string.Empty;
            var next = current.ToDictionary(value => value.Slot);
            foreach (var old in previous)
            {
                if (old.ScoreboardFlair == null || !next.TryGetValue(old.Slot, out var request) ||
                    request.ScoreboardFlair != null) continue;
                var player = Utilities.GetPlayerFromSlot(old.Slot);
                if (player is not { IsValid: true } ||
                    api.GetSlotIncarnation(old.Slot) != old.Incarnation ||
                    !ApplyFlair(player, api.GetScoreboardFlair(old.Slot)))
                { reason = $"scoreboard_flair_restore_failed:{old.Slot}"; return false; }
            }
            return true;
        }

        // Restores captured clans without writing into a replacement controller.
        private static bool RestoreClans(Dictionary<int, ControllerBase> bases)
        {
            bool restored = true;
            foreach (var (slot, baseline) in bases)
                restored &= RestoreClan(slot, baseline);
            return restored;
        }

        // Restores one original controller pair only while its handle and user ID match.
        private static bool RestoreClan(int slot, ControllerBase baseline)
        {
            try
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player is not { IsValid: true } ||
                    player.EntityHandle.Raw != baseline.Handle || player.UserId != baseline.UserId)
                    return true;
                return ApplyClan(player, baseline.Clan);
            }
            catch { return false; }
        }

        // Publishes and verifies both controller clan fields as one pair.
        private static bool ApplyClan(CCSPlayerController player, BotHiderClan clan)
        {
            if (player.Clan != clan.Tag ||
                Schema.GetRef<uint>(player.Handle, "CCSPlayerController", "m_unClanId32bit") != clan.Id)
            {
                player.Clan = clan.Tag;
                Schema.SetSchemaValue(player.Handle, "CCSPlayerController", "m_unClanId32bit", clan.Id);
                Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
                Utilities.SetStateChanged(player, "CCSPlayerController", "m_unClanId32bit");
            }
            return player.Clan == clan.Tag &&
                   Schema.GetRef<uint>(player.Handle, "CCSPlayerController", "m_unClanId32bit") == clan.Id;
        }

        // Applies native identity and controller fields with server-side readback.
        private static bool Apply(HiderApi api, string token,
            BotHiderPresentationOverride[] overrides, Dictionary<int, ControllerBase> bases,
            out string reason)
        {
            reason = string.Empty;
            foreach (var request in overrides)
            {
                int slot = request.Slot;
                if (!api.IsManagedBot(slot) || api.GetSlotIncarnation(slot) != request.Incarnation)
                { reason = $"slot_incarnation_changed:{slot}"; return false; }
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player is not { IsValid: true })
                { reason = $"controller_unavailable:{slot}"; return false; }
                ulong sid = request.SteamId ?? api.GetBaseBotSteamId(slot);
                string name = request.PlayerName ?? api.GetBasePersonaName(slot);
                if (!api.TryPublishIdentity(token, slot, request.Incarnation, sid, name))
                { reason = $"native_identity_rejected:{slot}"; return false; }
                try
                {
                    if (player.PlayerName != name)
                    {
                        player.PlayerName = name;
                        Utilities.SetStateChanged(player, "CBasePlayerController", "m_iszPlayerName");
                    }
                    if (player.SteamID != sid)
                    {
                        Schema.SetSchemaValue(player.Handle, "CBasePlayerController", "m_steamID", sid);
                        Utilities.SetStateChanged(player, "CBasePlayerController", "m_steamID");
                    }
                    string crosshair = request.CrosshairCode ?? api.GetCrosshairCode(slot);
                    if (player.CrosshairCodes != crosshair)
                    {
                        player.CrosshairCodes = crosshair;
                        Utilities.SetStateChanged(player, "CCSPlayerController", "m_szCrosshairCodes");
                    }
                    if (request.ScoreboardFlair is { } flair && !ApplyFlair(player, flair))
                    { reason = $"scoreboard_flair_unavailable:{slot}"; return false; }
                    var clan = request.Clan ?? (bases.TryGetValue(slot, out var baseline)
                        ? baseline.Clan : null);
                    if (clan != null && !ApplyClan(player, clan))
                    { reason = $"clan_write_failed:{slot}"; return false; }
                    if (api.GetSlotIncarnation(slot) != request.Incarnation ||
                        player.PlayerName != name || player.SteamID != sid ||
                        player.CrosshairCodes != crosshair)
                    { reason = $"controller_readback_failed:{slot}"; return false; }
                }
                catch (Exception ex)
                { reason = $"controller_write_failed:{slot}:{ex.Message}"; return false; }
            }
            return true;
        }

        // Publishes changed ranks and checks the retained rank array.
        private static bool ApplyFlair(CCSPlayerController player, uint flair)
        {
            var inventory = player.InventoryServices;
            if (inventory == null || inventory.Rank.Length == 0) return false;
            var ranks = inventory.Rank;
            bool changed = false;
            for (int index = 0; index < ranks.Length; index++)
            {
                if ((uint)ranks[index] == flair) continue;
                ranks[index] = (MedalRank_t)flair;
                changed = true;
            }
            if (changed)
            {
                int offset = Schema.GetSchemaOffset("CCSPlayerController_InventoryServices", "m_rank");
                NativeAPI.SchemaNetworkStateChanged(inventory.__m_pChainEntity.Handle,
                    (uint)offset, uint.MaxValue, uint.MaxValue);
            }
            foreach (var rank in ranks)
                if ((uint)rank != flair) return false;
            return true;
        }

        // Converts a replay batch to native slot-lifetime references.
        private static BotHiderSlotRef[] ToRefs(BotHiderPresentationOverride[] overrides)
            => overrides.Select(value => new BotHiderSlotRef(value.Slot, value.Incarnation)).ToArray();

        private static BotHiderPresentationLeaseResult Success(HiderApi api,
            string token, BotHiderSlotRef[] slots) => new()
        {
            Ok = true,
            LeaseToken = token,
            ProviderEpoch = api.ProviderEpoch,
            Slots = slots.Select(value => value.Slot).ToArray()
        };

        private static BotHiderPresentationLeaseResult Fail(string reason)
            => new() { Reason = reason };

        // Resolves the single shared XBribo BotHider capability.
        private bool TryGetApi(out HiderApi api)
        {
            if (!_resolved)
            {
                _resolved = true;
                try { _api = Capability.Get(); }
                catch { _api = null; }
            }
            api = _api!;
            return api != null;
        }
    }
}
