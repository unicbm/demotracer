namespace BotRandomizer;

internal static class RandomizerAssets
{
    internal const byte TerroristTeam = 2;
    internal const byte CounterTerroristTeam = 3;

    internal static bool TryNormalizeAgentModel(
        byte team,
        string? value,
        out string model)
    {
        model = value?.Trim().Replace('/', '\\').ToLowerInvariant() ?? string.Empty;
        var models = team == CounterTerroristTeam
            ? CounterTerroristModels
            : team == TerroristTeam
                ? TerroristModels
                : [];
        return models.Contains(model, StringComparer.Ordinal);
    }

    internal static readonly KnifeDefinition[] Knives =
    [
        // The four dominant types hold 70% together.
        new(515, 25), // Butterfly
        new(507, 21), // Karambit
        new(508, 17), // M9 Bayonet
        new(500, 7),  // Bayonet

        // The remaining 30% favors types used by at least seven distinct
        // owners. Classic Knife is an explicit maintainer preference.
        new(525, 7), // Skeleton
        new(522, 6), // Stiletto
        new(523, 5), // Talon
        new(505, 4), // Flip
        new(509, 3), // Huntsman
        new(503, 3), // Classic
        new(519, 2)  // Ursus
    ];

    // Relative per-finish weights. This gently favors the two dominant pro-demo
    // families without reproducing the corpus's extreme 55% Sport Gloves share.
    internal static int GetGloveVariantWeight(ushort defIndex)
        => defIndex switch
        {
            5030 => 4, // Sport Gloves: 2x baseline
            5034 => 3, // Specialist Gloves: 1.5x baseline
            _ => 2
        };

