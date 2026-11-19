namespace BotRandomizer;

internal sealed record TakeoverWearables(uint WeaponHandle, KnifeSelection? Knife, GloveSelection? Gloves);

internal readonly record struct TakeoverPawnBinding(ulong MapEpoch, uint HumanController, uint BotController, uint Pawn)
{
    internal bool Matches(ulong mapEpoch, uint humanController, uint botController,
        uint controlledPawn, uint botPlayerPawn, uint pawnController, bool controllingBot, bool alive)
        => MapEpoch == mapEpoch && HumanController == humanController &&
           BotController == botController && Pawn == controlledPawn && Pawn == botPlayerPawn &&
           HumanController == pawnController && controllingBot && alive;
}
