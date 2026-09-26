/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class WeaponSlotReplacementTests
{
    [Theory]
    [InlineData(true, true, true, (int)ReplayWeaponSlotPlanAction.Complete)]
    [InlineData(true, false, true, (int)ReplayWeaponSlotPlanAction.PreserveExisting)]
    [InlineData(false, false, true, (int)ReplayWeaponSlotPlanAction.PreserveExisting)]
    [InlineData(true, false, false, (int)ReplayWeaponSlotPlanAction.GrantIntoEmptySlot)]
    [InlineData(false, false, false, (int)ReplayWeaponSlotPlanAction.Complete)]
    public void PreparationPreservesOccupiedSlotsWithoutReplacementAuthorization(
        bool hasTarget,
        bool targetPresent,
        bool anySlotWeapon,
        int expected)
    {
        Assert.Equal(
            (ReplayWeaponSlotPlanAction)expected,
            ReplayWeaponReplacementPolicy.DecideSlotPlanAction(
                hasTarget,
                targetPresent,
                anySlotWeapon));
    }

    [Theory]
    [InlineData((int)ReplayWeaponSlot.Primary, "weapon_ak47", "weapon_awp")]
    [InlineData((int)ReplayWeaponSlot.Primary, "weapon_awp", "weapon_ak47")]
    [InlineData((int)ReplayWeaponSlot.Secondary, "weapon_hkp2000", "weapon_elite")]
    [InlineData((int)ReplayWeaponSlot.Secondary, "weapon_deagle", "weapon_usp_silencer")]
    public void OccupiedWeaponSlotsCanUseAConflictingManifestTarget(
        int slot,
        string currentItem,
        string targetItem)
    {
        Assert.True(ReplayWeaponReplacementPolicy.CanReplaceOccupiedWeaponSlot(
            (ReplayWeaponSlot)slot,
            currentItem,
            targetItem));
        Assert.Equal(
            ReplayWeaponSlotPlanAction.ReplaceOccupiedSlot,
            ReplayWeaponReplacementPolicy.DecideSlotPlanAction(
                hasTarget: true,
                targetPresent: false,
                anySlotWeapon: true,
                canReplaceOccupiedSlot: true));
    }

    [Theory]
    [InlineData((int)ReplayWeaponSlot.Utility, "weapon_smokegrenade", "weapon_flashbang")]
    [InlineData((int)ReplayWeaponSlot.Secondary, "weapon_hkp2000", "weapon_hkp2000")]
    [InlineData((int)ReplayWeaponSlot.Primary, "", "weapon_awp")]
    [InlineData((int)ReplayWeaponSlot.Primary, "weapon_ak47", "")]
    public void UnsupportedOrIdenticalOccupiedWeaponsRemainOutsideTheReplacementPath(
        int slot,
        string currentItem,
        string targetItem)
    {
        Assert.False(ReplayWeaponReplacementPolicy.CanReplaceOccupiedWeaponSlot(
            (ReplayWeaponSlot)slot,
            currentItem,
            targetItem));
        Assert.Equal(
            ReplayWeaponSlotPlanAction.PreserveExisting,
            ReplayWeaponReplacementPolicy.DecideSlotPlanAction(
                hasTarget: true,
                targetPresent: false,
                anySlotWeapon: true));
    }

    [Fact]
    public void OccupiedWeaponReplacementWaitsForTheOldWeaponToClear()
    {
        Assert.Equal(
            WeaponSlotReplacementAction.WaitForClear,
            ReplayWeaponReplacementPolicy.DecideReplacementProgress(
                targetPresent: false,
                anySlotWeapon: true,
                clearWaitFramesRemaining: 8));
        Assert.Equal(
            WeaponSlotReplacementAction.GrantTarget,
            ReplayWeaponReplacementPolicy.DecideReplacementProgress(
                targetPresent: false,
                anySlotWeapon: false,
                clearWaitFramesRemaining: 7));
    }

    [Theory]
    [InlineData(true, false, false, 0, 8, (int)DetachedWeaponCleanupAction.Retry)]
    [InlineData(true, false, false, 1, 8, (int)DetachedWeaponCleanupAction.Destroy)]
    [InlineData(true, true, false, 1, 8, (int)DetachedWeaponCleanupAction.Retry)]
    [InlineData(true, false, true, 1, 8, (int)DetachedWeaponCleanupAction.Retry)]
    [InlineData(true, true, true, 9, 0, (int)DetachedWeaponCleanupAction.Abandon)]
    [InlineData(false, false, false, 1, 8, (int)DetachedWeaponCleanupAction.Abandon)]
    public void DetachedWeaponCleanupWaitsForEngineReferences(
        bool identityMatches,
        bool ownedByPawn,
        bool activeWeaponReference,
        int framesSinceDetach,
        int retriesRemaining,
        int expected)
    {
        Assert.Equal(
            (DetachedWeaponCleanupAction)expected,
            ReplayWeaponReplacementPolicy.DecideDetachedWeaponCleanup(
                identityMatches,
                ownedByPawn,
                activeWeaponReference,
                framesSinceDetach,
                retriesRemaining));
    }

    [Theory]
    [InlineData(true, true, 4, 1, (int)WeaponGrantVerificationAction.TargetReady)]
    [InlineData(false, true, 4, 1, (int)WeaponGrantVerificationAction.Conflict)]
    [InlineData(false, false, 4, 1, (int)WeaponGrantVerificationAction.WaitForAttachment)]
    [InlineData(false, false, 0, 1, (int)WeaponGrantVerificationAction.RetryGrant)]
    [InlineData(false, false, 0, 0, (int)WeaponGrantVerificationAction.UseFallback)]
    public void GrantCompletionUsesObservedInventoryNotTheReturnedEntityPointer(
        bool targetPresent,
        bool anySlotWeapon,
        int grantWaitFramesRemaining,
        int grantRetryAttemptsRemaining,
        int expected)
    {
        Assert.Equal(
            (WeaponGrantVerificationAction)expected,
            ReplayWeaponReplacementPolicy.VerifyGrant(
                targetPresent,
                anySlotWeapon,
                grantWaitFramesRemaining,
                grantRetryAttemptsRemaining));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void FailedSwitchIsCachedOnlyWhenTheTargetWeaponExists(
        bool targetPresent,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReplayWeaponReplacementPolicy.ShouldCacheFailedSwitch(targetPresent));
    }

    [Theory]
    [InlineData(false, 1, 0, false, false, (int)SafeC4AlignmentAction.DropForeignOwners)]
    [InlineData(true, 0, 1, false, false, (int)SafeC4AlignmentAction.WaitForCleanup)]
    [InlineData(true, 0, 0, false, false, (int)SafeC4AlignmentAction.TargetReady)]
    [InlineData(false, 0, 0, true, true, (int)SafeC4AlignmentAction.WaitForCleanup)]
    [InlineData(false, 0, 0, false, false, (int)SafeC4AlignmentAction.WaitForNativeAssignment)]
    [InlineData(false, 0, 0, false, true, (int)SafeC4AlignmentAction.GrantTarget)]
    public void SafeC4TransferNeverGrantsUntilForeignOwnershipAndCleanupAreClear(
        bool targetHasC4,
        int foreignOwnerCount,
        int pendingDropCount,
        bool grantPending,
        bool replacementAuthorized,
        int expected)
    {
        Assert.Equal(
            (SafeC4AlignmentAction)expected,
            ReplayWeaponReplacementPolicy.DecideSafeC4Alignment(
                targetHasC4,
                foreignOwnerCount,
                pendingDropCount,
                grantPending,
                replacementAuthorized));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void C4CanUseTheActiveWeaponDropPathOnlyWhenItIsActuallyActive(
        bool pawnOwnsC4,
        bool c4IsActiveWeapon,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReplayWeaponReplacementPolicy.CanUseActiveWeaponDropForC4(
                pawnOwnsC4,
                c4IsActiveWeapon));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, true)]
    public void C4AlignmentNeverMutatesHumanOrUnownedReplaySlots(
        bool isSafeReplayTargetBot,
        bool hasLoadedReplay,
        bool replayOwnsSlot,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReplayWeaponReplacementPolicy.CanMutateForeignC4Owner(
                isSafeReplayTargetBot,
                hasLoadedReplay,
                replayOwnsSlot));
    }

    [Theory]
    [InlineData(true, true, false, false, true)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    public void CancellationRestoresFallbackOnlyToTheSameEmptyPawn(
        bool samePlayer,
        bool samePawn,
        bool targetPresent,
        bool anySlotWeapon,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReplayWeaponReplacementPolicy.ShouldRestoreFallback(
                samePlayer,
                samePawn,
                targetPresent,
                anySlotWeapon));
    }

    [Theory]
    [InlineData(true, "weapon_usp_silencer")]
    [InlineData(true, "weapon_hkp2000")]
    [InlineData(false, "weapon_glock")]
    public void EmptySidearmSlotRetriesTheRequestedModel(
        bool counterTerrorist,
        string targetItem)
    {
        Assert.Equal(
            targetItem,
            ReplayWeaponReplacementPolicy.EmptySlotFallbackItem(
                ReplayWeaponSlot.Secondary,
                counterTerrorist,
                targetItem));
    }
}
