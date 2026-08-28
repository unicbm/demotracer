/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotRandomizerApi;
using CounterStrikeSharp.API;

namespace DemoTracer;

internal sealed class DemoTracerBotRandomizerPlanSnapshot
{
    private readonly Dictionary<int, BotRandomizerReplayCosmeticPlan> _plans = [];

    internal string Token { get; private set; } = string.Empty;
    internal string ProviderEpoch { get; private set; } = string.Empty;
    internal IReadOnlyDictionary<int, BotRandomizerReplayCosmeticPlan> Plans => _plans;

    internal void Activate(
        string token,
        string providerEpoch,
        IEnumerable<BotRandomizerReplayCosmeticPlan> plans)
    {
        Token = token;
        ProviderEpoch = providerEpoch;
        _plans.Clear();
        foreach (var plan in plans)
            _plans[plan.Slot] = Clone(plan);
    }

    internal void Invalidate()
    {
        Token = string.Empty;
        ProviderEpoch = string.Empty;
        _plans.Clear();
    }

    internal bool TryGet(int slot, ulong subjectSteamId, out BotRandomizerReplayCosmeticPlan plan)
        => _plans.TryGetValue(slot, out plan!) &&
           subjectSteamId != 0 &&
           plan.SubjectSteamId == subjectSteamId;

    internal bool TryBuildRetainedPlan(
        int slot,
        ulong subjectSteamId,
        out BotRandomizerReplayCosmeticPlan plan)
    {
        plan = null!;
        if (!TryGet(slot, subjectSteamId, out var active))
            return false;
        plan = Clone(active);
        return true;
    }

    private static BotRandomizerReplayCosmeticPlan Clone(BotRandomizerReplayCosmeticPlan plan)
        => new()
        {
            Slot = plan.Slot,
            Incarnation = plan.Incarnation,
            SubjectSteamId = plan.SubjectSteamId,
            SpawnTeam = plan.SpawnTeam,
            Agent = new BotRandomizerAgentPlan
            {
                Mode = plan.Agent.Mode,
                ItemDefinitionIndex = plan.Agent.ItemDefinitionIndex,
                ModelPath = plan.Agent.ModelPath
            },
            Knife = CloneItem(plan.Knife),
            Gloves = CloneItem(plan.Gloves),
            MusicKit = plan.MusicKit,
            Weapons = (plan.Weapons ?? []).Select(CloneWeapon).ToArray()
        };

    private static BotRandomizerReplayItem? CloneItem(BotRandomizerReplayItem? item)
        => item is null ? null : CopyItem(item, new BotRandomizerReplayItem());

    private static BotRandomizerReplayWeapon CloneWeapon(BotRandomizerReplayWeapon weapon)
    {
        var clone = CopyItem(weapon, new BotRandomizerReplayWeapon());
        clone.PaintUsesLegacyModel = weapon.PaintUsesLegacyModel;
        clone.Stickers = (weapon.Stickers ?? []).Select(sticker => new BotRandomizerReplaySticker
        {
            Slot = sticker.Slot,
            StickerId = sticker.StickerId,
            Schema = sticker.Schema,
            Wear = sticker.Wear,
            OffsetX = sticker.OffsetX,
            OffsetY = sticker.OffsetY,
            Scale = sticker.Scale,
            Rotation = sticker.Rotation
        }).ToArray();
        clone.Keychains = (weapon.Keychains ?? []).Select(keychain => new BotRandomizerReplayKeychain
        {
            Slot = keychain.Slot,
            KeychainId = keychain.KeychainId,
            Seed = keychain.Seed,
            StickerId = keychain.StickerId,
            Highlight = keychain.Highlight,
            OffsetX = keychain.OffsetX,
            OffsetY = keychain.OffsetY,
            OffsetZ = keychain.OffsetZ
        }).ToArray();
        return clone;
    }

    private static T CopyItem<T>(BotRandomizerReplayItem source, T target)
        where T : BotRandomizerReplayItem
    {
        target.ItemDefinitionIndex = source.ItemDefinitionIndex;
        target.PaintKit = source.PaintKit;
        target.PaintSeed = source.PaintSeed;
        target.PaintWear = source.PaintWear;
        target.Quality = source.Quality;
        target.StattrakCounter = source.StattrakCounter;
        target.OriginalOwnerSteamId = source.OriginalOwnerSteamId;
        target.ItemAccountId = source.ItemAccountId;
        target.ItemId = source.ItemId;
        target.CustomName = source.CustomName;
        return target;
    }
}

