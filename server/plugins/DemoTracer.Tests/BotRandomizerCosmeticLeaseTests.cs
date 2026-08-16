/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotRandomizerApi;

namespace DemoTracer.Tests;

public sealed class BotRandomizerCosmeticLeaseTests
{
    [Fact]
    public void EmptyEvidenceDoesNotCreateAWriteLease()
    {
        var claim = BuildClaim(Evidence());

        Assert.Null(claim);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void AgentAlignmentOwnsMissingEvidenceToPreserveTheMapDefault(
        bool cosmeticAlignEnabled,
        bool cosmeticAgentsEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            DemoTracerPlugin.ShouldClaimAgentOwnership(
                cosmeticAlignEnabled,
                cosmeticAgentsEnabled));
    }

    [Theory]
    [InlineData("characters/models/tm_phoenix/tm_phoenix.vmdl", "characters\\models\\tm_phoenix\\tm_phoenix.vmdl")]
    [InlineData("agents/models/professional/varf.vmdl", null)]
    [InlineData("characters/models/../bad.vmdl", null)]
    public void NativeAgentCaptureAcceptsOnlyMapDefaultModelPaths(
        string input,
        string? expected)
    {
        Assert.Equal(expected, DemoTracerPlugin.NormalizeNativeAgentModelPath(input));
    }

    [Fact]
    public void KnifeAndGloveClaimsRequirePositiveEvidence()
    {
        var claim = BuildClaim(Evidence(knife: true, gloves: true));

        Assert.NotNull(claim);
        Assert.False(claim.Agent);
        Assert.True(claim.Knife);
        Assert.True(claim.Gloves);
        Assert.Empty(claim.Weapons);
    }

    [Fact]
    public void AkEvidenceDoesNotClaimM4()
    {
        var claim = BuildClaim(Evidence(
            weapons:
            [
                PaintedWeapon(7)
            ]));

        var weapon = Assert.Single(Assert.IsType<BotRandomizerCosmeticWriteClaim>(claim).Weapons);
        Assert.Equal(7, weapon.WeaponDefinitionIndex);
        Assert.True(weapon.Paint);
        Assert.DoesNotContain(claim.Weapons, candidate => candidate.WeaponDefinitionIndex is 16 or 60);
        Assert.False(claim.Agent);
        Assert.False(claim.Knife);
        Assert.False(claim.Gloves);
    }

    [Theory]
    [InlineData(60, 106u, false)]
    [InlineData(61, 60u, true)]
    public void SilencedWeaponEvidenceClaimsTheExactDefinitionAndEconFamilies(
        int weaponDefinitionIndex,
        uint paintKit,
        bool usesLegacyModel)
    {
        var claim = Assert.IsType<BotRandomizerCosmeticWriteClaim>(BuildClaim(Evidence(
            weapons:
            [
                PaintedWeapon(
                    weaponDefinitionIndex,
                    stickers: true,
                    keychain: true,
                    usesLegacyModel: usesLegacyModel,
                    paintKit: paintKit)
            ])));

        var weapon = Assert.Single(claim.Weapons);
        Assert.Equal(weaponDefinitionIndex, weapon.WeaponDefinitionIndex);
        Assert.True(weapon.Paint);
        Assert.True(weapon.Stickers);
        Assert.True(weapon.Keychain);
        Assert.Equal(paintKit, weapon.PaintKit);
        Assert.Equal(321u, weapon.PaintSeed);
        Assert.Equal(0.15f, weapon.PaintWear);
        Assert.Equal(usesLegacyModel, weapon.PaintUsesLegacyModel);
    }

    [Fact]
    public void PaintOnlyPreservesRandomizerAttachmentFamilies()
    {
        var claim = BuildClaim(Evidence(
            weapons:
            [
                PaintedWeapon(7)
            ]));

        var weapon = Assert.Single(Assert.IsType<BotRandomizerCosmeticWriteClaim>(claim).Weapons);
        Assert.True(weapon.Paint);
        Assert.False(weapon.Stickers);
        Assert.False(weapon.Keychain);
        Assert.False(DemoTracerPlugin.ShouldClearCompleteAttributeLists(
            DemoTracerCosmeticWriteField.WeaponPaint));
    }

