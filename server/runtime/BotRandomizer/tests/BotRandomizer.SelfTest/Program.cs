using BotRandomizer;
using BotRandomizerApi;

if (args.Length != 2)
    throw new InvalidOperationException(
        "Pass the absolute paths to cosmetic_catalog.json and cs2-lib-econ-index.v1.json.");

var catalog = CosmeticCatalog.Load(args[0]);
var replayEconIndex = ReplayEconIndex.Load(args[1]);
var catalogDirectory = Path.GetDirectoryName(Path.GetFullPath(args[0]))
    ?? throw new InvalidOperationException("Catalog path has no directory.");
var placementPath = Path.Combine(catalogDirectory, "charm_placements.json");
var charmPlacements = CharmPlacementCatalog.Load(placementPath, catalog);
Assert(catalog.SourceRepository == "ianlucas/cs2-lib", "catalog source");
Assert(catalog.WeaponCount == 35, "weapon count");
Assert(catalog.WeaponPaintCount == 1456, "weapon paint count");
Assert(catalog.KnifePaintCount == 556, "knife paint count");
Assert(catalog.Gloves.Count == 94, "glove count");
Assert(catalog.StickerCategories.Count == 61, "sticker category count");
Assert(catalog.StickerKits.Count == 10565, "sticker count");
Assert(catalog.KeychainDefinitions.Count == 81, "keychain count");
Assert(catalog.MusicKits.Count == 98, "music kit count");
Assert(replayEconIndex.SourceVersion == "8.4.0", "replay econ source version");
Assert(replayEconIndex.WeaponPaintCount == 1456, "replay weapon paint count");
Assert(replayEconIndex.MusicKitCount == 100, "replay music kit count");
Assert(replayEconIndex.IsMusicKit(1) && replayEconIndex.IsMusicKit(70),
    "valid demo music kits excluded from random pools remain replay-valid");
Assert(replayEconIndex.TryGetWeaponPaint(4, 799, out var glockLegacy) && !glockLegacy,
    "Glock replay paint tuple with a fifth CS2 sticker slot");
Assert(replayEconIndex.IsSticker(7887) && replayEconIndex.IsKeychain(37),
    "demo sticker and keychain evidence");
Assert(replayEconIndex.IsKnifeDefinition(506) && replayEconIndex.IsKnifeDefinition(526),
    "valid replay knives excluded from random type weights");
Assert(replayEconIndex.IsGloveDefinition(5030) && replayEconIndex.IsPaintKit(10038),
    "demo glove evidence");
var originalOwnerIdentity = new ReplayEconIdentity(
    Quality: null,
    StattrakCounter: null,
    OriginalOwnerSteamId: 76_561_198_012_345_678UL,
    ItemAccountId: null,
    ItemId: null,
    CustomName: null);
var originalOwner = ReplayOriginalOwner.ResolveSteamId(originalOwnerIdentity, fallbackSteamId: 0);
var (originalOwnerLow, originalOwnerHigh) = ReplayOriginalOwner.SplitSteamId(originalOwner);
Assert(((ulong)originalOwnerHigh << 32 | originalOwnerLow) == originalOwner,
    "replay weapon original-owner XUID roundtrip");
Assert(ReplayOriginalOwner.ResolveSteamId(
        originalOwnerIdentity with { OriginalOwnerSteamId = null, ItemAccountId = 123_456 },
        fallbackSteamId: 0) == 76_561_197_960_389_184UL,
    "replay weapon account ID owner fallback");
Assert(!replayEconIndex.IsMusicKit(2) &&
       !replayEconIndex.TryGetWeaponPaint(4, uint.MaxValue, out _),
    "unknown replay evidence fails closed");
Assert(charmPlacements.WeaponCount == 16, "charm placement weapon count");
Assert(charmPlacements.PlacementCount == 158, "charm placement count");
Assert(charmPlacements.TryGetPlacements(7, out var akPlacements) && akPlacements.Count == 36,
    "AK-47 charm placement pool");