public sealed partial class DemoTracerPlugin
{
    private const float BotRandomizerLeaseHeartbeatSeconds = 1.0f;
    private const float BotRandomizerLeaseRetrySeconds = 1.0f;
    private readonly DemoTracerBotRandomizerBridge _botRandomizerBridge = new();
    private readonly DemoTracerBotRandomizerPlanSnapshot _botRandomizerLease = new();
    private string _botRandomizerLeaseSignature = string.Empty;
    private string _lastBotRandomizerLeaseError = string.Empty;
    private float _nextBotRandomizerLeaseHeartbeatAt;
    private float _nextBotRandomizerLeaseRetryAt;
    private int _botRandomizerLeaseTransitionDepth;

    private void BeginBotRandomizerCosmeticLeaseTransition() => _botRandomizerLeaseTransitionDepth++;

    private void EndBotRandomizerCosmeticLeaseTransition()
    {
        if (_botRandomizerLeaseTransitionDepth <= 0 || --_botRandomizerLeaseTransitionDepth > 0)
            return;
        _ = SyncBotRandomizerCosmeticLease(announce: false);
    }

    private void EnsureBotRandomizerCosmeticLease()
    {
        if (_botRandomizerLeaseTransitionDepth > 0)
            return;

        if (!string.IsNullOrWhiteSpace(_botRandomizerLease.Token) &&
            Server.CurrentTime >= _nextBotRandomizerLeaseHeartbeatAt)
        {
            _nextBotRandomizerLeaseHeartbeatAt = Server.CurrentTime + BotRandomizerLeaseHeartbeatSeconds;
            if (!ProviderEpochMatchesActiveBotRandomizerLease() ||
                !_botRandomizerBridge.Heartbeat(_botRandomizerLease.Token))
            {
                InvalidateBotRandomizerCosmeticLease("heartbeat_failed");
            }
        }

        if (Server.CurrentTime >= _nextBotRandomizerLeaseRetryAt &&
            (string.IsNullOrWhiteSpace(_botRandomizerLease.Token) ||
             !string.IsNullOrWhiteSpace(_lastBotRandomizerLeaseError)))
        {
            _ = SyncBotRandomizerCosmeticLease(announce: false);
        }
    }

    private bool SyncBotRandomizerCosmeticLease(bool announce)
    {
        if (_botRandomizerLeaseTransitionDepth > 0)
            return true;

        var provider = _botRandomizerBridge.GetProviderInfo();
        if (provider == null ||
            provider.ApiVersion != BotRandomizerContract.ApiVersion ||
            !provider.Ready || provider.Draining)
        {
            InvalidateBotRandomizerCosmeticLease("provider_unavailable");
            _nextBotRandomizerLeaseRetryAt = Server.CurrentTime + BotRandomizerLeaseRetrySeconds;
            ReportBotRandomizerLeaseError("provider_unavailable", announce);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_botRandomizerLease.ProviderEpoch) &&
            !_botRandomizerLease.ProviderEpoch.Equals(provider.ProviderEpoch, StringComparison.Ordinal))
        {
            InvalidateBotRandomizerCosmeticLease("provider_epoch_changed");
        }

        var requests = BuildBotRandomizerReplayPlans();
        if (requests.Length == 0)
        {
            ReleaseBotRandomizerCosmeticLease("no_replay_plans");
            return true;
        }

        if (RequestsRequireReplayPrebuild(requests) &&
            (!provider.WeaponPrebuildAvailable || !provider.ReplayPlanPrebuildAvailable))
        {
            InvalidateBotRandomizerCosmeticLease("replay_prebuild_unavailable");
            _nextBotRandomizerLeaseRetryAt = Server.CurrentTime + BotRandomizerLeaseRetrySeconds;
            ReportBotRandomizerLeaseError("replay_prebuild_unavailable", announce);
            return false;
        }

        var signature = BuildBotRandomizerPlanSignature(provider.ProviderEpoch, requests);
        if (!string.IsNullOrWhiteSpace(_botRandomizerLease.Token) &&
            signature.Equals(_botRandomizerLeaseSignature, StringComparison.Ordinal))
        {
            _lastBotRandomizerLeaseError = string.Empty;
            return true;
        }

