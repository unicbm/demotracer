using BotRandomizer;
using BotRandomizerApi;

internal static class UnifiedProviderTests
{
    internal static void Run(CosmeticCatalog catalog, CharmPlacementCatalog placements)
    {
        var roller = new CosmeticRoller(catalog, placements, new Random(1700));
        var states = new CosmeticStateStore();
        var options = new RandomizerOptions();
        var bot = states.GetOrCreate(1, 101, 3, music => roller.RollLoadout(3, music));
        var other = states.GetOrCreate(2, 102, 3, music => roller.RollLoadout(3, music));
        var originalRandom = bot.Loadout;
        var originalOther = other.Loadout;
        var pendingSpawn = bot.Generation;
        var leases = new CosmeticWriteLeaseStore("unified", slots =>
        {
            foreach (var slot in slots) states.BumpGeneration(slot);
        });
        using var replayOwner = new CancellationTokenSource();
        using var competingOwner = new CancellationTokenSource();
        var replay = new CosmeticWritePolicy(3, BotRandomizerAgentPlanMode.ReplayModel,
            "agents\\models\\ctm_sas\\ctm_sas_variantf.vmdl", null, null, 70,
            new Dictionary<ushort, ReplayWeaponSelection>()) { AgentItemDefinitionIndex = 5601 };
        var claims = new Dictionary<int, LeasedCosmeticWriteClaim>
        {
            [bot.Slot] = new(bot.Incarnation, 76_561_198_012_345_678, replay)
        };
        Require(leases.TryAcquire("demotracer", claims, replayOwner.Token, out var lease, out _), "acquire replay");
        states.BumpGeneration(bot.Slot);
        Require(!states.IsCurrent(bot.Slot, bot.UserId, pendingSpawn), "transition invalidates an old spawn callback");
        Require(!leases.TryAcquire("another-consumer", claims, competingOwner.Token, out _, out _), "second writer cannot claim replay bot");
        Require(!leases.TryGetPolicy(other.Slot, other.Incarnation, out _, out _), "ordinary bot stays unclaimed");

        foreach (var category in new[] { "weapons", "knives", "gloves", "agents", "music", "stickers", "charms" })
            Require(options.TryApplyControl(RandomizerOptions.ControlKey, category, "off"), "Panel control accepted");
        Require(leases.TryGetPolicy(bot.Slot, bot.Incarnation, out var active, out _), "Panel keeps ownership");
        Require(options.ResolveMusicKit(active, bot.Loadout) == 70 &&
            options.ResolveIntroAgent(active, bot.Loadout) == 5601 &&
            options.ResolveAgentModel(active, bot.Loadout) == replay.AgentModel, "replay survives every Panel switch");
        Require(options.ResolveMusicKit(null, other.Loadout) == 0 &&
            options.ResolveIntroAgent(null, other.Loadout) == 0, "ordinary bot obeys the same Panel switches");
        Require(ReferenceEquals(originalRandom, bot.Loadout) && ReferenceEquals(originalOther, other.Loadout), "switches do not reroll either bot");

        bot = states.Reroll(bot.Slot, bot.UserId, 3, false, music => roller.RollLoadout(3, music))!;
        Require(leases.TryGetPolicy(bot.Slot, bot.Incarnation, out active, out _) &&
            options.ResolveMusicKit(active, bot.Loadout) == 70, "safe reroll preserves replay ownership");
        var beforeCancel = bot.Generation;
        replayOwner.Cancel();
        Require(!leases.TryGetPolicy(bot.Slot, bot.Incarnation, out _, out _) &&
            !states.IsCurrent(bot.Slot, bot.UserId, beforeCancel), "consumer unload cancels ownership and old callbacks");
        Require(options.ResolveMusicKit(null, bot.Loadout) == 0, "release restores current disabled defaults");
        options.TryApplyControl(RandomizerOptions.ControlKey, "music", "on");
        Require(options.ResolveMusicKit(null, bot.Loadout) == bot.Loadout.MusicKit, "random behavior returns without another plugin");

        Require(leases.TryAcquire("another-consumer", claims, competingOwner.Token, out _, out _), "new owner can acquire after release");
        leases.Reset(countRevocation: true);
        var oldGeneration = bot.Generation;
        states.Reset();
        Require(leases.GetCounters().ActiveLeases == 0 && !states.IsCurrent(bot.Slot, bot.UserId, oldGeneration), "map/provider teardown clears ownership and delayed state");
        Require(!leases.TryRelease(lease.Token, out _), "stale token cannot affect a later lifecycle");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"Unified provider test failed: {label}");
    }
}