    internal static int GetWeaponRarityWeight(CosmeticRarity rarity)
        => rarity switch
        {
            CosmeticRarity.Consumer => 1,
            CosmeticRarity.Industrial => 3,
            CosmeticRarity.MilSpec => 10,
            CosmeticRarity.Restricted => 22,
            CosmeticRarity.Classified => 23,
            CosmeticRarity.Covert => 40,
            CosmeticRarity.Contraband => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(rarity))
        };

    internal static int GetStickerFinishWeight(StickerFinish finish)
        => finish switch
        {
            StickerFinish.Paper => 43,
            StickerFinish.Glitter => 8,
            StickerFinish.Holo => 28,
            StickerFinish.Foil => 9,
            StickerFinish.Gold => 11,
            StickerFinish.Lenticular => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(finish))
        };

    // Four-sticker crafts in the demo corpus were frequently deliberate
    // four-of-a-kind Holo/Gold arrangements rather than arbitrary mixes.
    internal static int GetFourRepeatStickerFinishWeight(StickerFinish finish)
        => finish switch
        {
            StickerFinish.Paper => 31,
            StickerFinish.Glitter => 11,
            StickerFinish.Holo => 40,
            StickerFinish.Foil => 7,
            StickerFinish.Gold => 11,
            StickerFinish.Lenticular => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(finish))
        };

    internal static int GetFourMixedStickerFinishWeight(StickerFinish finish)
        => finish switch
        {
            StickerFinish.Paper => 31,
            StickerFinish.Glitter => 5,
            StickerFinish.Holo => 22,
            StickerFinish.Foil => 8,
            StickerFinish.Gold => 34,
            StickerFinish.Lenticular => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(finish))
        };

    internal static readonly IReadOnlyDictionary<string, ushort> KnifeDefIndexByName =
        new Dictionary<string, ushort>
        {
            ["weapon_bayonet"] = 500,
            ["weapon_knife_css"] = 503,
            ["weapon_knife_flip"] = 505,
            ["weapon_knife_gut"] = 506,
            ["weapon_knife_karambit"] = 507,
            ["weapon_knife_m9_bayonet"] = 508,
            ["weapon_knife_tactical"] = 509,
            ["weapon_knife_falchion"] = 512,
            ["weapon_knife_survival_bowie"] = 514,
            ["weapon_knife_butterfly"] = 515,
            ["weapon_knife_push"] = 516,
            ["weapon_knife_cord"] = 517,
            ["weapon_knife_canis"] = 518,
            ["weapon_knife_ursus"] = 519,
            ["weapon_knife_gypsy_jackknife"] = 520,
            ["weapon_knife_outdoor"] = 521,
            ["weapon_knife_stiletto"] = 522,
            ["weapon_knife_widowmaker"] = 523,
            ["weapon_knife_skeleton"] = 525,
            ["weapon_knife_kukri"] = 526
        };

    internal static readonly string[] CounterTerroristModels =
    [
        "agents\\models\\ctm_diver\\ctm_diver_varianta.vmdl",
        "agents\\models\\ctm_diver\\ctm_diver_variantb.vmdl",
        "agents\\models\\ctm_diver\\ctm_diver_variantc.vmdl",
        "agents\\models\\ctm_fbi\\ctm_fbi.vmdl",
        "agents\\models\\ctm_fbi\\ctm_fbi_varianta.vmdl",
        "agents\\models\\ctm_fbi\\ctm_fbi_variantb.vmdl",
        "agents\\models\\ctm_fbi\\ctm_fbi_variantc.vmdl",
        "agents\\models\\ctm_fbi\\ctm_fbi_variantd.vmdl",
        "agents\\models\\ctm_fbi\\ctm_fbi_variante.vmdl",
        "agents\\models\\ctm_fbi\\ctm_fbi_variantf.vmdl",
        "agents\\models\\ctm_fbi\\ctm_fbi_variantg.vmdl",
        "agents\\models\\ctm_fbi\\ctm_fbi_varianth.vmdl",
        "agents\\models\\ctm_gendarmerie\\ctm_gendarmerie_varianta.vmdl",
        "agents\\models\\ctm_gendarmerie\\ctm_gendarmerie_variantb.vmdl",
        "agents\\models\\ctm_gendarmerie\\ctm_gendarmerie_variantc.vmdl",
        "agents\\models\\ctm_gendarmerie\\ctm_gendarmerie_variantd.vmdl",
        "agents\\models\\ctm_gendarmerie\\ctm_gendarmerie_variante.vmdl",
        "agents\\models\\ctm_sas\\ctm_sas.vmdl",
        "agents\\models\\ctm_sas\\ctm_sas_variantf.vmdl",
        "agents\\models\\ctm_sas\\ctm_sas_variantg.vmdl",
        "agents\\models\\ctm_st6\\ctm_st6_variante.vmdl",
        "agents\\models\\ctm_st6\\ctm_st6_variantg.vmdl",
        "agents\\models\\ctm_st6\\ctm_st6_varianti.vmdl",
        "agents\\models\\ctm_st6\\ctm_st6_variantj.vmdl",
        "agents\\models\\ctm_st6\\ctm_st6_variantk.vmdl",
        "agents\\models\\ctm_st6\\ctm_st6_variantl.vmdl",
        "agents\\models\\ctm_st6\\ctm_st6_variantm.vmdl",
        "agents\\models\\ctm_st6\\ctm_st6_variantn.vmdl",
        "agents\\models\\ctm_swat\\ctm_swat_variante.vmdl",
        "agents\\models\\ctm_swat\\ctm_swat_variantf.vmdl",
        "agents\\models\\ctm_swat\\ctm_swat_variantg.vmdl",
        "agents\\models\\ctm_swat\\ctm_swat_varianth.vmdl",
        "agents\\models\\ctm_swat\\ctm_swat_varianti.vmdl",
        "agents\\models\\ctm_swat\\ctm_swat_variantj.vmdl",
        "agents\\models\\ctm_swat\\ctm_swat_variantk.vmdl"
    ];

    internal static readonly string[] TerroristModels =
    [
        "agents\\models\\tm_balkan\\tm_balkan_variantf.vmdl",
        "agents\\models\\tm_balkan\\tm_balkan_variantg.vmdl",
        "agents\\models\\tm_balkan\\tm_balkan_varianth.vmdl",
        "agents\\models\\tm_balkan\\tm_balkan_varianti.vmdl",
        "agents\\models\\tm_balkan\\tm_balkan_variantj.vmdl",
        "agents\\models\\tm_balkan\\tm_balkan_variantk.vmdl",
        "agents\\models\\tm_balkan\\tm_balkan_variantl.vmdl",
        "agents\\models\\tm_jungle_raider\\tm_jungle_raider_varianta.vmdl",
        "agents\\models\\tm_jungle_raider\\tm_jungle_raider_variantb.vmdl",
        "agents\\models\\tm_jungle_raider\\tm_jungle_raider_variantb2.vmdl",
        "agents\\models\\tm_jungle_raider\\tm_jungle_raider_variantc.vmdl",
        "agents\\models\\tm_jungle_raider\\tm_jungle_raider_variantd.vmdl",
        "agents\\models\\tm_jungle_raider\\tm_jungle_raider_variante.vmdl",
        "agents\\models\\tm_jungle_raider\\tm_jungle_raider_variantf.vmdl",
        "agents\\models\\tm_jungle_raider\\tm_jungle_raider_variantf2.vmdl",
        "agents\\models\\tm_leet\\tm_leet_varianta.vmdl",
        "agents\\models\\tm_leet\\tm_leet_variantb.vmdl",
        "agents\\models\\tm_leet\\tm_leet_variantc.vmdl",
        "agents\\models\\tm_leet\\tm_leet_variantd.vmdl",
        "agents\\models\\tm_leet\\tm_leet_variante.vmdl",
        "agents\\models\\tm_leet\\tm_leet_variantf.vmdl",
        "agents\\models\\tm_leet\\tm_leet_variantg.vmdl",
        "agents\\models\\tm_leet\\tm_leet_varianth.vmdl",
        "agents\\models\\tm_leet\\tm_leet_varianti.vmdl",
        "agents\\models\\tm_leet\\tm_leet_variantj.vmdl",
        "agents\\models\\tm_phoenix\\tm_phoenix.vmdl",
        "agents\\models\\tm_phoenix\\tm_phoenix_varianta.vmdl",
        "agents\\models\\tm_phoenix\\tm_phoenix_variantb.vmdl",
        "agents\\models\\tm_phoenix\\tm_phoenix_variantc.vmdl",
        "agents\\models\\tm_phoenix\\tm_phoenix_variantd.vmdl",
        "agents\\models\\tm_phoenix\\tm_phoenix_variantf.vmdl",
        "agents\\models\\tm_phoenix\\tm_phoenix_variantg.vmdl",
        "agents\\models\\tm_phoenix\\tm_phoenix_varianth.vmdl",
        "agents\\models\\tm_phoenix\\tm_phoenix_varianti.vmdl",
        "agents\\models\\tm_professional\\tm_professional_varf.vmdl",
        "agents\\models\\tm_professional\\tm_professional_varf1.vmdl",
        "agents\\models\\tm_professional\\tm_professional_varf2.vmdl",
        "agents\\models\\tm_professional\\tm_professional_varf3.vmdl",
        "agents\\models\\tm_professional\\tm_professional_varf4.vmdl",
        "agents\\models\\tm_professional\\tm_professional_varf5.vmdl",
        "agents\\models\\tm_professional\\tm_professional_varg.vmdl",
        "agents\\models\\tm_professional\\tm_professional_varh.vmdl",
        "agents\\models\\tm_professional\\tm_professional_vari.vmdl",
        "agents\\models\\tm_professional\\tm_professional_varj.vmdl"
    ];
}
