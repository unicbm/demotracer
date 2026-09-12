using BotRandomizerApi;

namespace BotRandomizer;

// These switches control randomized defaults, never positive replay evidence.
internal sealed class RandomizerOptions
{
    internal const string ControlKey = "github.com/ed0ard/CS2-Bot-Randomizer";

    internal bool Weapons { get; private set; } = true;
    internal bool Knives { get; private set; } = true;
    internal bool Gloves { get; private set; } = true;
    internal bool Agents { get; private set; } = true;
    internal bool Music { get; private set; } = true;
    internal bool Stickers { get; private set; } = true;
    internal bool Charms { get; private set; } = true;
    internal bool HasWeaponCosmetics => Weapons || Stickers || Charms;

    internal bool TryApplyControl(string key, string category, string value)
    {
        if (key != ControlKey || !TryParseBoolean(value, out var enabled))
            return false;

        switch (category.ToLowerInvariant())
        {
            case "weapons": Weapons = enabled; break;
            case "knives": Knives = enabled; break;
            case "gloves": Gloves = enabled; break;
            case "agents": Agents = enabled; break;
            case "music": Music = enabled; break;
            case "stickers": Stickers = enabled; break;
            case "charms": Charms = enabled; break;
            default: return false;
        }
        return true;
    }

    internal string? ResolveAgentModel(CosmeticWritePolicy? replay, BotCosmeticLoadout random)
        => replay?.AgentMode switch
        {
            BotRandomizerAgentPlanMode.ReplayModel => replay.AgentModel,
            BotRandomizerAgentPlanMode.PreserveEngineDefault => null,
            _ => Agents ? random.AgentModel : random.Team == RandomizerAssets.CounterTerroristTeam
                ? "agents\\models\\ctm_sas\\ctm_sas.vmdl"
                : "agents\\models\\tm_phoenix\\tm_phoenix.vmdl"
        };

    internal int ResolveMusicKit(CosmeticWritePolicy? replay, BotCosmeticLoadout random)
        => replay?.MusicKit ?? (Music ? random.MusicKit : 0);

    internal ushort? ResolveIntroAgent(CosmeticWritePolicy? replay, BotCosmeticLoadout random)
        => replay?.AgentMode switch
        {
            BotRandomizerAgentPlanMode.ReplayModel => replay.AgentItemDefinitionIndex,
            BotRandomizerAgentPlanMode.PreserveEngineDefault => null,
            _ => Agents ? RandomizerAssets.AgentDefIndexByModel[random.AgentModel] : (ushort)0
        };

    internal WeaponCosmeticSelection? ResolveWeapon(
        WeaponCatalogEntry weapon,
        WeaponCosmeticSelection? random)
    {
        if (!HasWeaponCosmetics || random is null)
            return null;

        var legacy = Weapons && random.Legacy;
        var schemas = legacy ? weapon.LegacyStickerSchemaCount : weapon.StickerSchemaCount;
        return random with
        {
            PaintKit = Weapons ? random.PaintKit : 0,
            Seed = Weapons ? random.Seed : 0,
            Wear = Weapons ? random.Wear : 0,
            Legacy = legacy,
            Stickers = Stickers ? random.Stickers.Where(sticker => sticker.Schema < schemas).ToArray() : [],
            Keychain = Charms ? random.Keychain : null
        };
    }

    private static bool TryParseBoolean(string value, out bool enabled)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1": case "on": case "yes": case "true":
                enabled = true;
                return true;
            case "0": case "off": case "no": case "false":
                enabled = false;
                return true;
            default:
                enabled = false;
                return false;
        }
    }
}
