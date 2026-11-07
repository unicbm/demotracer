namespace BotRandomizer;

internal sealed record TakeoverWearables(uint WeaponHandle, KnifeSelection? Knife, GloveSelection? Gloves);

internal readonly record struct TakeoverPawnBinding(ulong MapEpoch, uint HumanController, uint BotController, uint Pawn)
{
    internal bool Matches(ulong mapEpoch, uint humanController, uint botController,
        uint pawn, uint originalController, bool controllingBot, bool alive)
        => MapEpoch == mapEpoch && HumanController == humanController &&
           BotController == botController && Pawn == pawn &&
           originalController == BotController && controllingBot && alive;
}
