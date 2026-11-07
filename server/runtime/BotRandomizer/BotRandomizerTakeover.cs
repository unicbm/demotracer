using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace BotRandomizer;

public sealed partial class BotRandomizerPlugin
{
    private HookResult OnBotTakeover(EventBotTakeover @event, GameEventInfo info)
    {
        if (_draining || _applicator is null ||
            @event.Userid is not { IsValid: true, IsBot: false } human ||
            @event.Botid is not { IsValid: true, IsBot: true } bot ||
            human.PlayerPawn.Value is not { IsValid: true } pawn)
            return HookResult.Continue;

        var wearables = _applicator.CaptureTakeoverWearables(bot.Slot, pawn.EntityHandle.Raw);
        if (wearables.Knife is null && wearables.Gloves is null)
            return HookResult.Continue;

        var humanSlot = human.Slot;
        var botSlot = bot.Slot;
        var binding = new TakeoverPawnBinding(_mapEpoch, human.EntityHandle.Raw,
            bot.EntityHandle.Raw, pawn.EntityHandle.Raw);
        bool TryResolve(out CCSPlayerController currentBot, out CCSPlayerPawn currentPawn)
        {
            currentBot = null!;
            currentPawn = null!;
            if (_draining || _applicator is null ||
                Utilities.GetPlayerFromSlot(humanSlot) is not { IsValid: true, IsBot: false } currentHuman ||
                Utilities.GetPlayerFromSlot(botSlot) is not { IsValid: true, IsBot: true } resolvedBot ||
                currentHuman.PlayerPawn.Value is not { IsValid: true } resolvedPawn ||
                !binding.Matches(_mapEpoch, currentHuman.EntityHandle.Raw, resolvedBot.EntityHandle.Raw,
                    resolvedPawn.EntityHandle.Raw, currentHuman.OriginalControllerOfCurrentPawn.Raw,
                    currentHuman.ControllingBot, currentHuman.PawnIsAlive))
                return false;
            currentBot = resolvedBot;
            currentPawn = resolvedPawn;
            return true;
        }
        void Restore()
        {
            if (!TryResolve(out var currentBot, out var currentPawn))
                return;

            // Cosmetic repair only: the human retains all movement, input and
            // inventory control. Never resolve a random loadout for the human.
            if (_applicator!.RestoreTakeoverWearables(currentBot, currentPawn, wearables))
                Server.NextFrame(() =>
                {
                    if (TryResolve(out _, out var revealPawn))
                        _applicator!.ShowGloves(revealPawn);
                });
        }

        // Match the existing spawn settling passes: subclass/model setup and
        // wearable updates need to finish before the final cosmetic state sticks.
        Server.NextFrame(Restore);
        AddTimer(0.10f, Restore, TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(0.25f, Restore, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }
}
