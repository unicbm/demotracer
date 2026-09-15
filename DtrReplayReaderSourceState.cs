/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Runtime.InteropServices;
using System.Buffers.Binary;
namespace DemoTracer;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeReplaySourceStateChange
{
    // ABI 21.40: Present bit 0 is presence; bits 1..24 are PlayerTick run
    // length minus one. All other fields retain legacy 0/1 presence.
    public uint TickIndex, FieldId, ValueBits, Present;
}
internal static partial class DtrReplayReader
{
    private enum SourceKind : byte { F32, I32, U32, Bool }
    // Keep IDs/types aligned with replay-source-fields.v1.json.
    private static readonly SourceKind[] SourceKinds =
    [
        SourceKind.U32, // ServerTick
        SourceKind.U32, // PlayerTick
        SourceKind.F32, // DuckRoot
        SourceKind.F32, // DuckView
        SourceKind.F32, // LastDuckTime
        SourceKind.Bool, // DuckOverride
        SourceKind.F32, // Stamina
        SourceKind.F32, // DuckAmount
        SourceKind.F32, // DuckSpeed
        SourceKind.Bool, // Ducked
        SourceKind.Bool, // Ducking
        SourceKind.Bool, // DesiresDuck
        SourceKind.I32, // LastJumpTick
        SourceKind.F32, // LastJumpFrac
        SourceKind.F32, // LastJumpVelocityZ
        SourceKind.Bool, // GroundTopology
        SourceKind.F32, // GroundTopologySmoothing
        SourceKind.F32, // FrictionStashedSpeed
        SourceKind.Bool, // UseFrictionStashedSpeed
        SourceKind.F32, // FrictionStashedUntilFrac
        SourceKind.I32, // LadderSurface
        SourceKind.F32, // FallVelocity
        SourceKind.I32, // LastActualJumpPressTick
        SourceKind.F32, // LastActualJumpPressFrac
        SourceKind.I32, // LastUsableJumpPressTick
        SourceKind.F32, // LastUsableJumpPressFrac
        SourceKind.I32, // LastLandedTick
        SourceKind.F32, // LastLandedFrac
        SourceKind.F32, // LastLandedVelocityX
        SourceKind.F32, // LastLandedVelocityY
        SourceKind.F32, // LastLandedVelocityZ
        SourceKind.F32, // VelocityModifier
        SourceKind.F32, // Friction
        SourceKind.F32, // GravityScale
        SourceKind.Bool, // GravityDisabled
        SourceKind.I32, // ShotsFired
        SourceKind.Bool, // Scoped
        SourceKind.F32, // BaseVelocityX
        SourceKind.F32, // BaseVelocityY
        SourceKind.F32, // BaseVelocityZ
        SourceKind.F32, // PredictableAngleX
        SourceKind.F32, // PredictableAngleY
        SourceKind.F32, // PredictableAngleZ
        SourceKind.F32, // PredictableAngleVelX
        SourceKind.F32, // PredictableAngleVelY
        SourceKind.F32, // PredictableAngleVelZ
        SourceKind.F32, // UnpredictableAngleX
        SourceKind.F32, // UnpredictableAngleY
        SourceKind.F32, // UnpredictableAngleZ
        SourceKind.I32, // PredictableTick
        SourceKind.F32, // PredictableTickFrac
        SourceKind.I32, // UnpredictableTick
        SourceKind.I32, // Clip1
        SourceKind.I32, // Clip2
        SourceKind.Bool, // InReload
        SourceKind.I32, // NextPrimaryTick
        SourceKind.F32, // NextPrimaryFrac
        SourceKind.I32, // NextSecondaryTick
        SourceKind.F32, // NextSecondaryFrac
        SourceKind.F32, // RecoilIndex
        SourceKind.F32, // AccuracyPenalty
        SourceKind.F32, // LastShotTime
        SourceKind.I32, // BurstShotsRemaining
        SourceKind.F32, // NextAttack
        SourceKind.U32, // ActiveWeaponHandle
        SourceKind.I32, // ReserveAmmoPrimary
        SourceKind.I32, // ReserveAmmoSecondary
    ];

    private static void RequireSourceStateSectionShape(
        DtrSectionHeader header, uint version, int tickCount, DtrReadLimits limits, ref long totalDecodedBytes)
    {
        if (version < 11)
            throw new InvalidDataException("source state requires DTR 11");
        if (header.ElementCount > (long)tickCount * SourceKinds.Length)
            throw new InvalidDataException("source state count exceeds tick/field capacity");
        if (header.SectionVersion == SectionVersionV1)
        {
            RequireSectionShape(header, "source state", header.ElementCount,
                ExpectedSectionLength(header.ElementCount, 16, "source state"), SectionVersionV1);
            return;
        }
        if (version < 12 || header.SectionVersion != SectionVersionV2)
            throw new InvalidDataException("unsupported source state section version");
        var length = header.UncompressedLength;
        if (length % 12 != 0 || length / 12 > header.ElementCount ||
            (length == 0) != (header.ElementCount == 0))
            throw new InvalidDataException("invalid compact source state length");
        var indexedBytes = ExpectedSectionLength(length / 12, 16, "source state");
        if (indexedBytes > limits.MaxDecodedSectionBytes)
            throw new InvalidDataException("indexed source state exceeds decoded section limit");
        AddToBudget(indexedBytes - length, ref totalDecodedBytes,
            limits.MaxTotalDecodedBytes, "total section decoded bytes");
    }