Assert(catalog.SourceLogicalMaps == 269, "pro demo prior coverage");
Assert(catalog.SourceKnifeObservations == 741, "pro knife prior coverage");
Assert(catalog.MatchedKnifeObservations > 0
    && catalog.MatchedKnifeObservations == catalog.SourceKnifeObservations,
    "matched knife observation coverage");
Assert(catalog.SourceProConverterSha256.Length == 64
    && catalog.SourceProConverterSha256.All(Uri.IsHexDigit),
    "pro converter provenance");
Assert(catalog.SourceProCorpusDigest.Length == 64
    && catalog.SourceProCorpusDigest.All(Uri.IsHexDigit),
    "pro corpus provenance");
foreach (var defIndex in new ushort[] { 16, 23, 26, 60, 61 })
{
    Assert(catalog.TryGetWeapon(defIndex, out var weapon) && weapon.Paints.Count > 0,
        $"BotBuy CT replacement weapon {defIndex}");
}
foreach (var (designerName, defIndex) in new (string, ushort)[]
{
    ("weapon_m4a1", 16),
    ("weapon_mp5sd", 23),
    ("weapon_bizon", 26),
    ("weapon_m4a1_silencer", 60),
    ("weapon_usp_silencer", 61)
})
{
    Assert(catalog.TryGetWeapon(designerName, out var weapon) && weapon.DefIndex == defIndex,
        $"GiveNamedItem mapping {designerName}");
}
Assert(catalog.Weapons.All(weapon => weapon.DesignerName.StartsWith("weapon_", StringComparison.Ordinal)),
    "weapon designer names");
Assert(catalog.TryGetWeapon((ushort)60, out var m4a1s)
    && m4a1s.Paints.Any(paint => paint.PaintKit == 106),
    "M4A1-S replay paint tuple is accepted by the provider catalog");
Assert(catalog.TryGetWeapon((ushort)61, out var usps)
    && usps.Paints.Any(paint => paint.PaintKit == 60),
    "USP-S replay paint tuple is accepted by the provider catalog");
Assert(RandomizerAssets.TryNormalizeAgentModel(
        RandomizerAssets.CounterTerroristTeam,
        "AGENTS/MODELS/CTM_SAS/CTM_SAS_VARIANTF.VMDL",
        out var normalizedAgentModel)
    && normalizedAgentModel == "agents\\models\\ctm_sas\\ctm_sas_variantf.vmdl",
    "agent model separators and casing normalize to engine path");
Assert(!RandomizerAssets.TryNormalizeAgentModel(
        RandomizerAssets.TerroristTeam,
        normalizedAgentModel,
        out _),
    "agent model must match bot team");

var gloveWeights = catalog.Gloves
    .GroupBy(glove => glove.DefIndex)
    .ToDictionary(
        group => group.Key,
        group => group.Sum(glove => RandomizerAssets.GetGloveVariantWeight(glove.DefIndex)));
var gloveWeightTotal = gloveWeights.Values.Sum();
Assert(gloveWeightTotal == 245, "glove weight total");
Assert(gloveWeights[5030] == 76, "Sport Gloves weight");
Assert(gloveWeights[5034] == 57, "Specialist Gloves weight");

var gloveRoller = new CosmeticRoller(catalog, charmPlacements, new Random(20260722));
var gloveCounts = new Dictionary<ushort, int>();
var knifeTypeCounts = new Dictionary<ushort, int>();
var knifeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
var knifePaintLookup = new Dictionary<(ushort DefIndex, int PaintKit), string>();
var expectedKnifeShares = new Dictionary<string, double>(StringComparer.Ordinal);
var knifeTypeWeightTotal = RandomizerAssets.Knives.Sum(knife => knife.Weight);
Assert(knifeTypeWeightTotal == 100, "knife type weight total");
var dominantKnifeDefinitions = new HashSet<ushort> { 500, 507, 508, 515 };
Assert(RandomizerAssets.Knives
        .Where(knife => dominantKnifeDefinitions.Contains(knife.DefIndex))
        .Sum(knife => knife.Weight) == 70,
    "dominant knife group share");
