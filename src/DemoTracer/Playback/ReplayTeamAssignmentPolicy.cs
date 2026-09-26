/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotRandomizerApi;
using CounterStrikeSharp.API.Modules.Utils;

namespace DemoTracer;

internal static class ReplayTeamAssignmentPolicy
{
    internal static CsTeam ResolveUpcomingTeam(
        CsTeam currentTeam,
        bool switchingTeamsAtRoundReset)
        => (CsTeam)BotRandomizerReplayTeamPolicy.ResolveUpcomingTeam(
            (byte)currentTeam,
            switchingTeamsAtRoundReset);

    internal static bool LiveTeamMatches(CsTeam? manifestTeam, CsTeam actualTeam)
        => !manifestTeam.HasValue || manifestTeam.Value == actualTeam;

    internal static bool CanAlignC4(CsTeam? manifestTeam, CsTeam actualTeam)
        => manifestTeam == CsTeam.Terrorist && actualTeam == CsTeam.Terrorist;
}
