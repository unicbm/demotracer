/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal static partial class DtrReplayReader
{
    private static (NativeReplayInputHistoryTick[], NativeReplayInputHistoryEntry[])
        ReadInputHistoryFromSection(byte[] body, int tickCount)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var ticks = new NativeReplayInputHistoryTick[tickCount];
        var entries = new List<NativeReplayInputHistoryEntry>();
        for (var tickIndex = 0; tickIndex < tickCount; tickIndex++)
        {
            var tick = new NativeReplayInputHistoryTick
            {
                SourceClientTick = reader.ReadInt32(),
                Attack1StartHistoryIndex = reader.ReadInt32(),
                Attack2StartHistoryIndex = reader.ReadInt32(),
                NumEntries = reader.ReadUInt32()
            };
            if (tick.NumEntries > MaxInputHistoryPerTick)
                throw new InvalidDataException(
                    $"input history tick {tickIndex} count {tick.NumEntries} exceeds {MaxInputHistoryPerTick}");
            ValidateHistoryIndex(tick.Attack1StartHistoryIndex, tick.NumEntries, tickIndex, "attack1");
            ValidateHistoryIndex(tick.Attack2StartHistoryIndex, tick.NumEntries, tickIndex, "attack2");
            for (var i = 0; i < tick.NumEntries; i++)
                entries.Add(ReadInputHistoryEntry(reader));
            ticks[tickIndex] = tick;
        }
        RequireConsumed(stream, "input history");
        return (ticks, entries.ToArray());
    }

    private static void ValidateHistoryIndex(int index, uint count, int tickIndex, string name)
    {
        if (index < -1 || (index >= 0 && (uint)index >= count))
            throw new InvalidDataException(
                $"input history tick {tickIndex} {name} index {index} is outside {count} entries");
    }

    private static NativeReplayInputHistoryEntry ReadInputHistoryEntry(BinaryReader reader)
    {
        return new NativeReplayInputHistoryEntry
        {
            Fields = reader.ReadUInt32(),
            ViewPitch = reader.ReadSingle(),
            ViewYaw = reader.ReadSingle(),
            ViewRoll = reader.ReadSingle(),
            RenderTickCount = reader.ReadInt32(),
            RenderTickFraction = reader.ReadSingle(),
            PlayerTickCount = reader.ReadInt32(),
            PlayerTickFraction = reader.ReadSingle(),
            ClInterpFraction = reader.ReadSingle(),
            SvInterp0SrcTick = reader.ReadInt32(),
            SvInterp0DstTick = reader.ReadInt32(),
            SvInterp0Fraction = reader.ReadSingle(),
            SvInterp1SrcTick = reader.ReadInt32(),
            SvInterp1DstTick = reader.ReadInt32(),
            SvInterp1Fraction = reader.ReadSingle(),
            PlayerInterpSrcTick = reader.ReadInt32(),
            PlayerInterpDstTick = reader.ReadInt32(),
            PlayerInterpFraction = reader.ReadSingle(),
            FrameNumber = reader.ReadInt32(),
            TargetEntIndex = reader.ReadInt32(),
            ShootPositionX = reader.ReadSingle(),
            ShootPositionY = reader.ReadSingle(),
            ShootPositionZ = reader.ReadSingle(),
            TargetHeadPosCheckX = reader.ReadSingle(),
            TargetHeadPosCheckY = reader.ReadSingle(),
            TargetHeadPosCheckZ = reader.ReadSingle(),
            TargetAbsPosCheckX = reader.ReadSingle(),
            TargetAbsPosCheckY = reader.ReadSingle(),
            TargetAbsPosCheckZ = reader.ReadSingle(),
            TargetAbsAngCheckX = reader.ReadSingle(),
            TargetAbsAngCheckY = reader.ReadSingle(),
            TargetAbsAngCheckZ = reader.ReadSingle()
        };
    }
}
