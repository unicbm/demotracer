namespace BotRandomizer;

internal static class AttributeEncoding
{
    internal static float UInt32BitsToSingle(uint value)
        => BitConverter.Int32BitsToSingle(unchecked((int)value));

    internal static float Int32BitsToSingle(int value)
        => BitConverter.Int32BitsToSingle(value);
}