        BotRandomizerReplayPlanResult result;
        if (string.IsNullOrWhiteSpace(_botRandomizerLease.Token))
        {
            result = AcquireBotRandomizerReplayPlan(requests);
        }
        else
        {
            result = _botRandomizerBridge.Replace(_botRandomizerLease.Token, requests);
            if (!result.Ok && result.Reason.Equals("lease_not_found", StringComparison.Ordinal))
            {
                InvalidateBotRandomizerCosmeticLease("lease_not_found");
                result = AcquireBotRandomizerReplayPlan(requests);
            }
        }

        if (!result.Ok || string.IsNullOrWhiteSpace(result.PlanToken))
        {
            if (!ShouldRetainActiveBotRandomizerLeaseAfterSyncFailure(
                    !string.IsNullOrWhiteSpace(_botRandomizerLease.Token),
                    result.Reason))
            {
                _botRandomizerLease.Invalidate();
                _botRandomizerLeaseSignature = string.Empty;
            }
            _nextBotRandomizerLeaseRetryAt = Server.CurrentTime + BotRandomizerLeaseRetrySeconds;
            ReportBotRandomizerLeaseError(result.Reason, announce);
            return false;
        }

        _botRandomizerLease.Activate(result.PlanToken, result.ProviderEpoch, requests);
        _botRandomizerLeaseSignature = signature;
        _lastBotRandomizerLeaseError = string.Empty;
        _nextBotRandomizerLeaseHeartbeatAt = Server.CurrentTime + BotRandomizerLeaseHeartbeatSeconds;
        _nextBotRandomizerLeaseRetryAt = 0.0f;
        foreach (var slot in result.Slots)
            QueueLoadedReplayCosmeticAlignmentForSlot(slot);
        if (announce)
        {
            Server.PrintToConsole(
                $"dtr: BotRandomizer replay cosmetic plan accepted slots={string.Join(',', result.Slots)} " +
                $"apply=next_spawn provider_epoch={result.ProviderEpoch}");
        }
        return true;
    }

    private BotRandomizerReplayPlanResult AcquireBotRandomizerReplayPlan(
        BotRandomizerReplayCosmeticPlan[] requests)
    {
        var result = _botRandomizerBridge.Acquire(BotRandomizerContract.DemoTracerOwner, requests);
        if (!result.Ok && result.Reason.StartsWith("slot_leased:", StringComparison.Ordinal))
        {
            _ = _botRandomizerBridge.ReleaseOwner(BotRandomizerContract.DemoTracerOwner);
            result = _botRandomizerBridge.Acquire(BotRandomizerContract.DemoTracerOwner, requests);
        }
        return result;
    }

    private BotRandomizerReplayCosmeticPlan[] BuildBotRandomizerReplayPlans()
    {
        var plans = new List<BotRandomizerReplayCosmeticPlan>();
        foreach (var pair in _session.LoadedReplays.OrderBy(pair => pair.Key))
        {
            var slot = pair.Key;
            var replay = pair.Value;
            var canWriteReplaySlot = CanWriteReplaySlot(slot);
            var currentPlanAccepted = HasCurrentLoadedReplayCosmeticAlignment(slot, replay);
            if (!ShouldHoldBotRandomizerCosmeticLease(canWriteReplaySlot, currentPlanAccepted))
                continue;

            if (canWriteReplaySlot &&
                _botRandomizerBridge.TryGetManagedBot(slot, out var managed) &&
                BuildBotRandomizerReplayPlan(managed.Slot, managed.Incarnation, replay) is { } plan)
            {
                plans.Add(plan);
                continue;
            }

            if (currentPlanAccepted &&
                _botRandomizerLease.TryBuildRetainedPlan(slot, replay.SteamId, out var retained))
            {
                plans.Add(retained);
            }
        }
        return plans.ToArray();
    }

    private BotRandomizerReplayCosmeticPlan? BuildBotRandomizerReplayPlan(
        int slot,
        ulong incarnation,
        LoadedReplay replay)
    {
        if (slot < 0 || incarnation == 0 || replay.SteamId == 0)
            return null;

        var agent = new BotRandomizerAgentPlan { Mode = BotRandomizerAgentPlanMode.Randomized };
        if (_cosmeticAlignEnabled && _cosmeticAgentsEnabled)
        {
            agent = replay.Cosmetics.Agent is { } replayAgent
                ? new BotRandomizerAgentPlan
                {
                    Mode = BotRandomizerAgentPlanMode.ReplayModel,
                    ItemDefinitionIndex = replayAgent.ItemDefIndex,
                    ModelPath = replayAgent.ModelPath
                }
                : new BotRandomizerAgentPlan { Mode = BotRandomizerAgentPlanMode.PreserveEngineDefault };
        }

        var plan = new BotRandomizerReplayCosmeticPlan
        {
            Slot = slot,
            Incarnation = incarnation,
            SubjectSteamId = replay.SteamId,
            SpawnTeam = replay.ManifestTeam.HasValue
                ? (byte)replay.ManifestTeam.Value
                : (byte)0,
            Agent = agent,
            Knife = _cosmeticAlignEnabled && _weaponAlignEnabled && _cosmeticKnivesEnabled
                ? ToReplayItem(replay.Cosmetics.Knife)
                : null,
            Gloves = _cosmeticAlignEnabled && _weaponAlignEnabled && _cosmeticGlovesEnabled
                ? ToReplayItem(replay.Cosmetics.Glove)
                : null,
            MusicKit = ReplayMusicKitAlignmentAllowed(replay.MusicKitId) ? replay.MusicKitId : null,
            Weapons = BuildReplayWeapons(replay)
        };

        var claimsAnything = plan.Agent.Mode != BotRandomizerAgentPlanMode.Randomized ||
                             plan.Knife is not null || plan.Gloves is not null ||
                             plan.MusicKit is not null || plan.Weapons.Length > 0;
        return claimsAnything ? plan : null;
    }

    private BotRandomizerReplayWeapon[] BuildReplayWeapons(LoadedReplay replay)
    {
        if (!_cosmeticAlignEnabled || !_weaponAlignEnabled || !_cosmeticWeaponsEnabled)
            return [];

        return replay.Cosmetics.Weapons
            .Where(weapon => HasCompleteAuthoritativePaintEvidence(weapon))
            .OrderBy(weapon => weapon.WeaponDefIndex)
            .Select(weapon => new BotRandomizerReplayWeapon
            {
                ItemDefinitionIndex = weapon.WeaponDefIndex,
                PaintKit = weapon.PaintKit,
                PaintSeed = weapon.Seed,
                PaintWear = weapon.Wear,
                Quality = weapon.Quality,
                StattrakCounter = weapon.StattrakCounter,
                OriginalOwnerSteamId = weapon.OriginalOwnerSteamId,
                ItemAccountId = weapon.ItemAccountId,
                ItemId = weapon.ItemId,
                CustomName = _cosmeticNamesEnabled ? weapon.CustomName : null,
                PaintUsesLegacyModel = IsLegacyCosmeticPaint(weapon.WeaponDefIndex, (int)weapon.PaintKit),
                Stickers = _stickerAlignEnabled
                    ? weapon.Stickers.Select(sticker => new BotRandomizerReplaySticker
                    {
                        Slot = sticker.Slot,
                        StickerId = sticker.StickerId,
                        Schema = (uint)sticker.Slot,
                        Wear = sticker.Wear,
                        OffsetX = sticker.OffsetX,
                        OffsetY = sticker.OffsetY,
                        Scale = sticker.Scale,
                        Rotation = sticker.Rotation
                    }).ToArray()
                    : [],
                Keychains = _charmAlignEnabled
                    ? weapon.Charms.Select(charm => new BotRandomizerReplayKeychain
                    {
                        Slot = charm.Slot,
                        KeychainId = charm.CharmId,
                        Seed = charm.Seed is { } seed && seed <= int.MaxValue ? (int)seed : 0,
                        StickerId = charm.StickerId,
                        Highlight = charm.Highlight,
                        OffsetX = charm.OffsetX,
                        OffsetY = charm.OffsetY,
                        OffsetZ = charm.OffsetZ
                    }).ToArray()
                    : []
            })
            .ToArray();
    }

    private BotRandomizerReplayItem? ToReplayItem(ReplayItemCosmetic? item)
    {
        if (item?.ItemDefIndex is not { } itemDefIndex || itemDefIndex <= 0)
            return null;
        return new BotRandomizerReplayItem
        {
            ItemDefinitionIndex = itemDefIndex,
            PaintKit = item.PaintKit,
            PaintSeed = item.Seed,
            PaintWear = item.Wear,
            OriginalOwnerSteamId = item.OriginalOwnerSteamId,
            ItemAccountId = item.ItemAccountId,
            ItemId = item.ItemId,
            CustomName = _cosmeticNamesEnabled ? item.CustomName : null
        };
    }

    private static bool HasCompleteAuthoritativePaintEvidence(ReplayWeaponCosmetic weapon)
        => weapon.WeaponDefIndex > 0 &&
           weapon.PaintKit is > 0 and <= int.MaxValue &&
           weapon.Seed <= int.MaxValue &&
           float.IsFinite(weapon.Wear) && weapon.Wear is >= 0.0f and <= 1.0f;

    internal static bool RequestsRequireReplayPrebuild(IEnumerable<BotRandomizerReplayCosmeticPlan> plans)
        => plans.Any(plan => plan.Knife is not null || (plan.Weapons?.Length ?? 0) > 0);

    internal static bool ShouldHoldBotRandomizerCosmeticLease(
        bool canWriteReplaySlot,
        bool currentPawnCosmeticsAligned)
        => canWriteReplaySlot || currentPawnCosmeticsAligned;

    internal static bool ShouldRetainActiveBotRandomizerLeaseAfterSyncFailure(
        bool hadActiveLease,
        string? reason)
        => hadActiveLease &&
           !string.Equals(reason, "lease_not_found", StringComparison.Ordinal) &&
           !IsBotRandomizerRosterTopologyFailure(reason);

    internal static bool IsBotRandomizerRosterTopologyFailure(string? reason)
        => reason != null &&
           (reason.StartsWith("invalid_spawn_team:", StringComparison.Ordinal) ||
            reason.StartsWith("stale_incarnation:", StringComparison.Ordinal) ||
            reason.StartsWith("slot_not_managed:", StringComparison.Ordinal));

    private static string BuildBotRandomizerPlanSignature(
        string providerEpoch,
        IEnumerable<BotRandomizerReplayCosmeticPlan> plans)
    {
        var canonical = plans.OrderBy(plan => plan.Slot).ToArray();
        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(providerEpoch + "|" + json)));
    }

    private bool ProviderEpochMatchesActiveBotRandomizerLease()
    {
        var provider = _botRandomizerBridge.GetProviderInfo();
        return provider != null && provider.Ready && !provider.Draining &&
               provider.ProviderEpoch.Equals(_botRandomizerLease.ProviderEpoch, StringComparison.Ordinal);
    }

    private void InvalidateBotRandomizerCosmeticLease(string reason)
    {
        var hadActiveLease = !string.IsNullOrWhiteSpace(_botRandomizerLease.Token);
        _botRandomizerLease.Invalidate();
        _botRandomizerLeaseSignature = string.Empty;
        _nextBotRandomizerLeaseHeartbeatAt = 0.0f;
        _nextBotRandomizerLeaseRetryAt = Server.CurrentTime;
        if (hadActiveLease)
            Server.PrintToConsole($"dtr: BotRandomizer replay plan invalidated reason={reason}");
    }

    private void ReleaseBotRandomizerCosmeticLease(string reason)
    {
        if (_botRandomizerLeaseTransitionDepth > 0)
            return;

        var token = _botRandomizerLease.Token;
        _botRandomizerLease.Invalidate();
        _botRandomizerLeaseSignature = string.Empty;
        _lastBotRandomizerLeaseError = string.Empty;
        _nextBotRandomizerLeaseHeartbeatAt = 0.0f;
        _nextBotRandomizerLeaseRetryAt = 0.0f;
        if (string.IsNullOrWhiteSpace(token))
            return;

        if (!_botRandomizerBridge.Release(token))
            _ = _botRandomizerBridge.ReleaseOwner(BotRandomizerContract.DemoTracerOwner);
        Server.PrintToConsole($"dtr: BotRandomizer replay plan released reason={reason}");
    }

    private void ReportBotRandomizerLeaseError(string reason, bool announce)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
        if (announce || !_lastBotRandomizerLeaseError.Equals(reason, StringComparison.Ordinal))
            Server.PrintToConsole($"dtr: BotRandomizer replay plan unavailable: {reason}");
        _lastBotRandomizerLeaseError = reason;
    }

    private string FormatBotRandomizerLeaseStatus()
    {
        var diagnostics = _botRandomizerBridge.GetDiagnostics();
        return
            $"randomizer_plan={(!string.IsNullOrWhiteSpace(_botRandomizerLease.Token) ? "active" : "inactive")}" +
            $" randomizer_plan_slots={_botRandomizerLease.Plans.Count}" +
            $" randomizer_provider_ready={(diagnostics?.Ready == true ? "on" : "off")}";
    }
}
