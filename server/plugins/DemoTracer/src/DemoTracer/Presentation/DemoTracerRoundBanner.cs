/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    internal const string DtrRoundBannerImageUrl =
        "https://cdn.jsdelivr.net/gh/unicbm/cs2-banner-overlay@c999941/assets/dtr-round-overlay.png";
    private const int DtrRoundBannerDurationSeconds = 5;
    private const string DtrRoundBannerLocToken =
        "<img src=\"" + DtrRoundBannerImageUrl + "\" style=\"width: 100%\"/>";

    private bool _roundBannerEnabled = true;

    private void ShowDtrRoundBanner()
    {
        if (!_roundBannerEnabled || _session.LoadedSlots.Count == 0 || _soulChallenge is { Active: true })
            return;

        var recipients = Utilities.GetPlayers().Where(IsHumanPlayer).ToArray();
        if (recipients.Length == 0)
            return;

        var banner = new EventShowSurvivalRespawnStatus(true);
        try
        {
            banner.LocToken = DtrRoundBannerLocToken;
            banner.Duration = DtrRoundBannerDurationSeconds;
            var failures = 0;
            string? firstError = null;
            foreach (var player in recipients)
            {
                try
                {
                    banner.Userid = player;
                    banner.FireEventToClient(player);
                }
                catch (NativeException ex)
                {
                    failures++;
                    firstError ??= ex.Message;
                }
            }
            if (failures > 0)
                Server.PrintToConsole($"[DTR WARN] round banner send failed for {failures}/{recipients.Length} players: {firstError}");
        }
        finally
        {
            banner.Free();
        }
    }
}
