using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace BotRandomizer;

public sealed partial class BotRandomizerPlugin
{
    private void ApplyIntroAgents()
    {
        if (_draining)
            return;
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;
        if (rules is not { TeamIntroPeriod: true })
            return;

        ApplyIntroAgentsForTeam("team_intro_counterterrorist", 3, rules.CTTeamIntroVariant);
        ApplyIntroAgentsForTeam("team_intro_terrorist", 2, rules.TTeamIntroVariant);
    }

    private void ApplyIntroAgentsForTeam(string designerName, byte team, int variant)
    {
        var players = Utilities.GetPlayers()
            .Where(player => player.IsValid && !player.IsHLTV && player.TeamNum == team)
            .OrderBy(player => player.Slot).ToArray();
        if (!players.Any(player => player.IsBot))
            return;

        var previews = Utilities.FindAllEntitiesByDesignerName<CCSGO_TeamPreviewCharacterPosition>(designerName)
            .Where(preview => preview.IsValid && preview.Variant == variant)
            .OrderBy(preview => preview.Ordinal).Take(players.Length).ToArray();
        var matches = IntroAgentAssignment.Match(
            players.Select(player => new IntroParticipant(player.SteamID, player.IsBot)).ToArray(),
            previews.Select(preview => preview.Xuid).ToArray());
        foreach (var (previewIndex, playerIndex) in matches)
        {
            var state = GetOrCreateState(players[playerIndex]);
            if (state is null)
                continue;
            TryGetWritePolicy(state, out var replay);
            if (_options.ResolveIntroAgent(replay, state.Loadout) is not { } defIndex)
                continue;

            var preview = previews[previewIndex];
            preview.AgentItem.ItemDefinitionIndex = defIndex;
            Utilities.SetStateChanged(preview, "CCSGO_TeamPreviewCharacterPosition", "m_agentItem");
        }
    }
}