    [Fact]
    public void StickerEvidenceDoesNotClaimMissingKeychain()
    {
        var claim = BuildClaim(Evidence(
            weapons:
            [
                PaintedWeapon(7, stickers: true, usesLegacyModel: true)
            ]));

        var weapon = Assert.Single(Assert.IsType<BotRandomizerCosmeticWriteClaim>(claim).Weapons);
        Assert.True(weapon.Paint);
        Assert.True(weapon.Stickers);
        Assert.False(weapon.Keychain);
        Assert.True(weapon.PaintUsesLegacyModel);
    }

    [Fact]
    public void IncompletePaintTupleNeverClaimsPaintOwnership()
    {
        var claim = Assert.IsType<BotRandomizerCosmeticWriteClaim>(BuildClaim(Evidence(
            weapons:
            [
                new DemoTracerBotRandomizerWeaponEvidence(
                    7,
                    Paint: true,
                    Stickers: true,
                    Keychain: false,
                    PaintUsesLegacyModel: false,
                    PaintKit: 44,
                    PaintSeed: null,
                    PaintWear: 0.15f)
            ])));

        var weapon = Assert.Single(claim.Weapons);
        Assert.False(weapon.Paint);
        Assert.Null(weapon.PaintKit);
        Assert.Null(weapon.PaintSeed);
        Assert.Null(weapon.PaintWear);
        Assert.True(weapon.Stickers);
    }

    [Fact]
    public void PaintClaimsRequireProviderSideAuthoritativePrebuild()
    {
        var paintClaim = Assert.IsType<BotRandomizerCosmeticWriteClaim>(BuildClaim(Evidence(
            weapons: [PaintedWeapon(7)])));
        var attachmentOnlyClaim = Assert.IsType<BotRandomizerCosmeticWriteClaim>(BuildClaim(Evidence(
            weapons:
            [
                new DemoTracerBotRandomizerWeaponEvidence(7, false, true, false, null)
            ])));

        Assert.True(DemoTracerPlugin.RequestsRequireAuthoritativePaintPrebuild([paintClaim]));
        Assert.False(DemoTracerPlugin.RequestsRequireAuthoritativePaintPrebuild([attachmentOnlyClaim]));
    }

    [Fact]
    public void EmptyOriginalOrDefaultWeaponEvidenceDoesNotCreateAWriteLease()
    {
        var claim = BuildClaim(Evidence(
            weapons:
            [
                new DemoTracerBotRandomizerWeaponEvidence(7, false, false, false, null)
            ]));

        Assert.Null(claim);
    }

    [Fact]
    public void SnapshotRejectsSlotReuseAndWrongSubject()
    {
        var apiClaim = Assert.IsType<BotRandomizerCosmeticWriteClaim>(BuildClaim(Evidence(
            weapons:
            [
                PaintedWeapon(7)
            ])));
        var snapshot = new DemoTracerBotRandomizerLeaseSnapshot();
        snapshot.Activate("token", "epoch-a", [apiClaim]);

        Assert.True(snapshot.TryGet(Slot, SubjectSteamId, out var active));
        Assert.True(active.MatchesIdentity(Incarnation, SubjectSteamId));
        Assert.False(active.MatchesIdentity(Incarnation + 1, SubjectSteamId));
        Assert.False(active.MatchesIdentity(Incarnation, SubjectSteamId + 1));
        Assert.False(snapshot.TryGet(Slot, SubjectSteamId + 1, out _));
    }