    private static NativeReplaySourceStateChange[] ReadCompactSourceState(byte[] body, int count, int tickCount)
    {
        var encodedCount = body.Length / 12;
        var columnBytes = encodedCount * 4;
        var changes = new NativeReplaySourceStateChange[encodedCount];
        Span<int> fieldCounts = stackalloc int[SourceKinds.Length];
        Span<int> fieldBases = stackalloc int[SourceKinds.Length];
        Span<int> fieldCursors = stackalloc int[SourceKinds.Length];
        fieldCounts.Clear();
        fieldCursors.Clear();
        for (var i = 0; i < encodedCount; ++i)
        {
            var field = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(columnBytes + i * 4)) & 0x7f;
            if (field >= SourceKinds.Length)
                throw new InvalidDataException("invalid source state field");
            ++fieldCounts[(int)field];
        }
        var valueBase = columnBytes * 2;
        for (var field = 0; field < SourceKinds.Length; ++field)
        {
            fieldBases[field] = valueBase;
            valueBase += fieldCounts[field] * 4;
        }
        Span<uint> previousBits = stackalloc uint[SourceKinds.Length];
        previousBits.Clear();
        uint tick = 0;
        ulong previousKey = 0, clockEnd = 0, expandedCount = 0;
        for (var i = 0; i < encodedCount; ++i)
        {
            var delta = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i * 4));
            if (delta > uint.MaxValue - tick)
                throw new InvalidDataException("source state tick delta overflow");
            tick += delta;
            var descriptor = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(columnBytes + i * 4));
            var field = descriptor & 0x7f;
            var present = (descriptor >> 7) & 1;
            var length = (descriptor >> 8) + 1;
            if (field >= SourceKinds.Length)
                throw new InvalidDataException("invalid source state field");
            var id = (int)field;
            var start = fieldBases[id] + fieldCursors[id]++;
            var stride = fieldCounts[id];
            var xor = (uint)body[start] | ((uint)body[start + stride] << 8) |
                ((uint)body[start + stride * 2] << 16) | ((uint)body[start + stride * 3] << 24);
            var bits = previousBits[id] ^ xor;
            previousBits[(int)field] = bits;
            var key = ((ulong)tick << 32) | field;
            var end = (ulong)tick + length;
            if (tick >= tickCount || end > (ulong)tickCount ||
                (i > 0 && previousKey >= key) || (field == 1 && tick < clockEnd) ||
                (length > 1 && (field != 1 || present != 1)) ||
                (present == 0 && bits != 0) ||
                (present == 1 && SourceKinds[field] == SourceKind.F32 && !float.IsFinite(BitConverter.UInt32BitsToSingle(bits))) ||
                (present == 1 && SourceKinds[field] == SourceKind.Bool && bits > 1))
                throw new InvalidDataException("invalid or overlapping source state run");
            if (field == 1) clockEnd = end;
            previousKey = key;
            expandedCount += length;
            if (expandedCount > (ulong)count)
                throw new InvalidDataException("source state expanded count mismatch");
            changes[i] = new NativeReplaySourceStateChange {
                TickIndex = tick, FieldId = field, ValueBits = bits, Present = present | ((length - 1) << 1)
            };
        }
        if (expandedCount != (ulong)count)
            throw new InvalidDataException("source state expanded count mismatch");
        return changes;
    }

    private static NativeReplaySourceStateChange[] ReadSourceState(byte[] body, int count, int tickCount)
    {
        using var reader = new BinaryReader(new MemoryStream(body, writable: false));
        var changes = new NativeReplaySourceStateChange[count];
        (uint Tick, uint Field)? previous = null;
        for (var i = 0; i < count; ++i)
        {
            var change = new NativeReplaySourceStateChange
            {
                TickIndex = reader.ReadUInt32(),
                FieldId = reader.ReadUInt32(),
                ValueBits = reader.ReadUInt32(),
                Present = reader.ReadUInt32()
            };
            var key = (change.TickIndex, change.FieldId);
            if (change.TickIndex >= tickCount || change.FieldId >= SourceKinds.Length || change.Present > 1 ||
                (previous.HasValue && previous.Value.CompareTo(key) >= 0) ||
                (change.Present == 0 && change.ValueBits != 0) ||
                (change.Present == 1 && SourceKinds[change.FieldId] == SourceKind.F32 && !float.IsFinite(BitConverter.UInt32BitsToSingle(change.ValueBits))) ||
                (change.Present == 1 && SourceKinds[change.FieldId] == SourceKind.Bool && change.ValueBits > 1))
                throw new InvalidDataException("invalid or unordered source state change");
            changes[i] = change;
            previous = key;
        }
        return changes;
    }
}
