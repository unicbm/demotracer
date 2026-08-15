/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using DemoTracerApi;
using DemoTracerBotHiderApi;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private enum ReplayStartAnchor
    {
        Live,
        FreezePreroll,
    }

    private enum ReplayIdentityMode
    {
        Off,
        Name,
        Steam,
        Avatar,
    }

    private sealed class TickPlayerSnapshot
    {
        private readonly CCSPlayerController?[] _bySlot = new CCSPlayerController?[MaxPlayerSlots];

        public TickPlayerSnapshot(
            IReadOnlyList<CCSPlayerController> controllers,
            IReadOnlyList<CCSPlayerController> teamPlayers)
        {
            Controllers = controllers;
            TeamPlayers = teamPlayers;

            foreach (var controller in controllers)
            {
                if (controller is not { IsValid: true } || controller.Slot is < 0 or >= MaxPlayerSlots)
                    continue;
                _bySlot[controller.Slot] ??= controller;
            }
        }

        public IReadOnlyList<CCSPlayerController> Controllers { get; }
        public IReadOnlyList<CCSPlayerController> TeamPlayers { get; }

        public bool TryGetSlot(int slot, out CCSPlayerController player)
        {
            if (slot is >= 0 and < MaxPlayerSlots && _bySlot[slot] is { } value)
            {
                player = value;
                return true;
            }

            player = null!;
            return false;
        }
    }

    private readonly record struct LoadRoundResult(bool Ok, string Message)
    {
        public static LoadRoundResult Success(string message) => new(true, message);
        public static LoadRoundResult Fail(string message) => new(false, message);
    }

    private readonly record struct LoadedReplay(
        string Path,
        string PlayerName,
        ulong SteamId,
        CsTeam? ManifestTeam,
        int FirstWeaponDefIndex,
        int[] PreloadWeaponDefIndices,
        bool HasLoadout,
        ReplayLoadoutSnapshot Loadout,
        int MusicKitId,
        ReplayScoreboardFlair? ScoreboardFlair,
        ReplayCosmetics Cosmetics,
        ReplayView View,
        ReplayPlayerScoreboard Scoreboard,
        ReplayProjectileEvent[] Projectiles,
        ReplayHifiEvent[] HifiEvents,
        ReplayInventorySnapshot[] InventorySnapshots,
        uint? RoundStartBalance,
        int TickCount,
        float TickRate,
        uint PlayStartTickIndex,
        ReplayVector3? RoundStartOrigin,
        int RetentionRank);

    private readonly record struct ReplayMusicKitBaseline(
        long Generation,
        int UserId,
        ushort InventoryMusicKitId,
        int ControllerMusicKitId,
        int ControllerMusicKitMvps,
        bool MvpNoMusic);

    private readonly record struct ReplayAssignment(
        ManifestFile File,
        CCSPlayerController Bot,
        int RetentionRank);

    private readonly record struct LoadedTeamAvatarOverride(
        string ManifestDirectory,
        ManifestAvatarOverride Avatar,
        string ContentKey);

    private readonly record struct AppliedHumanTeamAvatarOverride(
        int? UserId,
        ulong SteamId,
        string ContentKey);

    private readonly record struct DtrKickCandidate(
        int Slot,
        int? UserId,
        CsTeam Team,
        string LiveName,
        string LoadedName,
        ulong SteamId,
        int RetentionRank);

    private readonly record struct PendingWeaponSlotReplacement(
        int PlayerSlot,
        int PlayerUserId,
        uint PawnEntityHandle,
        long ReplayWriteEpoch,
        string TargetItem,
        string FallbackItem,
        ReplayWeaponSlot WeaponSlot);

    private readonly record struct PendingReplayKnifeSubclassRepair(
        int PlayerSlot,
        int PlayerUserId,
        uint PawnEntityHandle,
        uint WeaponEntityHandle,
        long ReplayWriteEpoch,
        ulong ReplaySteamId,
        int ItemDefinitionIndex);

    private readonly record struct AppliedActiveWeaponCosmetic(int WeaponDefIndex, nint WeaponHandle);

    private readonly record struct AppliedCosmeticEntityWrite(
        long ReplayIdentityGeneration,
        ulong ReplaySteamId,
        object CosmeticSource);

    private readonly record struct PendingBulletHit(int AttackerSlot, float Time);

    private readonly record struct PendingBulletDamage(int AttackerSlot, int Damage, float Time);

    private readonly record struct PendingThreat360(int EnemySlot, float FirstSeenAt);

    private enum ProjectileAlignDecision
    {
        Apply,
        Retry,
        Skip
    }

    private enum MolotovPointAlignMode
    {
        Off,
        Teleport,
        Detonate
    }

    private sealed class PendingProjectileAlign(
        uint index,
        IntPtr handle,
        ReplayProjectileKind kind,
        int weaponDefIndex)
    {
        public uint Index { get; } = index;
        public IntPtr Handle { get; } = handle;
        public ReplayProjectileKind Kind { get; } = kind;
        public int WeaponDefIndex { get; } = weaponDefIndex;
        public ReplayProjectileEvent Align { get; set; }
        public int Slot { get; set; } = -1;
        public int EventIndex { get; set; } = -1;
        public int MatchAttemptsRemaining { get; set; }
        public int WritesRemaining { get; set; }
        public int TotalWritesTarget { get; set; } = ProjectileAlignDefaultTotalWrites;
        public int WritesApplied { get; set; }
        public int LastNativeBirthRc { get; set; } = -99;
        public float FirstWriteTime { get; set; }
        public float LastWriteTime { get; set; }
        public bool Matched { get; set; }
        public bool MolotovPointAlignArmed { get; set; }
        public bool MolotovPointAlignApplied { get; set; }
        public MolotovPointAlignMode MolotovPointAlignMode { get; set; } = MolotovPointAlignMode.Off;
        public int MolotovPointAlignTargetTickIndex { get; set; } = -1;
    }

    private enum HandoffMode
    {
        Off,
        Death,
        Contact,
        DeathOrContact,
        DeathContactC4
    }

    private enum ReplayReleaseKind
    {
        Immediate,
        Handoff,
        Finished
    }

    private static string EscapeConsoleString(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static bool ParseOnOff(string value, bool fallback)
        => value.ToLowerInvariant() switch
        {
            "1" or "on" or "true" or "yes" or "full" or "name" => true,
            "0" or "off" or "false" or "no" => false,
            _ => fallback,
        };

    private static string FormatOnOff(bool value)
        => value ? "on" : "off";

    private static bool TryParseProjectileAlignTicks(string value, out int ticks)
    {
        var normalized = value.Trim().ToLowerInvariant();
        ticks = normalized switch
        {
            "status" => int.MinValue,
            "default" or "normal" => ProjectileAlignDefaultTotalWrites,
            "once" or "single" => 1,
            "until_delete" or "until-delete" or "per_tick" or "per-tick" or "tick" or "every_tick" or "every-tick" => ProjectileAlignUntilDelete,
            _ => 0
        };
        if (ticks != 0)
            return true;

        if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
            return false;
        if (ticks < 1 || ticks > ProjectileAlignMaxTotalWrites)
            return false;
        return true;
    }

    private string FormatProjectileAlignTicks()
        => _projectileAlignTotalWrites == ProjectileAlignUntilDelete
            ? "until_delete"
            : _projectileAlignTotalWrites.ToString(CultureInfo.InvariantCulture);

    private static bool TryParseMolotovPointAlignMode(string value, out MolotovPointAlignMode? mode)
    {
        mode = value.Trim().ToLowerInvariant() switch
        {
            "status" => null,
            "off" or "0" or "false" or "none" => MolotovPointAlignMode.Off,
            "teleport" or "tp" => MolotovPointAlignMode.Teleport,
            "detonate" or "effect" or "point" or "1" or "true" => MolotovPointAlignMode.Detonate,
            _ => null
        };

        return mode.HasValue || value.Equals("status", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatMolotovPointAlignMode(MolotovPointAlignMode mode)
        => mode switch
        {
            MolotovPointAlignMode.Teleport => "teleport",
            MolotovPointAlignMode.Detonate => "detonate",
            _ => "off"
        };

    private static string FormatPendingMolotovPointAlign(PendingProjectileAlign pending)
        => pending.MolotovPointAlignArmed
            ? $"{FormatMolotovPointAlignMode(pending.MolotovPointAlignMode)}:{pending.MolotovPointAlignTargetTickIndex}"
            : "off";

    private static string CurrentMapName()
    {
        try
        {
            return Server.MapName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool CheckManifestMap(CommandInfo command, string manifestMap, string manifestPath)
    {
        if (CurrentMapMatchesManifest(manifestMap, out var currentMap))
            return true;

        command.ReplyToCommand(
            $"[DTR ERR] map mismatch: server=\"{currentMap}\" manifest=\"{manifestMap}\" path=\"{manifestPath}\"");
        return false;
    }

    private static bool CurrentMapMatchesManifest(string manifestMap, out string currentMap)
    {
        currentMap = CurrentMapName();
        if (string.IsNullOrWhiteSpace(manifestMap) ||
            string.IsNullOrWhiteSpace(currentMap) ||
            currentMap.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return NormalizeMapName(currentMap).Equals(NormalizeMapName(manifestMap), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMapName(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.StartsWith("de_", StringComparison.Ordinal)
            ? normalized[3..]
            : normalized;
    }

    private static string FormatRoundList(IReadOnlyList<int> rounds)
    {
        if (rounds.Count == 0)
            return "none";
        if (rounds.Count <= 16)
            return string.Join(",", rounds);
        return $"{string.Join(",", rounds.Take(16))},... ({rounds.Count})";
    }

    private static bool CheckAbi(CommandInfo command)
    {
        if (BotControllerNative.IsCompatible)
            return true;

        command.ReplyToCommand(
            $"dtr: ABI mismatch; {BotControllerNative.RuntimeSummary}");
        return false;
    }

    private static bool TryParseRoundArgs(
        CommandInfo command,
        string commandName,
        out string manifestPath,
        out int round,
        int argOffset = 1)
    {
        manifestPath = string.Empty;
        round = 0;
        if (command.ArgCount <= argOffset + 1)
        {
            command.ReplyToCommand($"usage: {commandName} <manifest.json> <source_round>");
            return false;
        }

        manifestPath = command.GetArg(argOffset);
        if (int.TryParse(command.GetArg(argOffset + 1), out round) && round >= 0)
            return true;

        command.ReplyToCommand("dtr: source_round must be a non-negative integer");
        return false;
    }

    private static bool TryParseSlot(CommandInfo command, out int slot)
        => TryParseSlotAt(command, 1, out slot);

    private static bool TryParseSlotAt(CommandInfo command, int argIndex, out int slot)
    {
        slot = 0;
        if (command.ArgCount > argIndex &&
            int.TryParse(command.GetArg(argIndex), out slot) &&
            slot is >= 0 and < MaxPlayerSlots)
            return true;

        command.ReplyToCommand($"dtr: slot must be an integer from 0 to {MaxPlayerSlots - 1}");
        return false;
    }

    private static bool TryParseHandoffMode(string value, out HandoffMode mode)
    {
        mode = value.ToLowerInvariant() switch
        {
            "0" or "off" or "none" => HandoffMode.Off,
            "death" or "kill" => HandoffMode.Death,
            "contact" or "see" or "sight" => HandoffMode.Contact,
            "death_or_contact" or "contact_or_death" => HandoffMode.DeathOrContact,
            "1" or "auto" or "default" or
            "death_contact_c4" or "death_contact_c4planted" or "death_contact_c4_planted" or
            "death_or_contact_or_c4" or "death_or_contact_or_bomb" or "death_contact_bomb" => HandoffMode.DeathContactC4,
            _ => HandoffMode.Off
        };
        return value.ToLowerInvariant() is "0" or "off" or "none" or
            "death" or "kill" or
            "contact" or "see" or "sight" or
            "death_or_contact" or "contact_or_death" or
            "1" or "auto" or "default" or
            "death_contact_c4" or "death_contact_c4planted" or "death_contact_c4_planted" or
            "death_or_contact_or_c4" or "death_or_contact_or_bomb" or "death_contact_bomb";
    }

    private static bool HandoffIncludesDeath(HandoffMode mode)
        => mode is HandoffMode.Death or HandoffMode.DeathOrContact or HandoffMode.DeathContactC4;

    private static bool HandoffIncludesContact(HandoffMode mode)
        => mode is HandoffMode.Contact or HandoffMode.DeathOrContact or HandoffMode.DeathContactC4;

    private static bool HandoffIncludesC4(HandoffMode mode)
        => mode is HandoffMode.DeathContactC4;

    private static string FormatHandoffMode(HandoffMode mode)
        => mode switch
        {
            HandoffMode.Off => "off",
            HandoffMode.Death => "death",
            HandoffMode.Contact => "contact",
            HandoffMode.DeathOrContact => "death_or_contact",
            HandoffMode.DeathContactC4 => "death_contact_c4",
            _ => "off"
        };

    private string ReplayIdentityModeName()
        => _replayIdentityMode switch
        {
            ReplayIdentityMode.Name => "name",
            ReplayIdentityMode.Steam => "steam",
            ReplayIdentityMode.Avatar => "avatar",
            _ => "off",
        };

}
