using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const int CosmeticHeartbeatAttempts = 12;
    private const float CosmeticHeartbeatIntervalSeconds = 0.10f;
    private const int KnifeEntityRefreshFreezeWaitFrames = 8;
    private const int KnifeEntityRestoreAttempts = 3;
    private const float KnifeEntityRefreshMinFreezeSeconds = 0.25f;
    private const string AttributeSetterWindowsSignature = "40 53 55 41 56 48 81 EC 90 00 00 00";
    private const string AttributeSetterLinuxSignature = "55 48 89 E5 41 57 41 56 49 89 FE 41 55 41 54 53 48 89 F3 48 83 EC ? F3 0F 11 85";
    private static readonly (int WeaponDefIndex, int PaintKit)[] BuiltInLegacyCosmeticPaints =
    [
        // Fallback when the compact econ index is absent. These older USP-S
        // finishes use the legacy bodygroup in CS2; bodygroup 0 renders as the
        // plain dark model.
        (61, 277), // USP-S | Stainless
        (61, 339), // USP-S | Caiman
        (61, 504), // USP-S | Kill Confirmed
    ];
    private static readonly Lazy<MemoryFunctionVoid<nint, string, float>?> AttributeSetter = new(CreateAttributeSetter);
    private readonly HashSet<(int WeaponDefIndex, uint PaintKit)> _legacyCosmeticPaints = new();
    private readonly Dictionary<ulong, PlayerCosmeticEvidence> _cosmeticEvidenceByKey = new();
    private readonly Dictionary<int, ulong> _slotCosmeticEvidenceKeys = new();
    private readonly Dictionary<int, int> _cosmeticHeartbeatTokens = new();
    private readonly Dictionary<int, AppliedGloveCosmetic> _appliedGloveCosmetics = new();
    private readonly Dictionary<int, int> _gloveCosmeticTokens = new();
    private readonly Dictionary<int, AppliedKnifeCosmeticBirth> _appliedKnifeCosmeticBirths = new();
    private readonly Dictionary<int, PendingKnifeEntityRefresh> _pendingKnifeEntityRefreshes = new();
    private readonly Dictionary<int, NativeAgentModelCapture> _nativeAgentModels = new();
    private readonly Dictionary<int, NativeAgentRespawnAttempt> _nativeAgentRespawnAttempts = new();
    private readonly HashSet<int> _knifeEntityRefreshUnavailableWarnings = new();
    private bool _cosmeticGiveNamedItemHooked;
    private int _nextCosmeticHeartbeatToken;
    private int _nextGloveCosmeticToken;
    private int _nextKnifeEntityRefreshToken;
    private long _cosmeticLifecycleGeneration;

    private void HookCosmeticGiveNamedItem()
    {
        if (_cosmeticGiveNamedItemHooked)
            return;

        try
        {
            VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveNamedItemPostForCosmetics, HookMode.Post);
            _cosmeticGiveNamedItemHooked = true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: cosmetic GiveNamedItem hook unavailable: {ex.Message}");
        }
    }

    private void UnhookCosmeticGiveNamedItem()
    {
        if (!_cosmeticGiveNamedItemHooked)
            return;

        try
        {
            VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveNamedItemPostForCosmetics, HookMode.Post);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: cosmetic GiveNamedItem unhook failed: {ex.Message}");
        }
        finally
        {
            _cosmeticGiveNamedItemHooked = false;
        }
    }

    private ReplayCosmetics NormalizeReplayCosmetics(ReplayCosmetics? cosmetics)
    {
        var normalized = new ReplayCosmetics();
        if (cosmetics == null)
            return normalized;

        foreach (var group in (cosmetics.Weapons ?? [])
                     .Where(IsValidWeaponCosmetic)
                     .GroupBy(weapon => NormalizeWeaponDefIndex(weapon.WeaponDefIndex)))
        {
            if (!IsWeaponCosmeticDefIndex(group.Key) || group.Count() != 1)
                continue;
            var weapon = group.First();
            if (!IsKnownWeaponCosmeticPaint(group.Key, weapon.PaintKit))
                continue;
            normalized.Weapons.Add(new ReplayWeaponCosmetic
            {
                WeaponDefIndex = group.Key,
                PaintKit = weapon.PaintKit,
                Seed = weapon.Seed,
                Wear = weapon.Wear,
                Quality = NormalizeStattrakQuality(weapon.Quality),
                StattrakCounter = NormalizeStattrakCounter(weapon.StattrakCounter),
                OriginalOwnerSteamId = NormalizeOptionalULong(weapon.OriginalOwnerSteamId),
                ItemAccountId = NormalizeOptionalUInt(weapon.ItemAccountId),
                ItemId = NormalizeOptionalULong(weapon.ItemId),
                CustomName = NormalizeCosmeticCustomName(weapon.CustomName),
                Stickers = NormalizeWeaponStickers(weapon.Stickers),
                Charms = NormalizeWeaponCharms(weapon.Charms)
            });
        }

        if (cosmetics.Knife is { } knife &&
            IsValidItemCosmetic(knife) &&
            knife.ItemDefIndex is { } knifeDef &&
            IsExactKnifeCosmeticDefIndex(knifeDef) &&
            IsKnownKnifeCosmeticItemDefIndex(knifeDef))
        {
            normalized.Knife = CloneItemCosmetic(knife);
        }

        if (cosmetics.Glove is { } glove &&
            IsValidItemCosmetic(glove) &&
            (glove.ItemDefIndex == null ||
             IsKnownGloveCosmeticItemDefIndex(glove.ItemDefIndex.Value)))
        {
            normalized.Glove = CloneItemCosmetic(glove);
        }

        if (NormalizeAgentCosmetic(cosmetics.Agent) is { } agent)
        {
            normalized.Agent = agent;
        }

        normalized.Weapons = normalized.Weapons
            .OrderBy(weapon => weapon.WeaponDefIndex)
            .ToList();
        return normalized;
    }

    private static ReplayItemCosmetic CloneItemCosmetic(ReplayItemCosmetic source)
        => new()
        {
            ItemDefIndex = source.ItemDefIndex,
            PaintKit = source.PaintKit,
            Seed = source.Seed,
            Wear = source.Wear,
            OriginalOwnerSteamId = NormalizeOptionalULong(source.OriginalOwnerSteamId),
            ItemAccountId = NormalizeOptionalUInt(source.ItemAccountId),
            ItemId = NormalizeOptionalULong(source.ItemId),
            CustomName = NormalizeCosmeticCustomName(source.CustomName)
        };

    private ReplayAgentCosmetic? NormalizeAgentCosmetic(ReplayAgentCosmetic? source)
    {
        if (source == null || source.ItemDefIndex == 0 || !IsKnownAgentCosmeticItemDefIndex(source.ItemDefIndex))
            return null;
        var modelPath = NormalizeAgentModelPath(source.ModelPath);
        if (modelPath == null)
            return null;
        return new ReplayAgentCosmetic
        {
            ItemDefIndex = source.ItemDefIndex,
            ModelPath = modelPath,
            Name = NormalizeAgentName(source.Name)
        };
    }

    private static ulong CosmeticEvidenceKey(ulong steamId, int slot)
        => steamId != 0 ? steamId : 18_000_000_000_000_000_000UL + (uint)Math.Clamp(slot, 0, MaxPlayerSlots - 1);

    private void RememberReplayCosmeticEvidence(int slot, LoadedReplay replay)
    {
        var key = CosmeticEvidenceKey(replay.SteamId, slot);
        _slotCosmeticEvidenceKeys[slot] = key;
        if (!_cosmeticEvidenceByKey.TryGetValue(key, out var evidence))
        {
            evidence = new PlayerCosmeticEvidence(replay.SteamId);
            _cosmeticEvidenceByKey[key] = evidence;
        }
        else
        {
            evidence.RememberReplaySteamId(replay.SteamId);
        }

        evidence.Knife = replay.Cosmetics.Knife != null
            ? CloneItemCosmetic(replay.Cosmetics.Knife)
            : null;
        evidence.Glove = replay.Cosmetics.Glove != null
            ? CloneItemCosmetic(replay.Cosmetics.Glove)
            : null;
        evidence.Agent = replay.Cosmetics.Agent != null
            ? NormalizeAgentCosmetic(replay.Cosmetics.Agent)
            : null;
    }

    private static bool HasCosmeticEvidence(ReplayCosmetics? cosmetics)
        => cosmetics != null &&
           ((cosmetics.Weapons?.Count ?? 0) > 0 ||
            cosmetics.Knife != null ||
            cosmetics.Glove != null ||
            cosmetics.Agent != null);

    private bool IsValidWeaponCosmetic(ReplayWeaponCosmetic cosmetic)
        => cosmetic.PaintKit > 0 &&
           IsKnownPaintKit(cosmetic.PaintKit) &&
           cosmetic.Wear is >= 0.0f and <= 1.0f &&
           float.IsFinite(cosmetic.Wear);

    private bool IsValidItemCosmetic(ReplayItemCosmetic? cosmetic)
        => cosmetic != null &&
           cosmetic.PaintKit > 0 &&
           IsKnownPaintKit(cosmetic.PaintKit) &&
           cosmetic.Wear is >= 0.0f and <= 1.0f &&
           float.IsFinite(cosmetic.Wear);

    private static int? NormalizeStattrakQuality(int? quality)
        => quality == 9 ? 9 : null;

    private static int? NormalizeStattrakCounter(int? counter)
        => counter is >= 0 ? counter : null;

    private List<ReplayWeaponSticker> NormalizeWeaponStickers(IEnumerable<ReplayWeaponSticker>? stickers)
    {
        if (stickers == null)
            return [];

        var normalized = new List<ReplayWeaponSticker>();
        var slots = new HashSet<int>();
        foreach (var sticker in stickers)
        {
            if (sticker.Slot is < 0 or > 4 ||
                sticker.StickerId == 0 ||
                !IsKnownStickerId(sticker.StickerId) ||
                sticker.Wear is < 0.0f or > 1.0f ||
                !float.IsFinite(sticker.Wear) ||
                !float.IsFinite(sticker.OffsetX) ||
                !float.IsFinite(sticker.OffsetY) ||
                (sticker.Scale.HasValue && !float.IsFinite(sticker.Scale.Value)) ||
                (sticker.Rotation.HasValue && !float.IsFinite(sticker.Rotation.Value)) ||
                !slots.Add(sticker.Slot))
            {
                return [];
            }

            normalized.Add(new ReplayWeaponSticker
            {
                Slot = sticker.Slot,
                StickerId = sticker.StickerId,
                Wear = sticker.Wear,
                OffsetX = sticker.OffsetX,
                OffsetY = sticker.OffsetY,
                Scale = sticker.Scale,
                Rotation = sticker.Rotation
            });
        }

        return normalized
            .OrderBy(sticker => sticker.Slot)
            .ToList();
    }

    private List<ReplayWeaponCharm> NormalizeWeaponCharms(IEnumerable<ReplayWeaponCharm>? charms)
    {
        if (charms == null)
            return [];

        var normalized = new List<ReplayWeaponCharm>();
        var slots = new HashSet<int>();
        foreach (var charm in charms)
        {
            if (charm.Slot != 0 ||
                charm.CharmId == 0 ||
                !IsKnownKeychainId(charm.CharmId) ||
                !float.IsFinite(charm.OffsetX) ||
                !float.IsFinite(charm.OffsetY) ||
                !float.IsFinite(charm.OffsetZ) ||
                !slots.Add(charm.Slot))
            {
                return [];
            }

            normalized.Add(new ReplayWeaponCharm
            {
                Slot = charm.Slot,
                CharmId = charm.CharmId,
                OffsetX = charm.OffsetX,
                OffsetY = charm.OffsetY,
                OffsetZ = charm.OffsetZ,
                Seed = NormalizeOptionalUInt(charm.Seed),
                Highlight = NormalizeOptionalUInt(charm.Highlight),
                StickerId = NormalizeKnownStickerId(charm.StickerId)
            });
        }

        return normalized
            .OrderBy(charm => charm.Slot)
            .ToList();
    }

    private static uint? NormalizeOptionalUInt(uint? value)
        => value is > 0 ? value : null;

    private uint? NormalizeKnownStickerId(uint? value)
    {
        var normalized = NormalizeOptionalUInt(value);
        return normalized.HasValue && IsKnownStickerId(normalized.Value) ? normalized : null;
    }

    private static ulong? NormalizeOptionalULong(ulong? value)
        => value is > 0 ? value : null;

    private static string? NormalizeCosmeticCustomName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var cleaned = new string(value
            .Trim()
            .Where(ch => !char.IsControl(ch) || ch == '\t')
            .Take(128)
            .ToArray())
            .Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? NormalizeAgentName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var cleaned = value.Trim().ToLowerInvariant();
        if (cleaned.Length is 0 or > 128 ||
            cleaned.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_'))
        {
            return null;
        }
        return cleaned;
    }

    private static string? NormalizeAgentModelPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var path = value.Trim().Replace('/', '\\').ToLowerInvariant();
        if (path.Length is < 24 or > 160 ||
            !path.StartsWith("agents\\models\\", StringComparison.Ordinal) ||
            !path.EndsWith(".vmdl", StringComparison.Ordinal) ||
            path.Contains("..", StringComparison.Ordinal) ||
            path.Contains(':', StringComparison.Ordinal) ||
            path.Contains('\0'))
        {
            return null;
        }

        foreach (var ch in path)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '\\' or '.' or '-'))
                return null;
        }
        return path;
    }

    private static string? NormalizeNativeAgentModelPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var path = value.Trim().Replace('/', '\\').ToLowerInvariant();
        if (path.Length is < 24 or > 192 ||
            !path.StartsWith("characters\\models\\", StringComparison.Ordinal) ||
            !path.EndsWith(".vmdl", StringComparison.Ordinal) ||
            path.Contains("..", StringComparison.Ordinal) ||
            path.Contains(':', StringComparison.Ordinal) ||
            path.Contains('\0'))
        {
            return null;
        }

        foreach (var ch in path)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '\\' or '.' or '-'))
                return null;
        }
        return path;
    }

    private void CaptureNativeAgentModelForSpawn(CCSPlayerController player)
    {
        var slot = player.Slot;
        _nativeAgentModels.Remove(slot);
        if (player is not { IsValid: true, IsBot: true } ||
            player.UserId is not int userId ||
            player.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true } ||
            NormalizeNativeAgentModelPath(
                pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName) is not { } modelPath)
        {
            return;
        }

        _nativeAgentModels[slot] = new NativeAgentModelCapture(
            userId,
            pawn.EntityHandle.Raw,
            player.Team,
            modelPath);
        _nativeAgentRespawnAttempts.Remove(slot);
    }

    private void ResetNativeAgentModelCaptures()
    {
        _nativeAgentModels.Clear();
        _nativeAgentRespawnAttempts.Clear();
    }

    internal static bool IsWeaponCosmeticDefIndex(int weaponDefIndex)
        => IsKnownWeaponDefIndex(weaponDefIndex) &&
           weaponDefIndex is not 42 and not 43 and not 44 and not 45 and not 46 and not 47 and not 48 and not 49;

    private static bool IsKnifeCosmeticDefIndex(int weaponDefIndex)
        => weaponDefIndex is 41 or 42 or 59 || weaponDefIndex is >= 500 and < 600;

    private void ResetCosmeticAlignState(bool resetCounters = false)
    {
        _cosmeticLifecycleGeneration++;
        _cosmeticSyncedSlots.Clear();
        _cosmeticHeartbeatTokens.Clear();
        _activeWeaponCosmetics.Clear();
        _appliedGloveCosmetics.Clear();
        _gloveCosmeticTokens.Clear();
        _appliedKnifeCosmeticBirths.Clear();
        _pendingKnifeEntityRefreshes.Clear();
        _knifeEntityRefreshUnavailableWarnings.Clear();
        if (resetCounters)
        {
            _cosmeticAppliedCount = 0;
            _cosmeticSkippedCount = 0;
        }
    }

    private void ScheduleCosmeticNextFrame(Action callback)
    {
        var generation = _cosmeticLifecycleGeneration;
        Server.NextFrame(() =>
        {
            if (generation != _cosmeticLifecycleGeneration ||
                !_mapActive ||
                _lifecycleResetInProgress)
            {
                return;
            }

            callback();
        });
    }

    private void ResetCosmeticEvidenceCache()
    {
        _slotCosmeticEvidenceKeys.Clear();
        _cosmeticEvidenceByKey.Clear();
        _appliedKnifeCosmeticBirths.Clear();
        _pendingKnifeEntityRefreshes.Clear();
        _knifeEntityRefreshUnavailableWarnings.Clear();
    }

    private void ResetStickerAlignState(bool resetCounters = false)
    {
        if (resetCounters)
        {
            _stickerAppliedCount = 0;
            _stickerSkippedCount = 0;
        }
    }

    private void ResetCharmAlignState(bool resetCounters = false)
    {
        if (resetCounters)
        {
            _charmAppliedCount = 0;
            _charmSkippedCount = 0;
        }
    }

    private string FormatCosmeticStatusCounts()
    {
        var counts = CountLoadedCosmeticEvidence();
        var cached = CountCachedCosmeticEvidence();
        return
            $"cosmetics_evidence={counts.Files} cosmetic_weapons={counts.Weapons} cosmetic_knives={counts.Knives} cosmetic_gloves={counts.Gloves} cosmetic_agents={counts.Agents} cached_players={cached.Players} cached_knives={cached.Knives} cached_gloves={cached.Gloves} cached_agents={cached.Agents} sticker_evidence={counts.Stickers} charm_evidence={counts.Charms} applied={_cosmeticAppliedCount} skipped={_cosmeticSkippedCount} sticker_applied={_stickerAppliedCount} sticker_skipped={_stickerSkippedCount} charm_applied={_charmAppliedCount} charm_skipped={_charmSkippedCount} {FormatBotRandomizerLeaseStatus()}";
    }

    private (int Players, int Knives, int Gloves, int Agents) CountCachedCosmeticEvidence()
    {
        var players = 0;
        var knives = 0;
        var gloves = 0;
        var agents = 0;

        foreach (var evidence in _cosmeticEvidenceByKey.Values)
        {
            if (evidence.IsEmpty)
                continue;

            players++;
            if (evidence.Knife != null)
                knives++;
            if (evidence.Glove != null)
                gloves++;
            if (evidence.Agent != null)
                agents++;
        }

        return (players, knives, gloves, agents);
    }

    private (int Files, int Weapons, int Knives, int Gloves, int Agents, int Stickers, int Charms) CountLoadedCosmeticEvidence()
    {
        var files = 0;
        var weapons = 0;
        var knives = 0;
        var gloves = 0;
        var agents = 0;
        var stickers = 0;
        var charms = 0;

        foreach (var replay in _loadedReplays.Values)
        {
            if (!HasCosmeticEvidence(replay.Cosmetics))
                continue;

            files++;
            weapons += replay.Cosmetics.Weapons.Count;
            stickers += replay.Cosmetics.Weapons.Sum(weapon => weapon.Stickers.Count);
            charms += replay.Cosmetics.Weapons.Sum(weapon => weapon.Charms.Count);
            if (replay.Cosmetics.Knife != null)
                knives++;
            if (replay.Cosmetics.Glove != null)
                gloves++;
            if (replay.Cosmetics.Agent != null)
                agents++;
        }

        return (files, weapons, knives, gloves, agents, stickers, charms);
    }

    private void ApplyLoadedReplayCosmeticsForSlot(int slot, LoadedReplay replay)
    {
        if (!IsReplaySlotStillSafe(slot))
        {
            return;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
            return;
        if (!_botRandomizerLease.TryGet(slot, replay.SteamId, out _))
        {
            return;
        }

        var applied = 0;
        var skipped = 0;
        if (replay.Cosmetics.Agent is { } agentCosmetic)
        {
            if (TryApplyAgentCosmetic(player, pawn, agentCosmetic, replay.SteamId))
                applied++;
            else
                skipped++;
        }
        else if (TryRestoreNativeAgentModel(player, pawn, replay.SteamId))
            applied++;
        else
            skipped++;

        if (_weaponAlignEnabled && WeaponCosmeticFeatureEnabled())
        {
            foreach (var cosmetic in replay.Cosmetics.Weapons)
            {
                if (TryFindReplayWeaponByDefIndex(pawn, cosmetic.WeaponDefIndex, out var weapon) &&
                    TryApplyWeaponCosmetic(player, weapon, cosmetic, replay.SteamId))
                {
                    applied++;
                    ScheduleReplayWeaponCosmeticRetry(slot, cosmetic, framesRemaining: 3);
                }
                else
                {
                    skipped++;
                }
            }
        }

        if (replay.Cosmetics.Knife is { } knifeCosmetic)
        {
            var appliedKnife = false;
            if (TryFindReplayKnife(pawn, out var knife) &&
                TryApplyItemCosmetic(
                    player,
                    knife,
                    knifeCosmetic,
                    replay.SteamId,
                    DemoTracerCosmeticWriteField.Knife,
                    allowSubclassChange: true,
                    applyPaint: true,
                    applyCustomName: _cosmeticNamesEnabled))
            {
                applied++;
                appliedKnife = true;
            }
            else
            {
                skipped++;
            }
            if (_loadedReplays.ContainsKey(slot))
                ScheduleReplayKnifeCosmeticRetry(slot, knifeCosmetic, framesRemaining: appliedKnife ? 2 : 4);
            else
                ScheduleCachedKnifeCosmeticRetry(slot, knifeCosmetic, replay.SteamId, framesRemaining: appliedKnife ? 2 : 4);
            ScheduleReplayKnifeEntityRefresh(slot, knifeCosmetic);
        }
        else
        {
            var defaultKnife = BuildDefaultKnifeCosmetic(player);
            if (TryFindReplayKnife(pawn, out var knife) &&
                TryClearKnifeCosmetic(player, knife, replay.SteamId))
            {
                applied++;
            }
            else
                skipped++;
            ScheduleReplayKnifeEntityRefresh(slot, defaultKnife);
        }

        if (replay.Cosmetics.Glove is { } gloveCosmetic)
        {
            if (TryApplyGloveCosmetic(player, pawn, gloveCosmetic, replay.SteamId, out var changed))
            {
                if (changed)
                    applied++;
            }
            else
                skipped++;
        }
        else if (TryClearGloveCosmetic(player, pawn, replay.SteamId, out var gloveChanged))
        {
            if (gloveChanged)
                applied++;
        }
        else
            skipped++;

        _cosmeticAppliedCount += applied;
        _cosmeticSkippedCount += skipped;
        if (applied > 0)
        {
            _cosmeticSyncedSlots.Add(slot);
            Server.PrintToConsole(
                $"dtr: cosmetic aligned slot={slot} player={replay.PlayerName} applied={applied} skipped={skipped}");
        }

        if (WeaponCosmeticFeatureEnabled() && replay.Cosmetics.Weapons.Count > 0)
            ScheduleReplayCosmeticHeartbeat(slot);
    }

    private void ScheduleReplayCosmeticHeartbeat(int slot)
    {
        var token = ++_nextCosmeticHeartbeatToken;
        _cosmeticHeartbeatTokens[slot] = token;
        AddTimer(
            CosmeticHeartbeatIntervalSeconds,
            () => RunReplayCosmeticHeartbeat(slot, token, CosmeticHeartbeatAttempts),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void RunReplayCosmeticHeartbeat(int slot, int token, int attemptsRemaining)
    {
        if (attemptsRemaining <= 0 ||
            !_cosmeticHeartbeatTokens.TryGetValue(slot, out var activeToken) ||
            activeToken != token)
        {
            return;
        }

        if (!WeaponCosmeticFeatureEnabled() ||
            !_weaponAlignEnabled ||
            !_loadedReplays.TryGetValue(slot, out var replay) ||
            !HasCosmeticEvidence(replay.Cosmetics) ||
            !IsReplaySlotStillSafe(slot))
        {
            _cosmeticHeartbeatTokens.Remove(slot);
            return;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is { IsValid: true, PawnIsAlive: true } && pawn is { IsValid: true })
        {
            var activeWeapon = pawn.WeaponServices?.ActiveWeapon.Value;
            if (activeWeapon is { IsValid: true })
                ApplyActiveReplayWeaponCosmeticForSlot(
                    slot,
                    WeaponDefIndex(activeWeapon),
                    force: false,
                    scheduleNextFrame: true);
        }

        if (attemptsRemaining == 1)
        {
            _cosmeticHeartbeatTokens.Remove(slot);
            return;
        }

        AddTimer(
            CosmeticHeartbeatIntervalSeconds,
            () => RunReplayCosmeticHeartbeat(slot, token, attemptsRemaining - 1),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ScheduleReplayWeaponCosmeticRetry(
        int slot,
        ReplayWeaponCosmetic cosmetic,
        int framesRemaining)
    {
        if (framesRemaining <= 0)
            return;

        ScheduleCosmeticNextFrame(() =>
        {
            if (!WeaponCosmeticFeatureEnabled() || !_weaponAlignEnabled || !IsReplaySlotStillSafe(slot))
                return;

            var refreshedPlayer = Utilities.GetPlayerFromSlot(slot);
            var refreshedPawn = refreshedPlayer?.PlayerPawn.Value;
            if (refreshedPlayer is not { IsValid: true, PawnIsAlive: true } ||
                refreshedPawn is not { IsValid: true })
            {
                return;
            }

            ApplyActiveReplayWeaponCosmeticForSlot(
                slot,
                cosmetic.WeaponDefIndex,
                force: false,
                scheduleNextFrame: true);

            ScheduleReplayWeaponCosmeticRetry(slot, cosmetic, framesRemaining - 1);
        });
    }

    private void ScheduleReplayKnifeCosmeticRetry(
        int slot,
        ReplayItemCosmetic cosmetic,
        int framesRemaining)
    {
        if (framesRemaining <= 0)
            return;

        ScheduleCosmeticNextFrame(() =>
        {
            if (!IsReplaySlotStillSafe(slot) ||
                !_loadedReplays.TryGetValue(slot, out var replay))
            {
                return;
            }

            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
                return;

            if (TryFindReplayKnife(pawn, out var knife) &&
                TryApplyItemCosmetic(
                    player,
                    knife,
                    cosmetic,
                    replay.SteamId,
                    DemoTracerCosmeticWriteField.Knife,
                    allowSubclassChange: true,
                    applyPaint: true,
                    applyCustomName: _cosmeticNamesEnabled))
            {
                if (framesRemaining <= 2)
                    return;
            }

            ScheduleReplayKnifeCosmeticRetry(slot, cosmetic, framesRemaining - 1);
        });
    }

    private void ScheduleReplayKnifeEntityRefresh(int slot, ReplayItemCosmetic cosmetic)
    {
        // On the first physical round the default knife can predate replay
        // evidence. Late econ writes fix HUD/animations but not the initialized
        // model/material, so rebuild that one stale entity during freeze only.
        if (!_cosmeticGiveNamedItemHooked)
        {
            if (_knifeEntityRefreshUnavailableWarnings.Add(slot))
            {
                Server.PrintToConsole(
                    $"dtr: knife entity refresh unavailable slot={slot}: GiveNamedItem hook is not installed");
            }
            return;
        }

        var fingerprint = KnifeCosmeticFingerprint.From(cosmetic);
        var replayGeneration = CurrentReplayIdentityGeneration(slot);
        if (!_loadedReplays.TryGetValue(slot, out var replay) ||
            !HasActiveBotRandomizerClaim(
                slot,
                replay.SteamId,
                DemoTracerCosmeticWriteField.Knife))
        {
            return;
        }
        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is { IsValid: true, PawnIsAlive: true } &&
            pawn is { IsValid: true } &&
            TryFindReplayKnife(pawn, out var knife) &&
            IsKnifeCosmeticBirthCurrent(slot, knife, fingerprint))
        {
            _pendingKnifeEntityRefreshes.Remove(slot);
            return;
        }

        if (_pendingKnifeEntityRefreshes.TryGetValue(slot, out var existing) &&
            existing.Fingerprint == fingerprint &&
            existing.ReplayGeneration == replayGeneration)
        {
            return;
        }

        var pending = new PendingKnifeEntityRefresh(
            ++_nextKnifeEntityRefreshToken,
            fingerprint,
            replayGeneration,
            CloneItemCosmetic(cosmetic));
        _pendingKnifeEntityRefreshes[slot] = pending;
        Server.NextFrame(
            () => TryBeginReplayKnifeEntityRefresh(
                slot,
                pending,
                KnifeEntityRefreshFreezeWaitFrames));
    }

    private void TryBeginReplayKnifeEntityRefresh(
        int slot,
        PendingKnifeEntityRefresh pending,
        int freezeWaitFramesRemaining)
    {
        if (!IsKnifeEntityRefreshPending(slot, pending))
            return;

        if (!_cosmeticGiveNamedItemHooked ||
            !_loadedReplays.TryGetValue(slot, out var replay) ||
            !TryValidateBotRandomizerClaim(
                slot,
                replay.SteamId,
                DemoTracerCosmeticWriteField.Knife) ||
            !IsReplayIdentityGenerationCurrent(slot, pending.ReplayGeneration) ||
            IsReplaySlotPlaying(slot) ||
            !IsReplaySlotStillSafe(slot))
        {
            CancelKnifeEntityRefresh(slot, pending);
            return;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            pawn is not { IsValid: true } ||
            !TryResolveDesiredReplayKnifeCosmetic(player, replay, out var currentCosmetic) ||
            KnifeCosmeticFingerprint.From(currentCosmetic) != pending.Fingerprint)
        {
            CancelKnifeEntityRefresh(slot, pending);
            return;
        }

        if (!TryFindReplayKnife(pawn, out var knife))
        {
            RetryKnifeEntityRefreshDuringFreeze(
                slot,
                pending,
                freezeWaitFramesRemaining,
                "knife entity was not found");
            return;
        }

        if (IsKnifeCosmeticBirthCurrent(slot, knife, pending.Fingerprint))
        {
            CancelKnifeEntityRefresh(slot, pending);
            return;
        }

        if (!TryReadFreezePhaseRemaining(out var freezeRemaining, out var freezeReason) ||
            freezeRemaining < KnifeEntityRefreshMinFreezeSeconds)
        {
            var reason = string.IsNullOrWhiteSpace(freezeReason)
                ? $"freeze period has only {freezeRemaining:0.000}s remaining"
                : freezeReason;
            RetryKnifeEntityRefreshDuringFreeze(
                slot,
                pending,
                freezeWaitFramesRemaining,
                reason);
            return;
        }

        var controllerEntityHandle = player.EntityHandle.Raw;
        var pawnEntityHandle = pawn.EntityHandle.Raw;
        var oldKnifeEntityHandle = knife.EntityHandle.Raw;
        if (!DropAndKillReplayWeapon(player, pawn, knife, "cosmetic_birth_refresh"))
        {
            CancelKnifeEntityRefresh(slot, pending);
            Server.PrintToConsole($"dtr: knife entity refresh failed slot={slot}: could not drop the stale knife");
            return;
        }

        Server.PrintToConsole(
            $"dtr: refreshing stale knife entity during freeze slot={slot} handle=0x{oldKnifeEntityHandle:X}");
        ScheduleReplayKnifeEntityRestore(
            slot,
            pending,
            controllerEntityHandle,
            pawnEntityHandle,
            oldKnifeEntityHandle);
    }

    private void RetryKnifeEntityRefreshDuringFreeze(
        int slot,
        PendingKnifeEntityRefresh pending,
        int framesRemaining,
        string reason)
    {
        if (framesRemaining > 0)
        {
            Server.NextFrame(
                () => TryBeginReplayKnifeEntityRefresh(slot, pending, framesRemaining - 1));
            return;
        }

        CancelKnifeEntityRefresh(slot, pending);
        Server.PrintToConsole($"dtr: knife entity refresh skipped slot={slot}: {reason}");
    }

    private void ScheduleReplayKnifeEntityRestore(
        int slot,
        PendingKnifeEntityRefresh pending,
        uint controllerEntityHandle,
        uint pawnEntityHandle,
        uint oldKnifeEntityHandle)
    {
        // Drop cleanup runs two frames later. Give the replacement only after
        // the old full entity handle has had a frame to leave the inventory.
        Server.NextFrame(() => Server.NextFrame(() => Server.NextFrame(() =>
            RestoreReplayKnifeEntity(
                slot,
                pending,
                controllerEntityHandle,
                pawnEntityHandle,
                oldKnifeEntityHandle,
                KnifeEntityRestoreAttempts,
                replacementIssued: false))));
    }

    private void RestoreReplayKnifeEntity(
        int slot,
        PendingKnifeEntityRefresh pending,
        uint controllerEntityHandle,
        uint pawnEntityHandle,
        uint oldKnifeEntityHandle,
        int attemptsRemaining,
        bool replacementIssued)
    {
        // Once the stale knife is dropped, restoring a default knife is recovery
        // debt even if replay ownership changes. Exact controller and pawn handles
        // prevent a recycled slot from receiving it; the GiveNamedItem hook alone
        // decides whether current replay evidence may be applied.
        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.EntityHandle.Raw != controllerEntityHandle ||
            pawn is not { IsValid: true } ||
            pawn.EntityHandle.Raw != pawnEntityHandle)
        {
            CancelKnifeEntityRefresh(slot, pending);
            return;
        }

        if (TryFindReplayKnife(pawn, out var knife))
        {
            if (knife.EntityHandle.Raw == oldKnifeEntityHandle)
            {
                RetryReplayKnifeEntityRestore(
                    slot,
                    pending,
                    controllerEntityHandle,
                    pawnEntityHandle,
                    oldKnifeEntityHandle,
                    attemptsRemaining,
                    replacementIssued,
                    "stale knife handle is still present");
                return;
            }

            if (_loadedReplays.TryGetValue(slot, out var replay) &&
                IsReplayIdentityGenerationCurrent(slot, pending.ReplayGeneration) &&
                TryValidateBotRandomizerClaim(
                    slot,
                    replay.SteamId,
                    DemoTracerCosmeticWriteField.Knife))
            {
                var applied = replay.Cosmetics.Knife is { } knifeCosmetic
                    ? TryApplyItemCosmetic(
                        player,
                        knife,
                        knifeCosmetic,
                        replay.SteamId,
                        DemoTracerCosmeticWriteField.Knife,
                        allowSubclassChange: true,
                        applyPaint: true,
                        applyCustomName: _cosmeticNamesEnabled)
                    : TryClearKnifeCosmetic(player, knife, replay.SteamId);
                if (applied)
                    RememberKnifeCosmeticBirth(slot, knife, pending.DesiredCosmetic);
            }
            CancelKnifeEntityRefresh(slot, pending);
            if (IsReplaySlotStillSafe(slot) &&
                !IsReplaySlotPlaying(slot) &&
                _replayIdentityGenerationBySlot.TryGetValue(slot, out var currentGeneration))
            {
                Server.NextFrame(
                    () => RestoreReplayStartWeaponAfterKnifeRefresh(
                        slot,
                        currentGeneration));
            }
            return;
        }

        if (attemptsRemaining <= 0)
        {
            CancelKnifeEntityRefresh(slot, pending);
            Server.PrintToConsole($"dtr: knife entity restore failed slot={slot}: replacement did not appear");
            return;
        }

        if (replacementIssued)
        {
            Server.NextFrame(() => RestoreReplayKnifeEntity(
                slot,
                pending,
                controllerEntityHandle,
                pawnEntityHandle,
                oldKnifeEntityHandle,
                attemptsRemaining - 1,
                replacementIssued: true));
            return;
        }

        var defaultKnife = DefaultKnifeClassNameForPlayer(player);
        if (TryGiveNamedItem(player, defaultKnife))
        {
            Server.NextFrame(() => RestoreReplayKnifeEntity(
                slot,
                pending,
                controllerEntityHandle,
                pawnEntityHandle,
                oldKnifeEntityHandle,
                attemptsRemaining,
                replacementIssued: true));
            return;
        }

        RetryReplayKnifeEntityRestore(
            slot,
            pending,
            controllerEntityHandle,
            pawnEntityHandle,
            oldKnifeEntityHandle,
            attemptsRemaining,
            replacementIssued: false,
            $"GiveNamedItem({defaultKnife}) failed");
    }

    private void RetryReplayKnifeEntityRestore(
        int slot,
        PendingKnifeEntityRefresh pending,
        uint controllerEntityHandle,
        uint pawnEntityHandle,
        uint oldKnifeEntityHandle,
        int attemptsRemaining,
        bool replacementIssued,
        string reason)
    {
        if (attemptsRemaining > 1)
        {
            Server.NextFrame(() => RestoreReplayKnifeEntity(
                slot,
                pending,
                controllerEntityHandle,
                pawnEntityHandle,
                oldKnifeEntityHandle,
                attemptsRemaining - 1,
                replacementIssued));
            return;
        }

        CancelKnifeEntityRefresh(slot, pending);
        Server.PrintToConsole($"dtr: knife entity restore failed slot={slot}: {reason}");
    }

    private void RestoreReplayStartWeaponAfterKnifeRefresh(int slot, long replayGeneration)
    {
        if (!_weaponAlignEnabled ||
            !IsReplayIdentityGenerationCurrent(slot, replayGeneration) ||
            !_loadedReplays.TryGetValue(slot, out var replay) ||
            !IsReplaySlotStillSafe(slot) ||
            IsReplaySlotPlaying(slot))
        {
            return;
        }

        ApplyReplayWeaponPreset(
            slot,
            ChooseStartWeaponDef(replay),
            allowSlotReplacement: true,
            force: true);
    }

    private bool IsKnifeEntityRefreshPending(int slot, PendingKnifeEntityRefresh pending)
        => _pendingKnifeEntityRefreshes.TryGetValue(slot, out var current) && current == pending;

    private void CancelKnifeEntityRefresh(int slot, PendingKnifeEntityRefresh pending)
    {
        if (IsKnifeEntityRefreshPending(slot, pending))
            _pendingKnifeEntityRefreshes.Remove(slot);
    }

    private bool IsKnifeCosmeticBirthCurrent(
        int slot,
        CBasePlayerWeapon knife,
        KnifeCosmeticFingerprint fingerprint)
        => _appliedKnifeCosmeticBirths.TryGetValue(slot, out var applied) &&
           applied.WeaponEntityHandle == knife.EntityHandle.Raw &&
           applied.Fingerprint == fingerprint;

    private void RememberKnifeCosmeticBirth(
        int slot,
        CBasePlayerWeapon knife,
        ReplayItemCosmetic cosmetic)
    {
        var fingerprint = KnifeCosmeticFingerprint.From(cosmetic);
        _appliedKnifeCosmeticBirths[slot] = new AppliedKnifeCosmeticBirth(
            knife.EntityHandle.Raw,
            fingerprint);
        if (_pendingKnifeEntityRefreshes.TryGetValue(slot, out var pending) &&
            pending.Fingerprint == fingerprint &&
            IsReplayIdentityGenerationCurrent(slot, pending.ReplayGeneration))
        {
            _pendingKnifeEntityRefreshes.Remove(slot);
        }
    }

    private void ForgetKnifeCosmeticBirthForEntity(CEntityInstance entity)
    {
        uint entityHandle;
        try
        {
            entityHandle = entity.EntityHandle.Raw;
        }
        catch
        {
            return;
        }
        if (entityHandle == uint.MaxValue)
            return;

        foreach (var slot in _appliedKnifeCosmeticBirths
                     .Where(entry => entry.Value.WeaponEntityHandle == entityHandle)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _appliedKnifeCosmeticBirths.Remove(slot);
        }
    }

    private static string DefaultKnifeClassNameForPlayer(CCSPlayerController player)
        => player.Team == CsTeam.Terrorist ? "weapon_knife_t" : "weapon_knife";

    private static bool TryResolveDesiredReplayKnifeCosmetic(
        CCSPlayerController player,
        LoadedReplay replay,
        out ReplayItemCosmetic cosmetic)
    {
        cosmetic = replay.Cosmetics.Knife is { } evidence
            ? evidence
            : BuildDefaultKnifeCosmetic(player);
        return true;
    }

    private void ScheduleCachedKnifeCosmeticRetry(
        int slot,
        ReplayItemCosmetic cosmetic,
        ulong replaySteamId,
        int framesRemaining)
    {
        if (framesRemaining <= 0)
            return;

        ScheduleCosmeticNextFrame(() =>
        {
            if (!IsReplaySlotStillSafe(slot))
                return;

            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
                return;

            if (TryFindReplayKnife(pawn, out var knife) &&
                TryApplyItemCosmetic(
                    player,
                    knife,
                    cosmetic,
                    replaySteamId,
                    DemoTracerCosmeticWriteField.Knife,
                    allowSubclassChange: true,
                    applyPaint: true,
                    applyCustomName: _cosmeticNamesEnabled))
            {
                if (framesRemaining <= 2)
                    return;
            }

            ScheduleCachedKnifeCosmeticRetry(slot, cosmetic, replaySteamId, framesRemaining - 1);
        });
    }

    private void ScheduleCachedCosmeticRepairForSlot(int slot)
    {
        if (!_loadedReplays.ContainsKey(slot))
            return;

        AddTimer(0.05f, () => ApplyCachedCosmeticsForSlot(slot), TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(0.20f, () => ApplyCachedCosmeticsForSlot(slot), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ApplyCachedCosmeticsForSlot(int slot)
    {
        if (_loadedReplays.TryGetValue(slot, out var replay))
            ApplyLoadedReplayCosmeticsForSlot(slot, replay);
    }

    private bool TryResolvePlayerCosmeticEvidence(
        int slot,
        CCSPlayerController player,
        out ulong key,
        out PlayerCosmeticEvidence evidence)
    {
        if (_loadedReplays.TryGetValue(slot, out var replay))
        {
            key = CosmeticEvidenceKey(replay.SteamId, slot);
            if (_cosmeticEvidenceByKey.TryGetValue(key, out evidence!))
                return true;

            key = 0;
            evidence = null!;
            return false;
        }

        // After a replay releases, SteamID can be stale while bots are reused.
        // Only an explicit slot binding may drive cached knife/glove/agent repair.
        if (_slotCosmeticEvidenceKeys.TryGetValue(slot, out key) &&
            _cosmeticEvidenceByKey.TryGetValue(key, out evidence!))
        {
            return true;
        }

        key = 0;
        evidence = null!;
        return false;
    }

    private bool TryGetWeaponCosmeticForSlot(
        int slot,
        int weaponDefIndex,
        out ReplayWeaponCosmetic cosmetic,
        out ulong replaySteamId)
    {
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (_loadedReplays.TryGetValue(slot, out var replay) &&
            TryFindReplayWeaponCosmetic(replay, normalized, out cosmetic))
        {
            replaySteamId = replay.SteamId;
            return true;
        }

        replaySteamId = 0;
        cosmetic = null!;
        return false;
    }

    private bool TryGetKnifeCosmeticForSlot(
        int slot,
        CCSPlayerController player,
        out ReplayItemCosmetic cosmetic,
        out ulong replaySteamId)
    {
        if (_loadedReplays.TryGetValue(slot, out var replay) &&
            replay.Cosmetics.Knife != null)
        {
            cosmetic = replay.Cosmetics.Knife;
            replaySteamId = replay.SteamId;
            return true;
        }

        if (TryResolvePlayerCosmeticEvidence(slot, player, out _, out var evidence) &&
            evidence.Knife != null)
        {
            cosmetic = evidence.Knife;
            replaySteamId = evidence.ReplaySteamId;
            return true;
        }

        replaySteamId = 0;
        cosmetic = null!;
        return false;
    }

    private bool TryGetGloveCosmeticForSlot(
        int slot,
        CCSPlayerController player,
        out ReplayItemCosmetic cosmetic,
        out ulong replaySteamId)
    {
        if (_loadedReplays.TryGetValue(slot, out var replay) &&
            replay.Cosmetics.Glove != null)
        {
            cosmetic = replay.Cosmetics.Glove;
            replaySteamId = replay.SteamId;
            return true;
        }

        if (TryResolvePlayerCosmeticEvidence(slot, player, out _, out var evidence) &&
            evidence.Glove != null)
        {
            cosmetic = evidence.Glove;
            replaySteamId = evidence.ReplaySteamId;
            return true;
        }

        replaySteamId = 0;
        cosmetic = null!;
        return false;
    }

    private bool TryGetAgentCosmeticForSlot(
        int slot,
        CCSPlayerController player,
        out ReplayAgentCosmetic cosmetic,
        out ulong replaySteamId)
    {
        if (_loadedReplays.TryGetValue(slot, out var replay) &&
            replay.Cosmetics.Agent != null)
        {
            cosmetic = replay.Cosmetics.Agent;
            replaySteamId = replay.SteamId;
            return true;
        }

        if (TryResolvePlayerCosmeticEvidence(slot, player, out _, out var evidence) &&
            evidence.Agent != null)
        {
            cosmetic = evidence.Agent;
            replaySteamId = evidence.ReplaySteamId;
            return true;
        }

        replaySteamId = 0;
        cosmetic = null!;
        return false;
    }

    private void ApplyReplayWeaponCosmeticForSlot(int slot, int weaponDefIndex)
    {
        _ = TryApplyReplayWeaponCosmeticForSlot(
            slot,
            weaponDefIndex,
            activeOnly: false,
            forceActive: false,
            countResult: true);
    }

    private void ApplyActiveReplayWeaponCosmeticForSlot(
        int slot,
        int weaponDefIndex,
        bool force,
        bool scheduleNextFrame,
        TickPlayerSnapshot? playerSnapshot = null)
    {
        if (TryApplyReplayWeaponCosmeticForSlot(
                slot,
                weaponDefIndex,
                activeOnly: true,
                forceActive: force,
                countResult: false,
                playerSnapshot: playerSnapshot) &&
            scheduleNextFrame)
        {
            ScheduleActiveReplayWeaponCosmeticNextFrame(slot, NormalizeWeaponDefIndex(weaponDefIndex));
        }
    }

    private void ScheduleActiveReplayWeaponCosmeticNextFrame(int slot, int weaponDefIndex)
    {
        ScheduleCosmeticNextFrame(() =>
            ApplyActiveReplayWeaponCosmeticForSlot(
                slot,
                weaponDefIndex,
                force: true,
                scheduleNextFrame: false));
    }

    private bool TryApplyReplayWeaponCosmeticForSlot(
        int slot,
        int weaponDefIndex,
        bool activeOnly,
        bool forceActive,
        bool countResult,
        TickPlayerSnapshot? playerSnapshot = null)
    {
        if (!WeaponCosmeticFeatureEnabled() ||
            !_loadedReplays.TryGetValue(slot, out var replay) ||
            !HasCosmeticEvidence(replay.Cosmetics))
        {
            return false;
        }
        if (playerSnapshot != null)
        {
            if (!IsReplaySlotStillSafe(slot, playerSnapshot))
                return false;
        }
        else if (!IsReplaySlotStillSafe(slot))
        {
            return false;
        }

        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        var cosmetic = replay.Cosmetics.Weapons
            .FirstOrDefault(weapon => weapon.WeaponDefIndex == normalized);
        if (cosmetic == null)
            return false;

        CCSPlayerController? player;
        if (playerSnapshot != null)
        {
            if (!playerSnapshot.TryGetSlot(slot, out var snapshotPlayer))
                return false;
            player = snapshotPlayer;
        }
        else
        {
            player = Utilities.GetPlayerFromSlot(slot);
        }
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
            return false;

        var isActiveWeapon = TryFindActiveReplayWeaponByDefIndex(pawn, normalized, out var weapon);
        if (!isActiveWeapon && activeOnly)
            return false;
        if (!isActiveWeapon && !TryFindReplayWeaponByDefIndex(pawn, normalized, out weapon))
            return false;

        var weaponHandle = weapon.Handle;
        if (isActiveWeapon &&
            !forceActive &&
            _activeWeaponCosmetics.TryGetValue(slot, out var applied) &&
            applied.WeaponDefIndex == normalized &&
            applied.WeaponHandle == weaponHandle)
        {
            return false;
        }

        var ok = TryApplyWeaponCosmetic(player, weapon, cosmetic, replay.SteamId, countStickerStats: countResult);
        if (ok)
        {
            if (isActiveWeapon)
                _activeWeaponCosmetics[slot] = new AppliedActiveWeaponCosmetic(normalized, weaponHandle);
            if (countResult)
                _cosmeticAppliedCount++;
            return true;
        }

        if (countResult)
            _cosmeticSkippedCount++;
        return false;
    }

    private HookResult OnGiveNamedItemPostForCosmetics(DynamicHook hook)
    {
        try
        {
            if (_loadedReplays.Count == 0)
                return HookResult.Continue;

            var itemServices = hook.GetParam<CCSPlayer_ItemServices>(0);
            var weapon = hook.GetReturn<CBasePlayerWeapon>();
            if (weapon == null || !weapon.IsValid)
                return HookResult.Continue;

            if (!TryFindReplayPlayerByItemServices(itemServices, out var slot, out _))
                return HookResult.Continue;

            var weaponEntityHandle = weapon.EntityHandle.Raw;
            if (weaponEntityHandle != Utilities.InvalidEHandleIndex)
            {
                ScheduleGivenWeaponCosmeticNextFrame(
                    slot,
                    weaponEntityHandle,
                    countResult: true,
                    recordKnifeBirth: true);
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: cosmetic GiveNamedItem post failed: {ex.Message}");
        }

        return HookResult.Continue;
    }

    private void ScheduleGivenWeaponCosmeticNextFrame(
        int slot,
        uint weaponEntityHandle,
        bool countResult,
        bool recordKnifeBirth = false)
    {
        ScheduleCosmeticNextFrame(() =>
        {
            if (!TryResolveOwnedReplayWeapon(slot, weaponEntityHandle, out var player, out var weapon))
            {
                return;
            }

            TryApplyGivenWeaponCosmetic(
                slot,
                player,
                weapon,
                countResult,
                recordKnifeBirth);
        });
    }

    private bool TryResolveOwnedReplayWeapon(
        int slot,
        uint weaponEntityHandle,
        out CCSPlayerController player,
        out CBasePlayerWeapon weapon)
    {
        player = null!;
        weapon = null!;
        if (!IsReplaySlotStillSafe(slot))
            return false;

        var candidatePlayer = Utilities.GetPlayerFromSlot(slot);
        var pawn = candidatePlayer?.PlayerPawn.Value;
        if (candidatePlayer is not { IsValid: true, PawnIsAlive: true } ||
            pawn is not { IsValid: true })
        {
            return false;
        }

        var candidateWeapon = new CHandle<CBasePlayerWeapon>(weaponEntityHandle).Value;
        if (candidateWeapon is not { IsValid: true } || !PawnOwnsWeapon(pawn, candidateWeapon))
            return false;

        player = candidatePlayer;
        weapon = candidateWeapon;
        return true;
    }

    private bool TryFindReplayPlayerByItemServices(
        CCSPlayer_ItemServices itemServices,
        out int slot,
        out CCSPlayerController player)
    {
        slot = -1;
        player = null!;
        if (itemServices == null || itemServices.Handle == IntPtr.Zero)
            return false;

        var candidates = _loadedSlots
            .Select(slot => Utilities.GetPlayerFromSlot(slot))
            .Where(candidate => candidate is { IsValid: true })
            .Cast<CCSPlayerController>()
            .GroupBy(candidate => candidate.Slot)
            .Select(group => group.First())
            .ToList();

        foreach (var candidate in candidates)
        {
            var candidateSlot = candidate.Slot;
            if (!IsReplaySlotStillSafe(candidateSlot))
                continue;

            var pawn = candidate?.PlayerPawn.Value;
            if (candidate is not { IsValid: true, PawnIsAlive: true } ||
                pawn is not { IsValid: true } ||
                pawn.ItemServices == null ||
                pawn.ItemServices.Handle != itemServices.Handle)
            {
                continue;
            }

            slot = candidateSlot;
            player = candidate;
            return true;
        }

        return false;
    }

    private bool TryApplyGivenWeaponCosmetic(
        int slot,
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        bool countResult,
        bool recordKnifeBirth = false)
    {
        if (!IsReplaySlotStillSafe(slot))
        {
            return false;
        }

        var weaponDefIndex = WeaponDefIndex(weapon);
        if (IsKnifeCosmeticDefIndex(weaponDefIndex))
        {
            if (!_loadedReplays.TryGetValue(slot, out var replay) ||
                !HasActiveBotRandomizerClaim(
                    slot,
                    replay.SteamId,
                    DemoTracerCosmeticWriteField.Knife))
            {
                return false;
            }

            var desiredKnife = replay.Cosmetics.Knife ?? BuildDefaultKnifeCosmetic(player);
            var knifeOk = replay.Cosmetics.Knife != null
                ? TryApplyItemCosmetic(
                    player,
                    weapon,
                    desiredKnife,
                    replay.SteamId,
                    DemoTracerCosmeticWriteField.Knife,
                    allowSubclassChange: true,
                    applyPaint: true,
                    applyCustomName: _cosmeticNamesEnabled)
                : TryClearKnifeCosmetic(player, weapon, replay.SteamId);
            if (knifeOk && recordKnifeBirth)
                RememberKnifeCosmeticBirth(slot, weapon, desiredKnife);
            if (replay.Cosmetics.Knife != null)
            {
                ScheduleCachedKnifeCosmeticRetry(
                    slot,
                    desiredKnife,
                    replay.SteamId,
                    framesRemaining: knifeOk ? 3 : 8);
            }
            if (countResult)
            {
                if (knifeOk)
                    _cosmeticAppliedCount++;
                else
                    _cosmeticSkippedCount++;
            }
            return knifeOk;
        }

        var normalizedWeaponDefIndex = NormalizeWeaponDefIndex(weaponDefIndex);
        if (!IsWeaponCosmeticDefIndex(normalizedWeaponDefIndex))
            return false;
        if (!IsReplaySlotPlaying(slot))
            return false;

        if (!TryGetWeaponCosmeticForSlot(slot, normalizedWeaponDefIndex, out var cosmetic, out var replaySteamId))
        {
            return false;
        }

        var ok = TryApplyWeaponCosmetic(player, weapon, cosmetic, replaySteamId);
        if (countResult)
        {
            if (ok)
                _cosmeticAppliedCount++;
            else
                _cosmeticSkippedCount++;
        }
        return ok;
    }

    private void TryApplySpawnedReplayWeaponCosmetic(CEntityInstance entity)
    {
        if (_loadedReplays.Count == 0)
            return;
        var name = entity.DesignerName;
        if (string.IsNullOrWhiteSpace(name) ||
            !name.Contains("weapon", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var weaponEntityHandle = entity.EntityHandle.Raw;
        if (weaponEntityHandle == Utilities.InvalidEHandleIndex)
            return;

        ScheduleCosmeticNextFrame(() =>
        {
            if (_loadedReplays.Count == 0)
                return;

            var weapon = new CHandle<CBasePlayerWeapon>(weaponEntityHandle).Value;
            if (weapon is not { IsValid: true })
                return;

            var weaponDefIndex = WeaponDefIndex(weapon);
            var normalizedWeaponDefIndex = NormalizeWeaponDefIndex(weaponDefIndex);
            var isReplayWeaponCosmetic = IsWeaponCosmeticDefIndex(normalizedWeaponDefIndex);
            var isReplayKnifeCosmetic = IsKnifeCosmeticDefIndex(weaponDefIndex);
            if (!isReplayWeaponCosmetic && !isReplayKnifeCosmetic)
                return;

            var candidates = _loadedSlots
                .Select(slot => Utilities.GetPlayerFromSlot(slot))
                .Where(candidate => candidate is { IsValid: true })
                .Cast<CCSPlayerController>()
                .GroupBy(candidate => candidate.Slot)
                .Select(group => group.First())
                .ToList();

            foreach (var player in candidates)
            {
                var slot = player.Slot;
                if (!IsReplaySlotStillSafe(slot))
                {
                    continue;
                }

                var pawn = player?.PlayerPawn.Value;
                if (player is not { IsValid: true, PawnIsAlive: true } ||
                    pawn is not { IsValid: true } ||
                    !PawnOwnsWeapon(pawn, weapon))
                {
                    continue;
                }

                var attempted = false;
                var applied = false;
                ReplayItemCosmetic? knifeCosmetic = null;
                ReplayWeaponCosmetic? weaponCosmetic = null;
                ulong replaySteamId = 0;
                if (isReplayKnifeCosmetic)
                {
                    if (_loadedReplays.TryGetValue(slot, out var replay) &&
                        HasActiveBotRandomizerClaim(
                            slot,
                            replay.SteamId,
                            DemoTracerCosmeticWriteField.Knife))
                    {
                        replaySteamId = replay.SteamId;
                        knifeCosmetic = replay.Cosmetics.Knife ?? BuildDefaultKnifeCosmetic(player);
                        attempted = true;
                        applied = replay.Cosmetics.Knife != null
                            ? TryApplyItemCosmetic(
                                player,
                                weapon,
                                knifeCosmetic,
                                replaySteamId,
                                DemoTracerCosmeticWriteField.Knife,
                                allowSubclassChange: true,
                                applyPaint: true,
                                applyCustomName: _cosmeticNamesEnabled)
                            : TryClearKnifeCosmetic(player, weapon, replaySteamId);
                        if (applied)
                            RememberKnifeCosmeticBirth(slot, weapon, knifeCosmetic);
                    }
                }
                else if (IsReplaySlotPlaying(slot) &&
                         TryGetWeaponCosmeticForSlot(slot, normalizedWeaponDefIndex, out weaponCosmetic, out replaySteamId))
                {
                    attempted = true;
                    applied = TryApplyWeaponCosmetic(player, weapon, weaponCosmetic, replaySteamId);
                }

                if (!attempted)
                    continue;

                if (applied)
                    _cosmeticAppliedCount++;
                else
                {
                    _cosmeticSkippedCount++;
                }
                if (isReplayKnifeCosmetic &&
                    knifeCosmetic != null &&
                    _loadedReplays.TryGetValue(slot, out var currentReplay) &&
                    currentReplay.Cosmetics.Knife != null)
                {
                    ScheduleCachedKnifeCosmeticRetry(slot, knifeCosmetic, replaySteamId, framesRemaining: applied ? 3 : 8);
                }

                ScheduleCosmeticNextFrame(() =>
                {
                    if (!TryResolveOwnedReplayWeapon(
                            slot,
                            weaponEntityHandle,
                            out var retryPlayer,
                            out var retryWeapon))
                    {
                        return;
                    }

                    if (isReplayKnifeCosmetic)
                    {
                        if (_loadedReplays.TryGetValue(slot, out var replay) &&
                            HasActiveBotRandomizerClaim(
                                slot,
                                replay.SteamId,
                                DemoTracerCosmeticWriteField.Knife))
                        {
                            var desiredKnife = replay.Cosmetics.Knife ?? BuildDefaultKnifeCosmetic(retryPlayer);
                            var retryApplied = replay.Cosmetics.Knife != null
                                ? TryApplyItemCosmetic(
                                    retryPlayer,
                                    retryWeapon,
                                    desiredKnife,
                                    replay.SteamId,
                                    DemoTracerCosmeticWriteField.Knife,
                                    allowSubclassChange: true,
                                    applyPaint: true,
                                    applyCustomName: _cosmeticNamesEnabled)
                                : TryClearKnifeCosmetic(retryPlayer, retryWeapon, replay.SteamId);
                            if (retryApplied)
                                RememberKnifeCosmeticBirth(slot, retryWeapon, desiredKnife);
                        }
                    }
                    else if (GivenItemCosmeticFeatureEnabled() &&
                             _weaponAlignEnabled &&
                             IsReplaySlotPlaying(slot) &&
                             TryGetWeaponCosmeticForSlot(slot, normalizedWeaponDefIndex, out var retryCosmetic, out var retrySteamId))
                    {
                        _ = TryApplyWeaponCosmetic(retryPlayer, retryWeapon, retryCosmetic, retrySteamId);
                    }
                });
                return;
            }
        });
    }

    private static bool TryFindReplayWeaponCosmetic(
        LoadedReplay replay,
        int weaponDefIndex,
        out ReplayWeaponCosmetic cosmetic)
    {
        cosmetic = replay.Cosmetics.Weapons
            .FirstOrDefault(candidate => candidate.WeaponDefIndex == weaponDefIndex)!;
        return cosmetic != null;
    }

    private static bool PawnOwnsWeapon(CCSPlayerPawn pawn, CBasePlayerWeapon weapon)
    {
        if (pawn.WeaponServices == null)
            return false;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var candidate = handle.Value;
            if (candidate == null || !candidate.IsValid)
                continue;
            if (candidate.Handle == weapon.Handle)
                return true;
        }

        return false;
    }

    private static bool TryFindReplayWeaponByDefIndex(
        CCSPlayerPawn pawn,
        int weaponDefIndex,
        out CBasePlayerWeapon weapon)
    {
        weapon = null!;
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className) ||
            pawn.WeaponServices == null)
        {
            return false;
        }

        if (TryFindActiveReplayWeaponByDefIndex(pawn, weaponDefIndex, out weapon))
            return true;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var candidate = handle.Value;
            if (candidate == null || !candidate.IsValid)
                continue;
            if (WeaponClassMatches(candidate.DesignerName, className) ||
                WeaponDefIndex(candidate) == weaponDefIndex)
            {
                weapon = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindActiveReplayWeaponByDefIndex(
        CCSPlayerPawn pawn,
        int weaponDefIndex,
        out CBasePlayerWeapon weapon)
    {
        weapon = null!;
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className) ||
            pawn.WeaponServices == null)
        {
            return false;
        }

        var activeWeapon = pawn.WeaponServices.ActiveWeapon.Value;
        if (activeWeapon == null || !activeWeapon.IsValid)
            return false;

        if (WeaponClassMatches(activeWeapon.DesignerName, className) ||
            WeaponDefIndex(activeWeapon) == NormalizeWeaponDefIndex(weaponDefIndex))
        {
            weapon = activeWeapon;
            return true;
        }

        return false;
    }

    private static int WeaponDefIndex(CBasePlayerWeapon weapon)
    {
        var designerDef = WeaponDefIndex(weapon.DesignerName);
        try
        {
            var rawItemDef = weapon.AttributeManager.Item.ItemDefinitionIndex;
            if (IsExactKnifeCosmeticDefIndex(rawItemDef))
                return rawItemDef;
            if (IsExactKnifeCosmeticDefIndex(designerDef))
                return designerDef;

            var itemDef = NormalizeWeaponDefIndex(rawItemDef);
            if (IsKnownWeaponDefIndex(itemDef))
                return itemDef;
        }
        catch
        {
        }

        return designerDef;
    }

    private static bool TryFindReplayKnife(CCSPlayerPawn pawn, out CBasePlayerWeapon weapon)
    {
        weapon = null!;
        if (pawn.WeaponServices == null)
            return false;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var candidate = handle.Value;
            if (candidate == null || !candidate.IsValid)
                continue;
            var name = candidate.DesignerName;
            if (name.Contains("knife", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("bayonet", StringComparison.OrdinalIgnoreCase))
            {
                weapon = candidate;
                return true;
            }
        }

        return false;
    }

    private bool TryApplyAgentCosmetic(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayAgentCosmetic cosmetic,
        ulong replaySteamId)
    {
        var slot = player.Slot;
        if (!IsReplaySlotStillSafe(slot) ||
            !TryValidateBotRandomizerClaim(
                slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.Agent) ||
            NormalizeAgentModelPath(cosmetic.ModelPath) is not { } modelPath)
        {
            return false;
        }

        try
        {
            if (player.UserId is not int userId)
                return false;
            var mutationGeneration = CurrentReplayMutationGeneration(slot);
            ApplyAgentModel(pawn, modelPath);
            ScheduleCosmeticNextFrame(() => ApplyAgentModelForSlot(
                slot,
                userId,
                mutationGeneration,
                replaySteamId,
                modelPath));
            AddTimer(
                0.20f,
                () => ApplyAgentModelForSlot(slot, userId, mutationGeneration, replaySteamId, modelPath),
                TimerFlags.STOP_ON_MAPCHANGE);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: agent model apply failed slot={player.Slot} model={modelPath}: {ex.Message}");
            return false;
        }
    }

    private bool TryRestoreNativeAgentModel(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ulong replaySteamId)
    {
        var slot = player.Slot;
        if (!IsReplaySlotStillSafe(slot) ||
            !TryValidateBotRandomizerClaim(
                slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.Agent))
        {
            return false;
        }

        if (player.UserId is int userId &&
            _nativeAgentModels.TryGetValue(slot, out var native) &&
            native.UserId == userId &&
            native.PawnEntityHandle == pawn.EntityHandle.Raw &&
            native.Team == player.Team)
        {
            var mutationGeneration = CurrentReplayMutationGeneration(slot);
            ApplyAgentModel(pawn, native.ModelPath);
            ScheduleCosmeticNextFrame(() => ApplyAgentModelForSlot(
                slot,
                userId,
                mutationGeneration,
                replaySteamId,
                native.ModelPath));
            AddTimer(
                0.20f,
                () => ApplyAgentModelForSlot(
                    slot,
                    userId,
                    mutationGeneration,
                    replaySteamId,
                    native.ModelPath),
                TimerFlags.STOP_ON_MAPCHANGE);
            return true;
        }

        return TryScheduleNativeAgentRespawn(player, pawn, replaySteamId);
    }

    private bool TryScheduleNativeAgentRespawn(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ulong replaySteamId)
    {
        var slot = player.Slot;
        if (player.UserId is not int userId ||
            IsReplaySlotPlaying(slot) ||
            !TryReadFreezePhaseRemaining(out var freezeRemaining, out _) ||
            freezeRemaining < KnifeEntityRefreshMinFreezeSeconds)
        {
            return false;
        }

        var replayGeneration = CurrentReplayIdentityGeneration(slot);
        var attempt = new NativeAgentRespawnAttempt(pawn.EntityHandle.Raw, replayGeneration);
        if (_nativeAgentRespawnAttempts.TryGetValue(slot, out var existing) &&
            existing.ReplayGeneration == replayGeneration)
        {
            return false;
        }
        _nativeAgentRespawnAttempts[slot] = attempt;

        ScheduleCosmeticNextFrame(() =>
        {
            if (!_nativeAgentRespawnAttempts.TryGetValue(slot, out var current) ||
                current != attempt ||
                !IsReplayIdentityGenerationCurrent(slot, replayGeneration) ||
                !_loadedReplays.TryGetValue(slot, out var replay) ||
                replay.SteamId != replaySteamId ||
                !TryValidateBotRandomizerClaim(
                    slot,
                    replaySteamId,
                    DemoTracerCosmeticWriteField.Agent) ||
                IsReplaySlotPlaying(slot) ||
                !TryReadFreezePhaseRemaining(out var remaining, out _) ||
                remaining < KnifeEntityRefreshMinFreezeSeconds)
            {
                return;
            }

            var currentPlayer = Utilities.GetPlayerFromSlot(slot);
            var currentPawn = currentPlayer?.PlayerPawn.Value;
            if (currentPlayer is not { IsValid: true, PawnIsAlive: true } ||
                currentPlayer.UserId != userId ||
                currentPawn is not { IsValid: true } ||
                currentPawn.EntityHandle.Raw != attempt.PawnEntityHandle)
            {
                return;
            }

            try
            {
                currentPlayer.Respawn();
                _loadoutSyncedSlots.Remove(slot);
                _rebuiltInventorySlots.Remove(slot);
                _cosmeticSyncedSlots.Remove(slot);
                Server.PrintToConsole(
                    $"dtr: respawned replay bot during freeze to recover native agent model slot={slot}");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole(
                    $"dtr: native agent model recovery respawn failed slot={slot}: {ex.Message}");
            }
        });
        return true;
    }

    private void ApplyAgentModelForSlot(
        int slot,
        int userId,
        long mutationGeneration,
        ulong replaySteamId,
        string modelPath)
    {
        if (!IsReplaySlotStillSafe(slot) ||
            !IsReplayMutationGenerationCurrent(slot, mutationGeneration) ||
            !TryValidateBotRandomizerClaim(
                slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.Agent))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.UserId != userId ||
            pawn is not { IsValid: true })
            return;

        try
        {
            ApplyAgentModel(pawn, modelPath);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: agent model delayed apply failed slot={slot} model={modelPath}: {ex.Message}");
        }
    }

    private static void ApplyAgentModel(CCSPlayerPawn pawn, string modelPath)
    {
        Server.PrecacheModel(modelPath);
        pawn.SetModel(modelPath);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
        var color = pawn.Render;
        pawn.Render = System.Drawing.Color.FromArgb(255, color.R, color.G, color.B);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
    }

    private bool TryApplyWeaponCosmetic(
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        ReplayWeaponCosmetic cosmetic,
        ulong replaySteamId,
        bool countStickerStats = true)
    {
        if (!TryGetWeaponClassByDefIndex(cosmetic.WeaponDefIndex, out _))
            return false;

        var appliedItem = false;
        var paintClaimed = HasActiveBotRandomizerClaim(
            player.Slot,
            replaySteamId,
            DemoTracerCosmeticWriteField.WeaponPaint,
            cosmetic.WeaponDefIndex);
        var stickersClaimed = HasActiveBotRandomizerClaim(
            player.Slot,
            replaySteamId,
            DemoTracerCosmeticWriteField.WeaponStickers,
            cosmetic.WeaponDefIndex);
        var keychainClaimed = HasActiveBotRandomizerClaim(
            player.Slot,
            replaySteamId,
            DemoTracerCosmeticWriteField.WeaponKeychain,
            cosmetic.WeaponDefIndex);
        if (!paintClaimed && !stickersClaimed && !keychainClaimed)
            return false;

        if (paintClaimed)
        {
            appliedItem = TryApplyItemCosmetic(
                player,
                weapon,
                new ReplayItemCosmetic
                {
                    ItemDefIndex = cosmetic.WeaponDefIndex,
                    PaintKit = cosmetic.PaintKit,
                    Seed = cosmetic.Seed,
                    Wear = cosmetic.Wear,
                    OriginalOwnerSteamId = cosmetic.OriginalOwnerSteamId,
                    ItemAccountId = cosmetic.ItemAccountId,
                    ItemId = cosmetic.ItemId,
                    CustomName = cosmetic.CustomName
                },
                replaySteamId,
                DemoTracerCosmeticWriteField.WeaponPaint,
                allowSubclassChange: false,
                applyPaint: true,
                applyCustomName: _cosmeticNamesEnabled);
            if (!appliedItem)
            {
                if (countStickerStats)
                {
                    RecordStickerSkipped(cosmetic.Stickers.Count);
                    RecordCharmSkipped(cosmetic.Charms.Count);
                }
                return false;
            }
        }

        if (paintClaimed)
            ApplyWeaponStattrakEvidence(player.Slot, replaySteamId, weapon, cosmetic);
        if (stickersClaimed)
            ApplyWeaponStickers(player.Slot, replaySteamId, weapon, cosmetic, countStickerStats);
        if (keychainClaimed)
            ApplyWeaponCharms(player.Slot, replaySteamId, weapon, cosmetic, countStickerStats);
        return appliedItem || stickersClaimed || keychainClaimed;
    }

    private bool TryApplyItemCosmetic(
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        ReplayItemCosmetic cosmetic,
        ulong replaySteamId,
        DemoTracerCosmeticWriteField writeField,
        bool allowSubclassChange,
        bool applyPaint,
        bool applyCustomName)
    {
        try
        {
            var weaponDefinitionIndex = writeField == DemoTracerCosmeticWriteField.WeaponPaint
                ? NormalizeWeaponDefIndex(cosmetic.ItemDefIndex ?? WeaponDefIndex(weapon))
                : 0;
            if (!TryValidateBotRandomizerClaim(
                    player.Slot,
                    replaySteamId,
                    writeField,
                    weaponDefinitionIndex))
            {
                return false;
            }

            var item = weapon.AttributeManager.Item;
            if (cosmetic.ItemDefIndex is { } itemDef)
            {
                if (allowSubclassChange && IsKnifeCosmeticDefIndex(itemDef))
                    weapon.AcceptInput("ChangeSubclass", value: itemDef.ToString(CultureInfo.InvariantCulture));
                item.ItemDefinitionIndex = (ushort)itemDef;
            }

            item.EntityQuality = allowSubclassChange ? 3 : 0;
            ApplyReplayEconIdentity(player, weapon, item, cosmetic, replaySteamId);
            if (applyPaint)
            {
                var networkedDynamicAttributes = item.NetworkedDynamicAttributes;
                var attributeList = item.AttributeList;
                if (networkedDynamicAttributes.Handle == IntPtr.Zero ||
                    attributeList.Handle == IntPtr.Zero)
                {
                    return false;
                }

                if (ShouldClearCompleteAttributeLists(writeField))
                {
                    attributeList.Attributes.RemoveAll();
                    networkedDynamicAttributes.Attributes.RemoveAll();
                }
                weapon.FallbackPaintKit = (int)Math.Min(cosmetic.PaintKit, int.MaxValue);
                weapon.FallbackSeed = (int)Math.Min(cosmetic.Seed, int.MaxValue);
                weapon.FallbackWear = cosmetic.Wear;
                MarkWeaponPaintStateChanged(weapon);
                _ = TrySetTextureAttributes(networkedDynamicAttributes.Handle, cosmetic);
                _ = TrySetTextureAttributes(attributeList.Handle, cosmetic);
                var bodygroup = IsLegacyCosmeticPaint(
                    item.ItemDefinitionIndex,
                    (int)Math.Min(cosmetic.PaintKit, int.MaxValue)) ? 1 : 0;
                weapon.AcceptInput("SetBodygroup", value: $"body,{bodygroup}");
            }
            if (applyCustomName && !string.IsNullOrWhiteSpace(cosmetic.CustomName))
                item.CustomName = cosmetic.CustomName;
            Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: cosmetic apply failed slot={player.Slot} item={weapon.DesignerName}: {ex.Message}");
            return false;
        }
    }

    private bool IsLegacyCosmeticPaint(int weaponDefIndex, int paintKit)
        => paintKit > 0 &&
           _legacyCosmeticPaints.Contains((NormalizeWeaponDefIndex(weaponDefIndex), (uint)paintKit));

    private static bool IsExactKnifeCosmeticDefIndex(int weaponDefIndex)
        => IsKnifeCosmeticDefIndex(weaponDefIndex) && weaponDefIndex is not (41 or 42 or 59);

    private void ApplyWeaponStattrakEvidence(
        int slot,
        ulong replaySteamId,
        CBasePlayerWeapon weapon,
        ReplayWeaponCosmetic cosmetic)
    {
        if (cosmetic.Quality != 9 && cosmetic.StattrakCounter == null)
            return;
        if (!TryValidateBotRandomizerClaim(
                slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.WeaponPaint,
                cosmetic.WeaponDefIndex))
        {
            return;
        }

        var item = weapon.AttributeManager.Item;
        item.EntityQuality = 9;
        weapon.FallbackStatTrak = cosmetic.StattrakCounter ?? 0;
        _ = TrySetStattrakAttributes(item.NetworkedDynamicAttributes.Handle, weapon.FallbackStatTrak);
        _ = TrySetStattrakAttributes(item.AttributeList.Handle, weapon.FallbackStatTrak);
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackStatTrak");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
    }

    private static ReplayItemCosmetic BuildDefaultKnifeCosmetic(CCSPlayerController player)
        => new()
        {
            ItemDefIndex = DefaultKnifeDefIndexForTeam(player.Team),
            PaintKit = 0,
            Seed = 0,
            Wear = 0.0f
        };

    internal static int DefaultKnifeDefIndexForTeam(CsTeam team)
        => team == CsTeam.Terrorist ? 59 : 42;

    private bool TryClearKnifeCosmetic(
        CCSPlayerController player,
        CBasePlayerWeapon knife,
        ulong replaySteamId)
    {
        var slot = player.Slot;
        try
        {
            if (player.UserId is not int userId ||
                !TryValidateBotRandomizerClaim(
                    slot,
                    replaySteamId,
                    DemoTracerCosmeticWriteField.Knife))
            {
                return false;
            }

            var mutationGeneration = CurrentReplayMutationGeneration(slot);
            ClearKnifeEconItem(player, knife);
            AddTimer(
                0.10f,
                () => ClearKnifeCosmeticForSlot(
                    slot,
                    userId,
                    mutationGeneration,
                    replaySteamId),
                TimerFlags.STOP_ON_MAPCHANGE);
            AddTimer(
                0.25f,
                () => ClearKnifeCosmeticForSlot(
                    slot,
                    userId,
                    mutationGeneration,
                    replaySteamId),
                TimerFlags.STOP_ON_MAPCHANGE);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: knife cosmetic clear failed slot={player.Slot}: {ex.Message}");
            return false;
        }
    }

    private void ClearKnifeCosmeticForSlot(
        int slot,
        int userId,
        long mutationGeneration,
        ulong replaySteamId)
    {
        try
        {
            if (!IsReplaySlotStillSafe(slot) ||
                !IsReplayMutationGenerationCurrent(slot, mutationGeneration) ||
                !TryValidateBotRandomizerClaim(
                    slot,
                    replaySteamId,
                    DemoTracerCosmeticWriteField.Knife))
            {
                return;
            }

            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player is not { IsValid: true, PawnIsAlive: true } ||
                player.UserId != userId ||
                pawn is not { IsValid: true })
            {
                return;
            }

            if (TryFindReplayKnife(pawn, out var knife))
                ClearKnifeEconItem(player, knife);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: knife cosmetic delayed clear failed slot={slot}: {ex.Message}");
        }
    }

    private static void ClearKnifeEconItem(
        CCSPlayerController player,
        CBasePlayerWeapon knife)
    {
        var item = knife.AttributeManager.Item;
        var defaultDef = DefaultKnifeDefIndexForTeam(player.Team);
        knife.AcceptInput("ChangeSubclass", value: defaultDef.ToString(CultureInfo.InvariantCulture));
        item.ItemDefinitionIndex = (ushort)defaultDef;
        item.EntityQuality = 0;
        item.AccountID = AccountIdForReplayPlayer(player, replaySteamId: null);
        SetReplayEconItemId(item, 0);
        item.CustomName = string.Empty;
        item.AttributeList.Attributes.RemoveAll();
        item.NetworkedDynamicAttributes.Attributes.RemoveAll();
        knife.FallbackPaintKit = 0;
        knife.FallbackSeed = 0;
        knife.FallbackWear = 0.0f;
        knife.FallbackStatTrak = 0;
        TrySetOriginalOwnerXuid(knife, null);
        MarkWeaponPaintStateChanged(knife);
        Utilities.SetStateChanged(knife, "CEconEntity", "m_nFallbackStatTrak");
        Utilities.SetStateChanged(knife, "CEconEntity", "m_AttributeManager");
        knife.AcceptInput("SetBodygroup", value: "body,0");
    }

    private bool TryClearGloveCosmetic(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ulong replaySteamId,
        out bool changed)
    {
        changed = false;
        try
        {
            var slot = player.Slot;
            if (!TryValidateBotRandomizerClaim(
                    slot,
                    replaySteamId,
                    DemoTracerCosmeticWriteField.Gloves))
            {
                return false;
            }

            var token = ++_nextGloveCosmeticToken;
            _gloveCosmeticTokens[slot] = token;
            if (IsGloveEconItemCleared(pawn))
            {
                _appliedGloveCosmetics.Remove(slot);
                return true;
            }

            ClearGloveEconItem(pawn);
            _appliedGloveCosmetics.Remove(slot);
            changed = true;
            AddTimer(
                0.10f,
                () => ClearGloveCosmeticForSlot(slot, replaySteamId, token),
                TimerFlags.STOP_ON_MAPCHANGE);
            AddTimer(
                0.25f,
                () => ClearGloveCosmeticForSlot(slot, replaySteamId, token),
                TimerFlags.STOP_ON_MAPCHANGE);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: glove cosmetic clear failed slot={player.Slot}: {ex.Message}");
            return false;
        }
    }

    private void ClearGloveCosmeticForSlot(int slot, ulong replaySteamId, int token)
    {
        try
        {
            if (!_gloveCosmeticTokens.TryGetValue(slot, out var activeToken) ||
                activeToken != token ||
                !IsReplaySlotStillSafe(slot) ||
                !TryValidateBotRandomizerClaim(
                    slot,
                    replaySteamId,
                    DemoTracerCosmeticWriteField.Gloves))
            {
                return;
            }

            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
                return;

            _ = TryClearGloveCosmetic(player, pawn, replaySteamId, out _);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: glove cosmetic delayed clear failed slot={slot}: {ex.Message}");
        }
    }

    private bool TryApplyGloveCosmetic(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayItemCosmetic cosmetic,
        ulong replaySteamId,
        out bool changed)
    {
        changed = false;
        try
        {
            if (AttributeSetter.Value == null)
                return false;

            var slot = player.Slot;
            if (!TryValidateBotRandomizerClaim(
                    slot,
                    replaySteamId,
                    DemoTracerCosmeticWriteField.Gloves))
            {
                return false;
            }
            var pawnHandle = pawn.Handle;
            var fingerprint = GloveCosmeticFingerprint.From(cosmetic);
            // Glove material creation streams on the client. Rewriting identical econ state disposes
            // and recreates those materials while their texture requests are still outstanding.
            if (IsAppliedGloveCosmeticCurrent(slot, pawn, fingerprint))
                return true;

            var token = ++_nextGloveCosmeticToken;
            _gloveCosmeticTokens[slot] = token;
            if (!ApplyGloveEconItem(player, pawn, cosmetic, replaySteamId))
                return false;
            var item = pawn.EconGloves;
            _appliedGloveCosmetics[slot] = new AppliedGloveCosmetic(
                pawnHandle,
                fingerprint,
                replaySteamId,
                item.ItemID,
                item.ItemDefinitionIndex,
                item.AccountID);
            changed = true;
            AddTimer(0.10f, () => ApplyGloveCosmeticForSlot(slot, cosmetic, replaySteamId, token), TimerFlags.STOP_ON_MAPCHANGE);
            AddTimer(0.20f, () => FinishGloveCosmeticBodygroup(slot, pawnHandle, token), TimerFlags.STOP_ON_MAPCHANGE);
            AddTimer(0.25f, () => ApplyGloveCosmeticForSlot(slot, cosmetic, replaySteamId, token), TimerFlags.STOP_ON_MAPCHANGE);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: glove cosmetic apply failed slot={player.Slot}: {ex.Message}");
            return false;
        }
    }

    private bool IsAppliedGloveCosmeticCurrent(
        int slot,
        CCSPlayerPawn pawn,
        GloveCosmeticFingerprint fingerprint)
    {
        if (!_appliedGloveCosmetics.TryGetValue(slot, out var applied) ||
            applied.PawnHandle != pawn.Handle ||
            applied.Fingerprint != fingerprint)
        {
            return false;
        }

        var item = pawn.EconGloves;
        return item.Initialized &&
               item.ItemID == applied.ItemId &&
               item.ItemDefinitionIndex == applied.ItemDefinitionIndex &&
               item.AccountID == applied.AccountId;
    }

    private void ApplyGloveCosmeticForSlot(
        int slot,
        ReplayItemCosmetic cosmetic,
        ulong replaySteamId,
        int token)
    {
        try
        {
            if (!_gloveCosmeticTokens.TryGetValue(slot, out var activeToken) ||
                activeToken != token ||
                !IsReplaySlotStillSafe(slot) ||
                !TryValidateBotRandomizerClaim(
                    slot,
                    replaySteamId,
                    DemoTracerCosmeticWriteField.Gloves))
            {
                return;
            }

            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
                return;

            _ = TryApplyGloveCosmetic(player, pawn, cosmetic, replaySteamId, out _);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: glove cosmetic delayed apply failed slot={slot}: {ex.Message}");
        }
    }

    private void FinishGloveCosmeticBodygroup(int slot, nint pawnHandle, int token)
    {
        if (!_gloveCosmeticTokens.TryGetValue(slot, out var activeToken) ||
            activeToken != token ||
            !_appliedGloveCosmetics.TryGetValue(slot, out var applied) ||
            applied.PawnHandle != pawnHandle ||
            !TryValidateBotRandomizerClaim(
                slot,
                applied.ReplaySteamId,
                DemoTracerCosmeticWriteField.Gloves))
        {
            return;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            pawn is not { IsValid: true } ||
            pawn.Handle != pawnHandle ||
            !IsAppliedGloveCosmeticCurrent(slot, pawn, applied.Fingerprint))
        {
            return;
        }

        pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
    }

    private bool ApplyGloveEconItem(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayItemCosmetic cosmetic,
        ulong replaySteamId)
    {
        if (!TryValidateBotRandomizerClaim(
                player.Slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.Gloves))
        {
            return false;
        }
        var item = pawn.EconGloves;
        if (cosmetic.ItemDefIndex is { } itemDef)
            item.ItemDefinitionIndex = (ushort)itemDef;
        item.AccountID = (uint)player.SteamID;
        item.Initialized = true;
        UpdateReplayEconItemId(item);

        item.NetworkedDynamicAttributes.Attributes.RemoveAll();
        item.AttributeList.Attributes.RemoveAll();
        if (!TrySetTextureAttributes(item.NetworkedDynamicAttributes.Handle, cosmetic) ||
            !TrySetTextureAttributes(item.AttributeList.Handle, cosmetic))
        {
            return false;
        }

        MarkGloveCosmeticStateChanged(pawn);
        pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
        return true;
    }

    private static bool IsGloveEconItemCleared(CCSPlayerPawn pawn)
    {
        var item = pawn.EconGloves;
        return item.ItemDefinitionIndex == 0 &&
               item.AccountID == 0 &&
               !item.Initialized &&
               item.ItemID == 0;
    }

    private static void ClearGloveEconItem(CCSPlayerPawn pawn)
    {
        var item = pawn.EconGloves;
        item.ItemDefinitionIndex = 0;
        item.AccountID = 0;
        item.Initialized = false;
        item.EntityQuality = 0;
        item.CustomName = string.Empty;
        SetReplayEconItemId(item, 0);
        item.NetworkedDynamicAttributes.Attributes.RemoveAll();
        item.AttributeList.Attributes.RemoveAll();
        MarkGloveCosmeticStateChanged(pawn);
        pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
    }

    private const ulong SteamId64AccountBase = 76_561_197_960_265_728;
    private static ulong _nextReplayEconItemId = 10_000_000_000;

    private static void MarkWeaponPaintStateChanged(CBasePlayerWeapon weapon)
    {
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackPaintKit");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackSeed");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_flFallbackWear");
    }

    private static void MarkGloveCosmeticStateChanged(CCSPlayerPawn pawn)
    {
        try
        {
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_EconGloves");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: glove cosmetic state change failed: {ex.Message}");
        }
    }

    private static void UpdateReplayEconItemId(CEconItemView item)
    {
        var itemId = _nextReplayEconItemId++;
        SetReplayEconItemId(item, itemId);
    }

    private static void SetReplayEconItemId(CEconItemView item, ulong itemId)
    {
        item.ItemID = itemId;
        item.ItemIDLow = (uint)(itemId & 0xFFFFFFFF);
        item.ItemIDHigh = (uint)(itemId >> 32);
    }

    private void ApplyReplayEconIdentity(
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        CEconItemView item,
        ReplayItemCosmetic cosmetic,
        ulong replaySteamId)
    {
        var ownerSteamId = NormalizeOptionalULong(cosmetic.OriginalOwnerSteamId);
        var replayPlayerSteamId = NormalizeOptionalULong(replaySteamId);
        var sourceOwnerSteamId = ownerSteamId ?? replayPlayerSteamId;
        var effectiveOwnerSteamId = sourceOwnerSteamId;
        var playerAccountId = AccountIdForReplayPlayer(player, replayPlayerSteamId);
        item.AccountID = AccountIdFromSteamId(effectiveOwnerSteamId)
                         ?? cosmetic.ItemAccountId
                         ?? playerAccountId;
        if (cosmetic.ItemId is { } itemId && itemId != 0)
            SetReplayEconItemId(item, itemId);
        else
            UpdateReplayEconItemId(item);
        TrySetOriginalOwnerXuid(weapon, effectiveOwnerSteamId);
    }

    private static uint AccountIdForReplayPlayer(CCSPlayerController player, ulong? replaySteamId)
        => AccountIdFromSteamId(replaySteamId)
           ?? AccountIdFromSteamId(NormalizeOptionalULong(player.SteamID))
           ?? (uint)player.SteamID;

    private static uint? AccountIdFromSteamId(ulong? steamId)
    {
        if (steamId is not { } value || value == 0)
            return null;
        if (value >= SteamId64AccountBase)
        {
            var accountId = value - SteamId64AccountBase;
            return accountId <= uint.MaxValue ? (uint)accountId : null;
        }
        return value <= uint.MaxValue ? (uint)value : null;
    }

    private static void TrySetOriginalOwnerXuid(CBasePlayerWeapon weapon, ulong? ownerSteamId)
    {
        var value = ownerSteamId.GetValueOrDefault();
        var low = (uint)(value & 0xFFFFFFFF);
        var high = (uint)(value >> 32);
        var setLow = TrySetOriginalOwnerSchemaValue(weapon, "m_OriginalOwnerXuidLow", low) ||
                     TrySetIntegralMember(weapon, "OriginalOwnerXuidLow", low);
        var setHigh = TrySetOriginalOwnerSchemaValue(weapon, "m_OriginalOwnerXuidHigh", high) ||
                      TrySetIntegralMember(weapon, "OriginalOwnerXuidHigh", high);
        if (!setLow && !setHigh)
            return;

        TrySetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidLow");
        TrySetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidHigh");
    }

    private static bool TrySetOriginalOwnerSchemaValue(
        CBasePlayerWeapon weapon,
        string fieldName,
        uint value)
    {
        if (weapon.Handle == IntPtr.Zero)
            return false;
        try
        {
            Schema.SetSchemaValue(weapon.Handle, "CEconEntity", fieldName, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetIntegralMember(object target, string name, ulong value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var property = target.GetType().GetProperty(name, flags);
            if (property?.CanWrite == true &&
                TryConvertIntegralValue(value, property.PropertyType, out var propertyValue))
            {
                property.SetValue(target, propertyValue);
                return true;
            }

            var field = target.GetType().GetField(name, flags);
            if (field != null && TryConvertIntegralValue(value, field.FieldType, out var fieldValue))
            {
                field.SetValue(target, fieldValue);
                return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static bool TryConvertIntegralValue(
        ulong value,
        Type targetType,
        out object converted)
    {
        converted = 0U;
        if (targetType == typeof(uint))
        {
            if (value > uint.MaxValue)
                return false;
            converted = (uint)value;
            return true;
        }
        if (targetType == typeof(ulong))
        {
            converted = value;
            return true;
        }
        if (targetType == typeof(int))
        {
            if (value > int.MaxValue)
                return false;
            converted = (int)value;
            return true;
        }
        if (targetType == typeof(long))
        {
            if (value > long.MaxValue)
                return false;
            converted = (long)value;
            return true;
        }
        return false;
    }

    private static void TrySetStateChanged(CBasePlayerWeapon weapon, string className, string fieldName)
    {
        try
        {
            Utilities.SetStateChanged(weapon, className, fieldName);
        }
        catch
        {
            // Some server/API versions expose the field but reject state-change marking.
        }
    }

    private static MemoryFunctionVoid<nint, string, float>? CreateAttributeSetter()
    {
        try
        {
            var signature = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? AttributeSetterWindowsSignature
                : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? AttributeSetterLinuxSignature
                    : string.Empty;
            return string.IsNullOrWhiteSpace(signature)
                ? null
                : new MemoryFunctionVoid<nint, string, float>(signature);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: cosmetic attribute setter unavailable: {ex.Message}");
            return null;
        }
    }

    private static bool TrySetTextureAttributes(nint attributeListHandle, ReplayItemCosmetic cosmetic)
    {
        if (attributeListHandle == IntPtr.Zero || AttributeSetter.Value == null)
            return false;

        try
        {
            AttributeSetter.Value.Invoke(attributeListHandle, "set item texture prefab", cosmetic.PaintKit);
            AttributeSetter.Value.Invoke(attributeListHandle, "set item texture seed", cosmetic.Seed);
            AttributeSetter.Value.Invoke(attributeListHandle, "set item texture wear", cosmetic.Wear);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: cosmetic attribute write failed: {ex.Message}");
            return false;
        }
    }

    private static bool TrySetStattrakAttributes(nint attributeListHandle, int counter)
    {
        if (AttributeSetter.Value == null)
            return false;

        try
        {
            AttributeSetter.Value.Invoke(attributeListHandle, "kill eater", BitConverter.Int32BitsToSingle(counter));
            AttributeSetter.Value.Invoke(attributeListHandle, "kill eater score type", 0.0f);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: StatTrak attribute write failed: {ex.Message}");
            return false;
        }
    }

    private void ApplyWeaponStickers(
        int slot,
        ulong replaySteamId,
        CBasePlayerWeapon weapon,
        ReplayWeaponCosmetic cosmetic,
        bool countStickerStats)
    {
        if (!_cosmeticAlignEnabled ||
            !_stickerAlignEnabled ||
            cosmetic.Stickers.Count == 0)
        {
            return;
        }
        if (!TryValidateBotRandomizerClaim(
                slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.WeaponStickers,
                cosmetic.WeaponDefIndex))
        {
            return;
        }

        if (AttributeSetter.Value == null)
        {
            if (countStickerStats)
                RecordStickerSkipped(cosmetic.Stickers.Count);
            return;
        }

        try
        {
            var item = weapon.AttributeManager.Item;
            var applied = 0;
            var skipped = 0;
            foreach (var sticker in cosmetic.Stickers)
            {
                var networkedOk = TrySetStickerAttributes(item.NetworkedDynamicAttributes.Handle, sticker);
                var listOk = TrySetStickerAttributes(item.AttributeList.Handle, sticker);
                if (networkedOk && listOk)
                    applied++;
                else
                    skipped++;
            }

            if (applied > 0)
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
            if (countStickerStats)
            {
                _stickerAppliedCount += applied;
                _stickerSkippedCount += skipped;
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: sticker apply failed item={weapon.DesignerName}: {ex.Message}");
            if (countStickerStats)
                RecordStickerSkipped(cosmetic.Stickers.Count);
        }
    }

    private void RecordStickerSkipped(int count)
    {
        if (_cosmeticAlignEnabled && _stickerAlignEnabled && count > 0)
            _stickerSkippedCount += count;
    }

    private void ApplyWeaponCharms(
        int slot,
        ulong replaySteamId,
        CBasePlayerWeapon weapon,
        ReplayWeaponCosmetic cosmetic,
        bool countCharmStats)
    {
        if (!_cosmeticAlignEnabled ||
            !_charmAlignEnabled ||
            cosmetic.Charms.Count == 0)
        {
            return;
        }
        if (!TryValidateBotRandomizerClaim(
                slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.WeaponKeychain,
                cosmetic.WeaponDefIndex))
        {
            return;
        }

        if (AttributeSetter.Value == null)
        {
            if (countCharmStats)
                RecordCharmSkipped(cosmetic.Charms.Count);
            return;
        }

        try
        {
            var item = weapon.AttributeManager.Item;
            var applied = 0;
            var skipped = 0;
            foreach (var charm in cosmetic.Charms)
            {
                var networkedOk = TrySetCharmAttributes(item.NetworkedDynamicAttributes.Handle, charm);
                var listOk = TrySetCharmAttributes(item.AttributeList.Handle, charm);
                if (networkedOk && listOk)
                    applied++;
                else
                    skipped++;
            }

            if (applied > 0)
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
            if (countCharmStats)
            {
                _charmAppliedCount += applied;
                _charmSkippedCount += skipped;
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: charm apply failed item={weapon.DesignerName}: {ex.Message}");
            if (countCharmStats)
                RecordCharmSkipped(cosmetic.Charms.Count);
        }
    }

    private void RecordCharmSkipped(int count)
    {
        if (_cosmeticAlignEnabled && _charmAlignEnabled && count > 0)
            _charmSkippedCount += count;
    }

    private static bool TrySetStickerAttributes(nint attributeListHandle, ReplayWeaponSticker sticker)
    {
        if (AttributeSetter.Value == null)
            return false;

        try
        {
            var slot = $"sticker slot {sticker.Slot}";
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} id", BitConverter.UInt32BitsToSingle(sticker.StickerId));
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} wear", sticker.Wear);
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset x", sticker.OffsetX);
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset y", sticker.OffsetY);
            if (sticker.Scale.HasValue)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} scale", sticker.Scale.Value);
            if (sticker.Rotation.HasValue)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} rotation", sticker.Rotation.Value);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: sticker attribute write failed slot={sticker.Slot}: {ex.Message}");
            return false;
        }
    }

    private static bool TrySetCharmAttributes(nint attributeListHandle, ReplayWeaponCharm charm)
    {
        if (AttributeSetter.Value == null)
            return false;

        try
        {
            var slot = $"keychain slot {charm.Slot}";
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} id", BitConverter.UInt32BitsToSingle(charm.CharmId));
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset x", charm.OffsetX);
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset y", charm.OffsetY);
            AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} offset z", charm.OffsetZ);
            if (charm.Seed is { } seed)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} seed", BitConverter.UInt32BitsToSingle(seed));
            if (charm.Highlight is { } highlight)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} highlight", BitConverter.UInt32BitsToSingle(highlight));
            if (charm.StickerId is { } stickerId)
                AttributeSetter.Value.Invoke(attributeListHandle, $"{slot} sticker", BitConverter.UInt32BitsToSingle(stickerId));
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: charm attribute write failed slot={charm.Slot}: {ex.Message}");
            return false;
        }
    }

    private sealed class PlayerCosmeticEvidence(ulong replaySteamId)
    {
        public ulong ReplaySteamId { get; private set; } = replaySteamId;
        public ReplayItemCosmetic? Knife { get; set; }
        public ReplayItemCosmetic? Glove { get; set; }
        public ReplayAgentCosmetic? Agent { get; set; }

        public bool HasPositiveEvidence => Knife != null || Glove != null || Agent != null;

        public bool IsEmpty => !HasPositiveEvidence;

        public void RememberReplaySteamId(ulong steamId)
        {
            if (steamId != 0)
                ReplaySteamId = steamId;
        }
    }

    private readonly record struct GloveCosmeticFingerprint(
        int ItemDefinitionIndex,
        uint PaintKit,
        uint Seed,
        int WearBits)
    {
        public static GloveCosmeticFingerprint From(ReplayItemCosmetic cosmetic)
            => new(
                cosmetic.ItemDefIndex ?? -1,
                cosmetic.PaintKit,
                cosmetic.Seed,
                BitConverter.SingleToInt32Bits(cosmetic.Wear));
    }

    private readonly record struct KnifeCosmeticFingerprint(
        int ItemDefinitionIndex,
        uint PaintKit,
        uint Seed,
        int WearBits)
    {
        public static KnifeCosmeticFingerprint From(ReplayItemCosmetic cosmetic)
            => new(
                cosmetic.ItemDefIndex ?? -1,
                cosmetic.PaintKit,
                cosmetic.Seed,
                BitConverter.SingleToInt32Bits(cosmetic.Wear));
    }

    private readonly record struct AppliedKnifeCosmeticBirth(
        uint WeaponEntityHandle,
        KnifeCosmeticFingerprint Fingerprint);

    private readonly record struct PendingKnifeEntityRefresh(
        int Token,
        KnifeCosmeticFingerprint Fingerprint,
        long ReplayGeneration,
        ReplayItemCosmetic DesiredCosmetic);

    private readonly record struct NativeAgentModelCapture(
        int UserId,
        uint PawnEntityHandle,
        CsTeam Team,
        string ModelPath);

    private readonly record struct NativeAgentRespawnAttempt(
        uint PawnEntityHandle,
        long ReplayGeneration);

    private readonly record struct AppliedGloveCosmetic(
        nint PawnHandle,
        GloveCosmeticFingerprint Fingerprint,
        ulong ReplaySteamId,
        ulong ItemId,
        ushort ItemDefinitionIndex,
        uint AccountId);
}