Assert(RandomizerAssets.Knives.Single(knife => knife.DefIndex == 503).Weight == 3,
    "Classic Knife maintainer preference");
foreach (var knifeDefinition in RandomizerAssets.Knives)
{
    Assert(catalog.TryGetKnifePaints(knifeDefinition.DefIndex, out var knifePaints),
        $"knife {knifeDefinition.DefIndex} catalog");
    foreach (var knifePaint in knifePaints)
        knifePaintLookup[(knifeDefinition.DefIndex, knifePaint.PaintKit)] = knifePaint.Finish;

    var finishWeights = knifePaints
        .Select(paint => paint.Finish)
        .Distinct(StringComparer.Ordinal)
        .ToDictionary(
            finish => finish,
            finish => catalog.GetKnifeFinishWeight(finish),
            StringComparer.Ordinal);
    var finishWeightTotal = finishWeights.Values.Sum();
    foreach (var (finish, weight) in finishWeights)
    {
        expectedKnifeShares[finish] = expectedKnifeShares.GetValueOrDefault(finish)
            + weight / (double)finishWeightTotal
            * knifeDefinition.Weight / knifeTypeWeightTotal;
    }
}
const int gloveTrials = 100000;
for (var iteration = 0; iteration < gloveTrials; iteration++)
{
    var loadout = gloveRoller.RollLoadout(RandomizerAssets.TerroristTeam);
    var glove = loadout.Glove;
    gloveCounts[glove.DefIndex] = gloveCounts.GetValueOrDefault(glove.DefIndex) + 1;
    knifeTypeCounts[loadout.Knife.DefIndex]
        = knifeTypeCounts.GetValueOrDefault(loadout.Knife.DefIndex) + 1;
    var knifeFinish = knifePaintLookup[(loadout.Knife.DefIndex, loadout.Knife.PaintKit)];
    knifeCounts[knifeFinish] = knifeCounts.GetValueOrDefault(knifeFinish) + 1;
}
foreach (var (defIndex, weight) in gloveWeights)
{
    var expected = weight / (double)gloveWeightTotal;
    var observed = gloveCounts.GetValueOrDefault(defIndex) / (double)gloveTrials;
    Assert(Math.Abs(observed - expected) < 0.01,
        $"glove family {defIndex} weighted distribution");
}
foreach (var knifeDefinition in RandomizerAssets.Knives)
{
    var expected = knifeDefinition.Weight / (double)knifeTypeWeightTotal;
    var observed = knifeTypeCounts.GetValueOrDefault(knifeDefinition.DefIndex)
        / (double)gloveTrials;
    Assert(Math.Abs(observed - expected) < 0.01,
        $"knife type {knifeDefinition.DefIndex} weighted distribution");
}
foreach (var (finish, expected) in expectedKnifeShares)
{
    var observed = knifeCounts.GetValueOrDefault(finish) / (double)gloveTrials;
    Assert(Math.Abs(observed - expected) < 0.01, $"knife finish {finish} distribution");
}
Assert(BitConverter.SingleToInt32Bits(AttributeEncoding.UInt32BitsToSingle(0xDEADBEEF))
    == unchecked((int)0xDEADBEEF), "uint attribute bit encoding");
Assert(BitConverter.SingleToInt32Bits(AttributeEncoding.Int32BitsToSingle(-1234567))
    == -1234567, "int attribute bit encoding");
var itemIds = Enumerable.Range(0, 32).Select(_ => EconItemIdAllocator.Next()).ToArray();
Assert(itemIds.Distinct().Count() == itemIds.Length, "custom item IDs are process-unique");
Assert(itemIds.All(EconItemIdAllocator.IsAllocated)
    && !EconItemIdAllocator.IsAllocated(0),
    "custom item IDs identify prebuilt economic views");

