/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal enum ReplayWeaponSlotSyncStatus
{
    Complete,
    Pending,
    RetryRequired
}

internal enum ReplayWeaponSlotPlanAction
{
    Complete,
    GrantIntoEmptySlot,
    ReplaceOccupiedSlot,
    PreserveExisting
}

internal enum WeaponSlotReplacementAction
{
    TargetReady,
    WaitForClear,
    GrantTarget,
    PreserveExisting
}

internal enum WeaponGrantVerificationAction
{
    TargetReady,
    Conflict,
    WaitForAttachment,
    RetryGrant,
    UseFallback
}

internal enum DetachedWeaponCleanupAction
{
    Destroy,
    Retry,
    Abandon
}

internal enum SafeC4AlignmentAction
{
    DropForeignOwners,
    WaitForCleanup,
    WaitForNativeAssignment,
    TargetReady,
    GrantTarget
}

internal static class ReplayWeaponReplacementPolicy
{
    internal static ReplayWeaponSlotPlanAction DecideSlotPlanAction(
        bool hasTarget,
        bool targetPresent,
        bool anySlotWeapon,
        bool canReplaceOccupiedSlot = false)
    {
        if (targetPresent)
            return ReplayWeaponSlotPlanAction.Complete;
        if (anySlotWeapon && canReplaceOccupiedSlot)
            return ReplayWeaponSlotPlanAction.ReplaceOccupiedSlot;
        if (anySlotWeapon)
            return ReplayWeaponSlotPlanAction.PreserveExisting;
        return hasTarget
            ? ReplayWeaponSlotPlanAction.GrantIntoEmptySlot
            : ReplayWeaponSlotPlanAction.Complete;
    }

    internal static bool CanReplaceOccupiedWeaponSlot(
        ReplayWeaponSlot slot,
        string currentItem,
        string targetItem)
    {
        if (slot is not (ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary) ||
            string.IsNullOrWhiteSpace(currentItem) ||
            string.IsNullOrWhiteSpace(targetItem))
        {
            return false;
        }

        return !currentItem.Equals(targetItem, StringComparison.OrdinalIgnoreCase);
    }

    internal static WeaponSlotReplacementAction DecideReplacementProgress(
        bool targetPresent,
        bool anySlotWeapon,
        int clearWaitFramesRemaining)
    {
        if (targetPresent)
            return WeaponSlotReplacementAction.TargetReady;
        if (!anySlotWeapon)
            return WeaponSlotReplacementAction.GrantTarget;
        return clearWaitFramesRemaining > 0
            ? WeaponSlotReplacementAction.WaitForClear
            : WeaponSlotReplacementAction.PreserveExisting;
    }

    internal static WeaponGrantVerificationAction VerifyGrant(
        bool targetPresent,
        bool anySlotWeapon,
        int grantWaitFramesRemaining,
        int grantRetryAttemptsRemaining)
    {
        if (targetPresent)
            return WeaponGrantVerificationAction.TargetReady;
        if (anySlotWeapon)
            return WeaponGrantVerificationAction.Conflict;
        if (grantWaitFramesRemaining > 0)
            return WeaponGrantVerificationAction.WaitForAttachment;
        return grantRetryAttemptsRemaining > 0
            ? WeaponGrantVerificationAction.RetryGrant
            : WeaponGrantVerificationAction.UseFallback;
    }

    internal static bool ShouldCacheFailedSwitch(bool targetPresent)
        => targetPresent;

    internal static DetachedWeaponCleanupAction DecideDetachedWeaponCleanup(
        bool identityMatches,
        bool ownedByPawn,
        bool activeWeaponReference,
        int framesSinceDetach,
        int retriesRemaining)
    {
        if (!identityMatches)
            return DetachedWeaponCleanupAction.Abandon;

        if (framesSinceDetach <= 0)
        {
            return retriesRemaining > 0
                ? DetachedWeaponCleanupAction.Retry
                : DetachedWeaponCleanupAction.Abandon;
        }

        if (!ownedByPawn && !activeWeaponReference)
            return DetachedWeaponCleanupAction.Destroy;

        return retriesRemaining > 0
            ? DetachedWeaponCleanupAction.Retry
            : DetachedWeaponCleanupAction.Abandon;
    }

    internal static SafeC4AlignmentAction DecideSafeC4Alignment(
        bool targetHasC4,
        int foreignOwnerCount,
        int pendingDropCount,
        bool grantPending,
        bool replacementAuthorized)
    {
        if (foreignOwnerCount > 0)
            return SafeC4AlignmentAction.DropForeignOwners;
        if (pendingDropCount > 0)
            return SafeC4AlignmentAction.WaitForCleanup;
        if (targetHasC4)
            return SafeC4AlignmentAction.TargetReady;
        if (grantPending)
            return SafeC4AlignmentAction.WaitForCleanup;
        return replacementAuthorized
            ? SafeC4AlignmentAction.GrantTarget
            : SafeC4AlignmentAction.WaitForNativeAssignment;
    }

    internal static bool CanUseActiveWeaponDropForC4(
        bool pawnOwnsC4,
        bool c4IsActiveWeapon)
        => pawnOwnsC4 && c4IsActiveWeapon;

    internal static bool CanMutateForeignC4Owner(
        bool isSafeReplayTargetBot,
        bool hasLoadedReplay,
        bool replayOwnsSlot)
        => isSafeReplayTargetBot && (!hasLoadedReplay || replayOwnsSlot);

    internal static bool ShouldRestoreFallback(
        bool samePlayer,
        bool samePawn,
        bool targetPresent,
        bool anySlotWeapon)
        => samePlayer && samePawn && !targetPresent && !anySlotWeapon;

    internal static string EmptySlotFallbackItem(
        ReplayWeaponSlot slot,
        bool counterTerrorist,
        string targetItem)
        => targetItem;
}
