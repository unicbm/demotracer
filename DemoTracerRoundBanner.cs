using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    internal const string DtrRoundBannerImageUrl =
        "https://cdn.jsdelivr.net/gh/unicbm/cs2-banner-overlay@c999941/assets/dtr-round-overlay.png";
    internal const int DtrRoundBannerTotalTicks = 320;
    internal const int DtrRoundBannerDurationSeconds = 5;
    private const string DtrRoundBannerLocToken =
        "<img src=\"" + DtrRoundBannerImageUrl + "\" style=\"width: 100%\"/>";

    private bool _roundBannerEnabled = true;
    private bool _roundBannerShownThisRound;
    private DtrRoundBannerPlayback? _roundBannerPlayback;

    private void ResetDtrRoundBannerForRound()
    {
        CancelDtrRoundBanner(resetRound: true);
    }

    private void TryStartDtrRoundBanner(string evidenceLabel)
    {
        if (!_roundBannerEnabled || _roundBannerShownThisRound || _loadedSlots.Count == 0)
            return;

        _roundBannerShownThisRound = true;
        var recipients = Utilities.GetPlayers()
            .Where(IsDtrRoundBannerRecipient)
            .Where(player => player.UserId.HasValue)
            .Select(player => new DtrRoundBannerRecipient(player.Slot, player.UserId.GetValueOrDefault()))
            .DistinctBy(recipient => recipient.Slot)
            .OrderBy(recipient => recipient.Slot)
            .ToArray();
        if (recipients.Length == 0)
            return;

        _roundBannerPlayback = new DtrRoundBannerPlayback(recipients);
        Server.PrintToConsole(
            $"dtr: round banner started evidence={evidenceLabel} recipients={recipients.Length} ticks={DtrRoundBannerTotalTicks}");
    }

    private void ProcessDtrRoundBanner()
    {
        var playback = _roundBannerPlayback;
        if (playback == null)
            return;
        if (_loadedSlots.Count == 0)
        {
            _roundBannerPlayback = null;
            return;
        }
        if (!playback.TryBeginTick())
        {
            if (playback.IsComplete)
                _roundBannerPlayback = null;
            return;
        }

        EventShowSurvivalRespawnStatus? bannerEvent = null;
        try
        {
            bannerEvent = new EventShowSurvivalRespawnStatus(true)
            {
                LocToken = DtrRoundBannerLocToken,
                Duration = DtrRoundBannerDurationSeconds,
            };

            foreach (var recipient in playback.Recipients)
            {
                var player = Utilities.GetPlayerFromSlot(recipient.Slot);
                if (player is not { IsValid: true } ||
                    player.IsHLTV ||
                    player.IsBot ||
                    player.UserId != recipient.UserId)
                {
                    continue;
                }

                try
                {
                    bannerEvent.FireEventToClient(player);
                }
                catch
                {
                    // A disconnected recipient must not interrupt sends to the others.
                }
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: round banner stopped after event failure: {ex.Message}");
            _roundBannerPlayback = null;
        }
        finally
        {
            try
            {
                bannerEvent?.Free();
            }
            catch
            {
            }
        }

        if (ReferenceEquals(_roundBannerPlayback, playback) && playback.IsComplete)
            _roundBannerPlayback = null;
    }

    private void CancelDtrRoundBanner(bool resetRound)
    {
        _roundBannerPlayback = null;
        if (resetRound)
            _roundBannerShownThisRound = false;
    }

    private static bool IsDtrRoundBannerRecipient(CCSPlayerController? player)
        => player is { IsValid: true } && !player.IsHLTV && !player.IsBot;
}

internal readonly record struct DtrRoundBannerRecipient(int Slot, int UserId);

internal sealed class DtrRoundBannerPlayback(IEnumerable<DtrRoundBannerRecipient> recipients)
{
    internal IReadOnlyList<DtrRoundBannerRecipient> Recipients { get; } = recipients
        .DistinctBy(recipient => recipient.Slot)
        .OrderBy(recipient => recipient.Slot)
        .ToArray();
    internal int SentTicks { get; private set; }
    internal bool IsComplete => SentTicks >= DemoTracerPlugin.DtrRoundBannerTotalTicks;

    internal bool TryBeginTick()
    {
        if (IsComplete)
            return false;

        SentTicks++;
        return true;
    }
}