var wearAllocator = new WeaponWearAllocator();
var paint = new PaintCatalogEntry(7, CosmeticRarity.Restricted, false, 0.0f, 1.0f);
var firstStickers = new[] { new StickerSelection(1, 0, 0) };
var secondStickers = new[] { new StickerSelection(2, 0, 0) };
var firstWear = wearAllocator.Reserve(7, paint, firstStickers);
var repeatedWear = wearAllocator.Reserve(7, paint, firstStickers);
var secondWear = wearAllocator.Reserve(7, paint, secondStickers);
Assert(firstWear == repeatedWear, "identical sticker signatures reuse wear");
Assert(firstWear != secondWear, "different sticker signatures reserve unique wear");
var roller = new CosmeticRoller(catalog, charmPlacements, new Random(1979));
var allWeaponsLoadout = roller.RollLoadout(RandomizerAssets.TerroristTeam);
foreach (var weaponEntry in catalog.Weapons)
{
    Assert(roller.GetOrCreateWeapon(allWeaponsLoadout, weaponEntry.DefIndex) is not null,
        $"weapon {weaponEntry.DefIndex} roll");
}

var sawStickers = false;
var sawKeychain = false;
var sawStickerSlab = false;
for (var iteration = 0; iteration < 2000; iteration++)
{
    var loadout = roller.RollLoadout(RandomizerAssets.TerroristTeam);
    var weapon = roller.GetOrCreateWeapon(loadout, 7)
        ?? throw new InvalidOperationException("AK-47 roll missing.");
    var weaponCatalog = catalog.TryGetWeapon(7, out var entry)
        ? entry
        : throw new InvalidOperationException("AK-47 catalog missing.");
    var schemaCount = weapon.Legacy
        ? weaponCatalog.LegacyStickerSchemaCount
        : weaponCatalog.StickerSchemaCount;

    Assert(weapon.Stickers.Count <= schemaCount, "sticker stack respects weapon schema");
    Assert(weapon.Stickers.Select(sticker => sticker.Schema).Distinct().Count()
        == weapon.Stickers.Count, "sticker schemas do not overlap");
    for (var slot = 0; slot < weapon.Stickers.Count; slot++)
    {
        var sticker = weapon.Stickers[slot];
        Assert(sticker.Slot == slot, "contiguous sticker slots");
        Assert(sticker.Schema < schemaCount, "sticker schema range");
        Assert(catalog.StickerKits.Any(entry => entry.DefIndex == sticker.DefIndex),
            "sticker definition catalog membership");
        sawStickers = true;
    }

    if (weapon.Keychain is { } keychain)
    {
        Assert(keychain.Seed is >= 1 and <= 100000, "keychain seed range");
        Assert(catalog.KeychainDefinitions.Contains(keychain.DefIndex), "keychain catalog membership");
        Assert(keychain.DefIndex == 37 ? keychain.Sticker is not null : keychain.Sticker is null,
            "Sticker Slab payload");
        Assert(keychain.X is float x
            && keychain.Y is float y
            && keychain.Z is float z
            && akPlacements.Contains(new CharmPlacement(x, y, z)),
            "weapon-specific charm placement");
        sawKeychain = true;
        sawStickerSlab |= keychain.DefIndex == 37;
    }
}
Assert(sawStickers, "sticker rolling exercised");
Assert(sawKeychain, "keychain rolling exercised");
Assert(sawStickerSlab, "Sticker Slab rolling exercised");

var distributionRoller = new CosmeticRoller(catalog, charmPlacements, new Random(20260723));
var akCatalog = catalog.TryGetWeapon(7, out var akEntry)
    ? akEntry
    : throw new InvalidOperationException("AK-47 catalog missing.");
