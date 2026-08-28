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
        uint? evidence,
        int? serverMaxMoney,
        out int balance)
    {
        balance = 0;
        if (!enabled || evidence is null)
            return false;

        var maximum = serverMaxMoney is >= 0 ? serverMaxMoney.Value : int.MaxValue;
        balance = (int)Math.Min(evidence.Value, (uint)maximum);
        return true;
    }

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

}
