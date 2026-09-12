namespace BotRandomizer;

internal readonly record struct IntroParticipant(ulong Xuid, bool IsBot);

internal static class IntroAgentAssignment
{
    // Inputs follow the engine/upstream ordering: players by slot, previews by ordinal.
    internal static IReadOnlyList<(int PreviewIndex, int PlayerIndex)> Match(
        IReadOnlyList<IntroParticipant> players, IReadOnlyList<ulong> previewXuids)
    {
        var result = new List<(int, int)>();
        var assignedPlayers = new HashSet<int>();
        for (var preview = 0; preview < previewXuids.Count; preview++)
        {
            var xuid = previewXuids[preview];
            if (xuid == 0 || previewXuids.Count(candidate => candidate == xuid) != 1)
                continue;
            var candidates = Enumerable.Range(0, players.Count)
                .Where(index => players[index].Xuid == xuid).ToArray();
            if (candidates.Length != 1)
                continue;
            var player = candidates[0];
            assignedPlayers.Add(player);
            if (players[player].IsBot)
                result.Add((preview, player));
        }

        // Ordinary bots have XUID 0. Use their ordered positions only once every
        // human has an explicit position, so a pending human identity is never guessed.
        if (Enumerable.Range(0, players.Count).Any(index =>
                !players[index].IsBot && !assignedPlayers.Contains(index)))
            return result;

        var remainingBots = Enumerable.Range(0, players.Count)
            .Where(index => players[index].IsBot && !assignedPlayers.Contains(index));
        var anonymousPreviews = Enumerable.Range(0, previewXuids.Count)
            .Where(index => previewXuids[index] == 0);
        result.AddRange(anonymousPreviews.Zip(remainingBots, (preview, player) => (preview, player)));
        return result;
    }
}