var akPaintsByIndex = akCatalog.Paints.ToDictionary(paintEntry => paintEntry.PaintKit);
var stickerCatalogByIndex = catalog.StickerKits.ToDictionary(sticker => sticker.DefIndex);
var akRarityCounts = new Dictionary<CosmeticRarity, int>();
var stickerCountCounts = new Dictionary<int, int>();
var repeatCraftCounts = new Dictionary<int, int>();
var fourRepeatFinishCounts = new Dictionary<StickerFinish, int>();
var fourMixedFinishCounts = new Dictionary<StickerFinish, int>();
const int distributionTrials = 100000;
for (var iteration = 0; iteration < distributionTrials; iteration++)
{
    var loadout = distributionRoller.RollLoadout(RandomizerAssets.TerroristTeam);
    var weapon = distributionRoller.GetOrCreateWeapon(loadout, 7)
        ?? throw new InvalidOperationException("AK-47 distribution roll missing.");
    var rarity = akPaintsByIndex[weapon.PaintKit].Rarity;
    akRarityCounts[rarity] = akRarityCounts.GetValueOrDefault(rarity) + 1;
    stickerCountCounts[weapon.Stickers.Count]
        = stickerCountCounts.GetValueOrDefault(weapon.Stickers.Count) + 1;

    var stickerEntries = weapon.Stickers
        .Select(sticker => stickerCatalogByIndex[sticker.DefIndex])
        .ToArray();
    Assert(stickerEntries.Select(sticker => sticker.Category).Distinct().Count() <= 1,
        "sticker craft category coherence");
    var repeated = weapon.Stickers.Count > 0
        && weapon.Stickers.Select(sticker => sticker.DefIndex).Distinct().Count() == 1;
    if (weapon.Stickers.Count >= 2 && repeated)
    {
        repeatCraftCounts[weapon.Stickers.Count]
            = repeatCraftCounts.GetValueOrDefault(weapon.Stickers.Count) + 1;
    }
    if (weapon.Stickers.Count == 4)
    {
        Assert(stickerEntries.Select(sticker => sticker.Finish).Distinct().Count() == 1,
            "four-sticker craft uses one finish theme");
        var finishCounts = repeated ? fourRepeatFinishCounts : fourMixedFinishCounts;
        var finish = stickerEntries[0].Finish;
        finishCounts[finish] = finishCounts.GetValueOrDefault(finish) + 1;
    }
    distributionRoller.ResetMap();
}

var akRarityWeights = akCatalog.Paints
    .Select(paintEntry => paintEntry.Rarity)
    .Distinct()
    .ToDictionary(
        rarity => rarity,
        rarity => RandomizerAssets.GetWeaponRarityWeight(rarity));
var akRarityWeightTotal = akRarityWeights.Values.Sum();
foreach (var (rarity, weight) in akRarityWeights)
{
    var expected = weight / (double)akRarityWeightTotal;
    var observed = akRarityCounts.GetValueOrDefault(rarity) / (double)distributionTrials;
    Assert(Math.Abs(observed - expected) < 0.01, $"AK-47 {rarity} distribution");
}
var akCovertShare = akRarityCounts[CosmeticRarity.Covert] / (double)distributionTrials;
Assert(akCovertShare is >= 0.39 and <= 0.42,
    "AK-47 Covert share stays near 40%");

var expectedStickerCounts = new Dictionary<int, double>
{
    [0] = 0.35,
    [1] = 0.12,
    [2] = 0.08,
    [3] = 0.08,
    [4] = 0.37
};
foreach (var (count, expected) in expectedStickerCounts)
{
    var observed = stickerCountCounts.GetValueOrDefault(count) / (double)distributionTrials;
    Assert(Math.Abs(observed - expected) < 0.01, $"{count}-sticker craft distribution");
}
var expectedRepeatShares = new Dictionary<int, double>
{
    [2] = 0.25,
    [3] = 0.28,
    [4] = 0.41
};
foreach (var (count, expected) in expectedRepeatShares)
{
    var observed = repeatCraftCounts.GetValueOrDefault(count)
        / (double)stickerCountCounts[count];
    Assert(Math.Abs(observed - expected) < 0.02,
        $"{count}-sticker repeat probability");
}
Assert(RandomizerAssets.GetFourRepeatStickerFinishWeight(StickerFinish.Holo)
    > RandomizerAssets.GetFourRepeatStickerFinishWeight(StickerFinish.Paper),
    "repeated four-sticker crafts favor Holo");