    [Fact]
    public void AlignedTakeoverCanRetainTheExactAuthenticatedClaimWithoutLiveProviderQueries()
    {
        var apiClaim = Assert.IsType<BotRandomizerCosmeticWriteClaim>(BuildClaim(Evidence(
            agent: true,
            knife: true,
            gloves: true,
            musicKit: true,
            weapons:
            [
                PaintedWeapon(7, stickers: true, keychain: true)
            ])));
        var snapshot = new DemoTracerBotRandomizerLeaseSnapshot();
        snapshot.Activate("token", "epoch-a", [apiClaim]);

        Assert.True(snapshot.TryBuildRetainedApiClaim(
            Slot,
            SubjectSteamId,
            out var retained));
        Assert.Equal(apiClaim.Slot, retained.Slot);
        Assert.Equal(apiClaim.Incarnation, retained.Incarnation);
        Assert.Equal(apiClaim.SubjectSteamId, retained.SubjectSteamId);
        Assert.Equal(apiClaim.Agent, retained.Agent);
        Assert.Equal(apiClaim.Knife, retained.Knife);
        Assert.Equal(apiClaim.Gloves, retained.Gloves);
        Assert.Equal(apiClaim.MusicKit, retained.MusicKit);
        Assert.Equal(apiClaim.Weapons, retained.Weapons, BotRandomizerWeaponClaimComparer.Instance);
    }

