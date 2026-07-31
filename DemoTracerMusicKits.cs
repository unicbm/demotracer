using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    [GameEventHandler(HookMode.Pre)]
    public HookResult OnReplayRoundMvpMusicKit(EventRoundMvp @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not { IsValid: true } ||
            !_session.LoadedReplays.TryGetValue(player.Slot, out var replay) ||
            !ReplayMusicKitAlignmentAllowed(replay.MusicKitId) ||
            !TryValidateBotRandomizerClaim(
                player.Slot,
                replay.SteamId,
                DemoTracerCosmeticWriteField.MusicKit) ||
            !IsReplaySlotStillSafe(player.Slot))
        {
            return HookResult.Continue;
        }

        try
        {
            _ = ApplyReplayMusicKit(player, replay.MusicKitId, replay.SteamId);

            // Keep the original event and publish the demo-backed kit through every
            // field used by the MVP panel. Suppressing and recreating this event can
            // lose engine-populated fields and leave clients with stale kit state.
            @event.Musickitid = replay.MusicKitId;
            @event.Musickitmvps = 0;
            @event.Nomusic = 0;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: music kit MVP event failed slot={player.Slot} kit={replay.MusicKitId}: {ex.Message}");
        }

        return HookResult.Continue;
    }
}