Assert(RandomizerAssets.GetFourMixedStickerFinishWeight(StickerFinish.Gold)
    > RandomizerAssets.GetFourMixedStickerFinishWeight(StickerFinish.Paper),
    "mixed four-sticker crafts favor Gold");
Assert(fourRepeatFinishCounts.GetValueOrDefault(StickerFinish.Holo) > 0
    && fourRepeatFinishCounts.GetValueOrDefault(StickerFinish.Gold) > 0
    && fourMixedFinishCounts.GetValueOrDefault(StickerFinish.Holo) > 0
    && fourMixedFinishCounts.GetValueOrDefault(StickerFinish.Gold) > 0,
    "Holo and Gold four-sticker themes are reachable");

var charmRoller = new CosmeticRoller(catalog, charmPlacements, new Random(20260720));
var charmCount = 0;
const int charmTrials = 10000;
for (var iteration = 0; iteration < charmTrials; iteration++)
{
    var loadout = charmRoller.RollLoadout(RandomizerAssets.TerroristTeam);
    var weapon = charmRoller.GetOrCreateWeapon(loadout, 7)
        ?? throw new InvalidOperationException("AK-47 probability roll missing.");
    if (weapon.Keychain is not null)
        charmCount++;
}
Assert(charmCount is >= 6800 and <= 7200, "70% keychain probability");

var defaultPlacementRoller = new CosmeticRoller(catalog, charmPlacements, new Random(20260721));
var sawDefaultPlacementCharm = false;
for (var iteration = 0; iteration < 100; iteration++)
{
    var loadout = defaultPlacementRoller.RollLoadout(RandomizerAssets.CounterTerroristTeam);
    var weapon = defaultPlacementRoller.GetOrCreateWeapon(loadout, 23)
        ?? throw new InvalidOperationException("MP5-SD default placement roll missing.");
    if (weapon.Keychain is not { } keychain)
        continue;

    Assert(keychain.X is null && keychain.Y is null && keychain.Z is null,
        "unobserved weapon preserves CS2 default charm placement");
    sawDefaultPlacementCharm = true;
    break;
}
Assert(sawDefaultPlacementCharm, "unobserved weapon charm rolling exercised");

var stateStore = new CosmeticStateStore();
var firstState = stateStore.GetOrCreate(
    slot: 5,
    userId: 101,
    RandomizerAssets.TerroristTeam,
    music => roller.RollLoadout(RandomizerAssets.TerroristTeam, music));
var firstIncarnation = firstState.Incarnation;
var rerolledState = stateStore.Reroll(
    slot: 5,
    userId: 101,
    RandomizerAssets.TerroristTeam,
    preserveMusic: false,
    music => roller.RollLoadout(RandomizerAssets.TerroristTeam, music));
Assert(rerolledState?.Incarnation == firstIncarnation,
    "reroll preserves managed bot incarnation");
stateStore.Remove(5);
var reusedSlotState = stateStore.GetOrCreate(
    slot: 5,
    userId: 202,
    RandomizerAssets.CounterTerroristTeam,
    music => roller.RollLoadout(RandomizerAssets.CounterTerroristTeam, music));
Assert(reusedSlotState.Incarnation != firstIncarnation,
    "slot reuse receives a new managed bot incarnation");

long leaseClock = 1_000;
var leaseStore = new CosmeticWriteLeaseStore("self-test", () => leaseClock);
var replayIdentity = new ReplayEconIdentity(9, 88, null, null, 1234, "demo");
var demoTracerPolicy = new CosmeticWritePolicy(
    BotRandomizerReplayTeamPolicy.Terrorist,
    BotRandomizerAgentPlanMode.PreserveEngineDefault,
    agentModel: null,
    knife: new ReplayItemSelection(507, 38, 12, 0.04f, replayIdentity),
    gloves: null,
    musicKit: null,
    new Dictionary<ushort, ReplayWeaponSelection>
    {
        [7] = new(7, 180, 321, 0.15f, false,
            [new StickerSelection(661, 0, 0)],
            [],
            replayIdentity)
    });