    [Fact]
    public void ReleasedReplayLeaseAllowsRandomizerToOwnAgentKnifeAndGlovesAgain()
    {
        var apiClaim = Assert.IsType<BotRandomizerCosmeticWriteClaim>(BuildClaim(Evidence(
            agent: true,
            knife: true,
            gloves: true,
            weapons:
            [
                PaintedWeapon(7, stickers: true, keychain: true)
            ])));
        var snapshot = new DemoTracerBotRandomizerLeaseSnapshot();
        snapshot.Activate("token", "epoch-a", [apiClaim]);

        Assert.True(snapshot.TryGet(Slot, SubjectSteamId, out var active));
        Assert.True(active.Allows(DemoTracerCosmeticWriteField.Agent));
        Assert.True(active.Allows(DemoTracerCosmeticWriteField.Knife));
        Assert.True(active.Allows(DemoTracerCosmeticWriteField.Gloves));

        snapshot.Invalidate();

        Assert.Equal(string.Empty, snapshot.Token);
        Assert.Equal(string.Empty, snapshot.ProviderEpoch);
        Assert.Empty(snapshot.Claims);
        Assert.False(snapshot.TryGet(Slot, SubjectSteamId, out _));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void AlignedLivePawnKeepsItsCosmeticFenceAfterReplayControlRelease(
        bool canWriteReplaySlot,
        bool currentPawnCosmeticsAligned,
        bool expected)
    {
        Assert.Equal(
            expected,
            DemoTracerPlugin.ShouldHoldBotRandomizerCosmeticLease(
                canWriteReplaySlot,
                currentPawnCosmeticsAligned));
    }

    [Theory]
    [InlineData(true, "slot_not_managed:3", true)]
    [InlineData(true, "stale_incarnation:3", true)]
    [InlineData(true, "lease_not_found", false)]
    [InlineData(false, "provider_unavailable", false)]
    public void FailedReplacementKeepsAnExistingExclusionFenceUnlessProviderLostIt(
        bool hadActiveLease,
        string reason,
        bool expected)
    {
        Assert.Equal(
            expected,
            DemoTracerPlugin.ShouldRetainActiveBotRandomizerLeaseAfterSyncFailure(
                hadActiveLease,
                reason));
    }

    [Fact]
    public void WeaponFieldClaimsNeverAuthorizeWholeAttributeListClears()
    {
        Assert.False(DemoTracerPlugin.ShouldClearCompleteAttributeLists(
            DemoTracerCosmeticWriteField.WeaponPaint));
        Assert.False(DemoTracerPlugin.ShouldClearCompleteAttributeLists(
            DemoTracerCosmeticWriteField.WeaponStickers));
        Assert.False(DemoTracerPlugin.ShouldClearCompleteAttributeLists(
            DemoTracerCosmeticWriteField.WeaponKeychain));
    }

    [Fact]
    public void WholeItemClaimsMayRebuildTheirAttributeLists()
    {
        Assert.True(DemoTracerPlugin.ShouldClearCompleteAttributeLists(
            DemoTracerCosmeticWriteField.Knife));
        Assert.True(DemoTracerPlugin.ShouldClearCompleteAttributeLists(
            DemoTracerCosmeticWriteField.Gloves));
    }

    [Theory]
    [InlineData(true, true, true, true, true, true, true)]
    [InlineData(false, true, true, true, true, true, false)]
    [InlineData(true, false, true, true, true, true, false)]
    [InlineData(true, true, false, true, true, true, false)]
    [InlineData(true, true, true, false, true, true, false)]
    [InlineData(true, true, true, true, false, true, false)]
    [InlineData(true, true, true, true, true, false, false)]
    public void KnifeSubclassRepairRequiresTheSameOwnedLeasedEntity(
        bool writeEpochCurrent,
        bool samePlayer,
        bool samePawn,
        bool sameWeapon,
        bool ownedKnife,
        bool activeClaim,
        bool expected)
    {
        Assert.Equal(
            expected,
            DemoTracerPlugin.CanReassertReplayKnifeSubclass(
                writeEpochCurrent,
                samePlayer,
                samePawn,
                sameWeapon,
                ownedKnife,
                activeClaim));
    }

    [Fact]
    public void LegacyCosmeticsKeepSeedEvidenceWhileExplicitlyUnknownSeedsDoNot()
    {
        Assert.True(DemoTracerPlugin.HasCosmeticSeedEvidence(null));
        Assert.True(DemoTracerPlugin.HasCosmeticSeedEvidence(true));
        Assert.False(DemoTracerPlugin.HasCosmeticSeedEvidence(false));
    }

    private static BotRandomizerCosmeticWriteClaim? BuildClaim(
        DemoTracerBotRandomizerClaimEvidence evidence)
        => DemoTracerPlugin.BuildBotRandomizerWriteClaim(
            Slot,
            Incarnation,
            SubjectSteamId,
            evidence);

    private static DemoTracerBotRandomizerClaimEvidence Evidence(
        bool agent = false,
        bool knife = false,
        bool gloves = false,
        bool musicKit = false,
        IReadOnlyList<DemoTracerBotRandomizerWeaponEvidence>? weapons = null)
        => new(agent, knife, gloves, musicKit, weapons ?? []);

    private static DemoTracerBotRandomizerWeaponEvidence PaintedWeapon(
        int weaponDefinitionIndex,
        bool stickers = false,
        bool keychain = false,
        bool usesLegacyModel = false,
        uint paintKit = 44,
        uint paintSeed = 321,
        float paintWear = 0.15f)
        => new(
            weaponDefinitionIndex,
            Paint: true,
            Stickers: stickers,
            Keychain: keychain,
            PaintUsesLegacyModel: usesLegacyModel,
            PaintKit: paintKit,
            PaintSeed: paintSeed,
            PaintWear: paintWear);

    private const int Slot = 3;
    private const ulong Incarnation = 11;
    private const ulong SubjectSteamId = 76561198000000003;

    private sealed class BotRandomizerWeaponClaimComparer : IEqualityComparer<BotRandomizerWeaponWriteClaim>
    {
        internal static BotRandomizerWeaponClaimComparer Instance { get; } = new();

        public bool Equals(
            BotRandomizerWeaponWriteClaim? left,
            BotRandomizerWeaponWriteClaim? right)
            => left is not null &&
               right is not null &&
               left.WeaponDefinitionIndex == right.WeaponDefinitionIndex &&
               left.Paint == right.Paint &&
               left.Stickers == right.Stickers &&
               left.Keychain == right.Keychain &&
               left.PaintKit == right.PaintKit &&
               left.PaintSeed == right.PaintSeed &&
               left.PaintWear == right.PaintWear &&
               left.PaintUsesLegacyModel == right.PaintUsesLegacyModel;

        public int GetHashCode(BotRandomizerWeaponWriteClaim value)
            => HashCode.Combine(
                value.WeaponDefinitionIndex,
                value.Paint,
                value.Stickers,
                value.Keychain,
                value.PaintKit,
                value.PaintSeed,
                value.PaintWear,
                value.PaintUsesLegacyModel);
    }
}
