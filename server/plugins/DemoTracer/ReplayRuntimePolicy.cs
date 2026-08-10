/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal readonly record struct FreezePrerollTiming(
    float DelaySeconds,
    float PlaybackSeconds);

internal static class ReplayRuntimePolicy
{
    internal static Version MaxVerifiedManagedSchemaPatch { get; } = new(1, 41, 7, 4);

    internal static IReadOnlyList<string> ManagedSchemaSteamInfCandidates(
        string? gameDirectory,
        string? assemblyLocation)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return;
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (seen.Add(fullPath))
                    candidates.Add(fullPath);
            }
            catch
            {
                // Invalid host paths are ignored; detection remains fail-closed.
            }
        }

        if (!string.IsNullOrWhiteSpace(gameDirectory))
            AddCandidate(Path.Combine(gameDirectory, "steam.inf"));

        string? directory = null;
        try
        {
            directory = Path.GetDirectoryName(assemblyLocation);
        }
        catch
        {
        }
        for (var depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(directory); depth++)
        {
            AddCandidate(Path.Combine(directory, "steam.inf"));
            directory = Directory.GetParent(directory)?.FullName;
        }

        return candidates;
    }

    internal static FreezePrerollTiming ComputeFreezePrerollTiming(
        float freezeTimeSeconds,
        float phaseRemainingSeconds,
        float maxRecordedPrerollSeconds)
    {
        var safeFreezeSeconds = Math.Max(0.0f, freezeTimeSeconds);
        var safeRemainingSeconds = Math.Clamp(
            phaseRemainingSeconds,
            0.0f,
            safeFreezeSeconds);
        var safeRecordedSeconds = Math.Max(0.0f, maxRecordedPrerollSeconds);
        var playbackSeconds = Math.Min(safeRemainingSeconds, safeRecordedSeconds);
        return new FreezePrerollTiming(
            Math.Max(0.0f, safeRemainingSeconds - playbackSeconds),
            playbackSeconds);
    }

    internal static bool TryResolveRoundStartBalance(
        bool enabled,
        bool runtimeSupported,
        uint? evidence,
        int? serverMaxMoney,
        out int balance)
    {
        balance = 0;
        if (!enabled || !runtimeSupported || evidence is null)
            return false;

        var maximum = serverMaxMoney is >= 0 ? serverMaxMoney.Value : int.MaxValue;
        balance = (int)Math.Min(evidence.Value, (uint)maximum);
        return true;
    }

    internal static bool MusicKitStateMatches(
        int expectedMusicKitId,
        int? inventoryMusicKitId,
        int controllerMusicKitId,
        int controllerMusicKitMvps,
        bool mvpNoMusic)
        => expectedMusicKitId is > 0 and <= ushort.MaxValue &&
           inventoryMusicKitId == (ushort)expectedMusicKitId &&
           controllerMusicKitId == expectedMusicKitId &&
           controllerMusicKitMvps == 0 &&
           !mvpNoMusic;

    internal static bool PawnEquipmentStateMatches(
        int expectedArmor,
        bool expectedHelmet,
        bool expectedDefuser,
        int pawnArmor,
        bool itemServicesAvailable,
        bool itemServicesHelmet,
        bool itemServicesDefuser,
        int controllerArmor,
        bool controllerHelmet,
        bool controllerDefuser)
        => itemServicesAvailable &&
           pawnArmor == expectedArmor &&
           itemServicesHelmet == expectedHelmet &&
           itemServicesDefuser == expectedDefuser &&
           controllerArmor == expectedArmor &&
           controllerHelmet == expectedHelmet &&
           controllerDefuser == expectedDefuser;

    internal static bool ShouldApplyMusicKit(
        bool cosmeticAlignEnabled,
        bool runtimeSupported,
        int musicKitId)
        => cosmeticAlignEnabled &&
           runtimeSupported &&
           musicKitId is > 0 and <= ushort.MaxValue;

    internal static bool ShouldApplyScoreboardFlair(bool identitySupportsFlair)
        => identitySupportsFlair;

    internal static bool IsManagedSchemaPatchSupported(string? patch)
        => Version.TryParse(patch, out var current) &&
           current.CompareTo(MaxVerifiedManagedSchemaPatch) <= 0;
}