var demoTracerClaims = new Dictionary<int, LeasedCosmeticWriteClaim>
{
    [1] = new(11, 76_561_198_000_000_001UL, demoTracerPolicy)
};
Assert(leaseStore.TryAcquire(
        "demotracer",
        demoTracerClaims,
        out var demoTracerLease,
        out var leaseReason)
    && leaseReason.Length == 0,
    "evidence writer lease acquisition");
Assert(leaseStore.TryGetPolicy(1, 11, out var activePolicy, out var activeOwner)
    && activeOwner == "demotracer"
    && activePolicy.SpawnTeam == BotRandomizerReplayTeamPolicy.Terrorist
    && activePolicy.AgentMode == BotRandomizerAgentPlanMode.PreserveEngineDefault
    && activePolicy.Knife?.DefIndex == 507
    && activePolicy.Gloves is null
    && activePolicy.TryGetWeapon(7, out var akPolicy)
    && akPolicy.PaintKit == 180
    && akPolicy.Seed == 321
    && akPolicy.Stickers.Count == 1
    && akPolicy.Keychains.Count == 0,
    "complete replay desired state is retained");
Assert(!leaseStore.TryGetPolicy(1, 12, out _, out _),
    "stale pawn incarnation cannot use lease");
Assert(!leaseStore.TryAcquire(
        "other-writer",
        demoTracerClaims,
        out _,
        out leaseReason)
    && leaseReason == "slot_leased:1",
    "lease rejects competing writer");

var replacementPolicy = new CosmeticWritePolicy(
    BotRandomizerReplayTeamPolicy.CounterTerrorist,
    BotRandomizerAgentPlanMode.ReplayModel,
    "agents\\models\\ctm_sas\\ctm_sas_variantf.vmdl",
    knife: null,
    gloves: null,
    musicKit: 3,
    new Dictionary<ushort, ReplayWeaponSelection>());
var replacementClaims = new Dictionary<int, LeasedCosmeticWriteClaim>
{
    [2] = new(22, null, replacementPolicy)
};
Assert(leaseStore.TryReplace(
        demoTracerLease.Token,
        replacementClaims,
        out var replacementLease,
        out var replacedSlots,
        out leaseReason)
    && replacedSlots.SequenceEqual(new[] { 1, 2 })
    && replacementLease.Token == demoTracerLease.Token,
    "lease replacement is atomic across slots");
Assert(!leaseStore.TryGetPolicy(1, 11, out _, out _)
    && leaseStore.TryGetPolicy(2, 22, out var replacementActive, out _)
    && replacementActive.SpawnTeam == BotRandomizerReplayTeamPolicy.CounterTerrorist
    && replacementActive.AgentMode == BotRandomizerAgentPlanMode.ReplayModel
    && replacementActive.Knife is null
    && replacementActive.MusicKit == 3,
    "lease replacement releases old claims and installs new claims");

leaseClock += 3_999;
Assert(leaseStore.Heartbeat(replacementLease.Token), "lease heartbeat before timeout");
leaseClock += 4_001;
Assert(leaseStore.SweepExpired().SequenceEqual(new[] { 2 })
    && !leaseStore.TryGetPolicy(2, 22, out _, out _),
    "expired lease restores randomizer ownership");
var leaseCounters = leaseStore.GetCounters();
Assert(leaseCounters.ActiveLeases == 0
    && leaseCounters.AcquiredLeases == 1
    && leaseCounters.ReplacedLeases == 1
    && leaseCounters.ExpiredLeases == 1
    && leaseCounters.RejectedRequests == 1,
    "lease diagnostics counters");

Console.WriteLine("BotRandomizer self-test passed.");

static void Assert(bool condition, string label)
{
    if (!condition)
        throw new InvalidOperationException($"Self-test failed: {label}");
}
