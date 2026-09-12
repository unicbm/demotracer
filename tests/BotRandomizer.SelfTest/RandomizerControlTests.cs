using BotRandomizer;
using BotRandomizerApi;

internal static class RandomizerControlTests
{
    internal static void Run()
    {
        var options = new RandomizerOptions();
        string[] categories = ["weapons", "knives", "gloves", "agents", "music", "stickers", "charms"];
        foreach (var category in categories)
        {
            foreach (var value in new[] { "1", "on", "YES", " true " })
            {
                Require(options.TryApplyControl(RandomizerOptions.ControlKey, category.ToUpperInvariant(), value),
                    $"Panel accepts {category}={value}");
                Require(Read(options, category), $"{category} enabled");
            }
            foreach (var value in new[] { "0", "off", "NO", " false " })
            {
                Require(options.TryApplyControl(RandomizerOptions.ControlKey, category, value),
                    $"Panel accepts {category}={value}");
                Require(!Read(options, category), $"{category} disabled");
            }
        }
        Require(!options.HasWeaponCosmetics, "all gun cosmetics disabled");
        Require(!options.TryApplyControl("wrong key", "weapons", "1") && !options.Weapons,
            "wrong command key cannot change options");
        Require(!options.TryApplyControl(RandomizerOptions.ControlKey, "weapons", "maybe") && !options.Weapons,
            "invalid boolean cannot change options");
        Require(!options.TryApplyControl(RandomizerOptions.ControlKey, "unknown", "1"),
            "unknown category rejected");

        var random = new BotCosmeticLoadout
        {
            Team = 3,
            AgentModel = "agents\\models\\ctm_sas\\ctm_sas_variantf.vmdl",
            MusicKit = 3,
            Knife = new KnifeSelection(507, 38, 0.1f),
            Glove = new GloveSelection(5030, 10038, 0.1f)
        };
        var preserveAgent = new CosmeticWritePolicy(3, BotRandomizerAgentPlanMode.PreserveEngineDefault,
            null, null, null, null, new Dictionary<ushort, ReplayWeaponSelection>());
        var evidence = new CosmeticWritePolicy(3, BotRandomizerAgentPlanMode.ReplayModel,
            random.AgentModel, null, null, 70, new Dictionary<ushort, ReplayWeaponSelection>())
            { AgentItemDefinitionIndex = 5601 };
        Require(options.ResolveIntroAgent(null, random) == 0, "agents off clears intro item");
        Require(options.ResolveIntroAgent(evidence, random) == 5601, "intro keeps DTR item evidence");
        Require(options.ResolveMusicKit(null, random) == 0, "music off clears a previous random kit");
        Require(options.ResolveMusicKit(evidence, random) == 70, "music off cannot suppress DTR evidence");
        Require(options.ResolveAgentModel(null, random) == "agents\\models\\ctm_sas\\ctm_sas.vmdl",
            "agents off restores CT default");
        random = new BotCosmeticLoadout
        {
            Team = 2, AgentModel = random.AgentModel, MusicKit = random.MusicKit,
            Knife = random.Knife, Glove = random.Glove
        };
        Require(options.ResolveAgentModel(null, random) == "agents\\models\\tm_phoenix\\tm_phoenix.vmdl",
            "agents off restores T default");
        Require(options.ResolveAgentModel(evidence, random) == evidence.AgentModel,
            "agents off cannot suppress DTR evidence");
        options.TryApplyControl(RandomizerOptions.ControlKey, "agents", "1");
        Require(options.ResolveAgentModel(preserveAgent, random) is null,
            "DTR preserve-engine-default prevents randomized agent writes");
        Require(options.ResolveAgentModel(null, random) == random.AgentModel,
            "unclaimed agent returns to configured randomization");
        Require(options.ResolveIntroAgent(null, random) == 5601, "intro matches randomized live model");
        Require(options.ResolveIntroAgent(preserveAgent, random) is null,
            "intro also preserves engine default for missing DTR evidence");

        Require(IntroAgentAssignment.Match([new(0, true), new(0, true)], [0, 0])
                .SequenceEqual(new[] { (0, 0), (1, 1) }), "ordinary bot intro ordering");
        Require(IntroAgentAssignment.Match([new(100, false), new(0, true), new(200, true)], [200, 100, 0])
                .SequenceEqual(new[] { (0, 2), (2, 1) }),
            "spoofed bots match XUID before anonymous bots, and human preview is untouched");
        Require(IntroAgentAssignment.Match([new(0, false), new(0, true)], [0, 0]).Count == 0,
            "pending human XUID cannot be mistaken for a bot preview");
        Require(IntroAgentAssignment.Match([new(200, true)], [999]).Count == 0,
            "unknown nonzero preview identity is never overwritten");
        Require(IntroAgentAssignment.Match([new(200, true), new(200, true)], [200]).Count == 0,
            "duplicate player identities cannot select an arbitrary bot");
        Require(IntroAgentAssignment.Match([new(200, true)], [200, 200]).Count == 0,
            "duplicate preview identities cannot duplicate an assignment");

        var weapon = new WeaponCatalogEntry("weapon_ak47", 7, 5, 4, []);
        var selection = new WeaponCosmeticSelection(180, 123, 0.1f, true,
            [new StickerSelection(661, 0, 0), new StickerSelection(661, 4, 4)],
            new KeychainSelection(37, 42));
        Require(options.ResolveWeapon(weapon, selection) is null,
            "all gun options off leaves GiveNamedItem untouched");
        options.TryApplyControl(RandomizerOptions.ControlKey, "stickers", "1");
        var stickersOnly = options.ResolveWeapon(weapon, selection)!;
        Require(stickersOnly.PaintKit == 0 && stickersOnly.Seed == 0 && stickersOnly.Wear == 0 &&
                !stickersOnly.Legacy && stickersOnly.Stickers.Count == 2 && stickersOnly.Keychain is null,
            "stickers without paint use default model schemas and no hidden paint or charm");
        options.TryApplyControl(RandomizerOptions.ControlKey, "weapons", "1");
        options.TryApplyControl(RandomizerOptions.ControlKey, "charms", "1");
        var all = options.ResolveWeapon(weapon, selection)!;
        Require(all.PaintKit == 180 && all.Seed == 123 && all.Wear == 0.1f && all.Legacy &&
                all.Stickers.Count == 1 && all.Keychain == selection.Keychain,
            "reenabling paint restores its legacy schemas and chosen charm");
        Require(selection.Stickers.Count == 2 && selection.PaintKit == 180,
            "switches never mutate the cached random loadout");
        options.TryApplyControl(RandomizerOptions.ControlKey, "stickers", "0");
        Require(options.ResolveWeapon(weapon, selection)!.Stickers.Count == 0,
            "stickers can be independently removed from later constructed items");
    }

    private static bool Read(RandomizerOptions options, string category) => category switch
    {
        "weapons" => options.Weapons, "knives" => options.Knives, "gloves" => options.Gloves,
        "agents" => options.Agents, "music" => options.Music, "stickers" => options.Stickers,
        "charms" => options.Charms, _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Randomizer control test failed: {message}");
    }
}
