/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Buffers.Binary;

namespace DemoTracer;

internal static partial class DtrReplayReader
{
    private static NativeMovementSnapshot[] ReadSnapshotsFromSection(byte[] body, int count)
    {
        var reader = new SectionPayloadReader(body);
        var snapshots = new NativeMovementSnapshot[count];
        ReadDeltaUInt32Column(ref reader, count, "snapshot origin x", (index, value) =>
            snapshots[index].OriginX = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "snapshot origin y", (index, value) =>
            snapshots[index].OriginY = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "snapshot origin z", (index, value) =>
            snapshots[index].OriginZ = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "snapshot velocity x", (index, value) =>
            snapshots[index].VelX = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "snapshot velocity y", (index, value) =>
            snapshots[index].VelY = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "snapshot velocity z", (index, value) =>
            snapshots[index].VelZ = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "snapshot pitch", (index, value) =>
            snapshots[index].Pitch = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "snapshot yaw", (index, value) =>
            snapshots[index].Yaw = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "snapshot roll", (index, value) =>
            snapshots[index].Roll = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "snapshot entity flags", (index, value) =>
            snapshots[index].EntityFlags = value);
        ReadDeltaByteColumn(ref reader, count, "snapshot move type", (index, value) =>
            snapshots[index].MoveType = value);
        ReadDeltaUInt64Column(ref reader, count, "snapshot buttons", (index, value) =>
            snapshots[index].Buttons = value);
        ReadDeltaUInt64Column(ref reader, count, "snapshot buttons1", (index, value) =>
            snapshots[index].Buttons1 = value);
        ReadDeltaUInt64Column(ref reader, count, "snapshot buttons2", (index, value) =>
            snapshots[index].Buttons2 = value);
        ReadDeltaUInt32Column(ref reader, count, "snapshot duck amount", (index, value) =>
            snapshots[index].DuckAmount = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "snapshot duck speed", (index, value) =>
            snapshots[index].DuckSpeed = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "snapshot ladder x", (index, value) =>
            snapshots[index].LadderNormalX = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "snapshot ladder y", (index, value) =>
            snapshots[index].LadderNormalY = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "snapshot ladder z", (index, value) =>
            snapshots[index].LadderNormalZ = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaByteColumn(ref reader, count, "snapshot ducked", (index, value) =>
            snapshots[index].Ducked = value);
        ReadDeltaByteColumn(ref reader, count, "snapshot ducking", (index, value) =>
            snapshots[index].Ducking = value);
        ReadDeltaByteColumn(ref reader, count, "snapshot desires duck", (index, value) =>
            snapshots[index].DesiresDuck = value);
        ReadDeltaByteColumn(ref reader, count, "snapshot actual move type", (_, _) => { });
        reader.RequireConsumed("snapshots");
        return snapshots;
    }

    private static TickMetadata[] ReadTickMetadataFromSection(byte[] body, int count)
    {
        var reader = new SectionPayloadReader(body);
        var metadata = new TickMetadata[count];
        for (var i = 0; i < count; i++)
        {
            metadata[i] = new TickMetadata(
                reader.ReadInt32(),
                reader.ReadUInt32());
        }
        reader.RequireConsumed("tick metadata");
        return metadata;
    }

    private static ReplayProjectileEvent[] ReadProjectilesFromSection(byte[] body, int count)
    {
        var reader = new SectionPayloadReader(body);
        var projectiles = new ReplayProjectileEvent[count];
        for (var i = 0; i < count; i++)
            projectiles[i] = ReadProjectileEvent(ref reader);
        reader.RequireConsumed("projectiles");
        return projectiles;
    }

    private static NativeSubtickMove[] ReadSubticksFromSection(byte[] body, int count)
    {
        var reader = new SectionPayloadReader(body);
        var subticks = new NativeSubtickMove[count];
        for (var i = 0; i < count; i++)
            subticks[i] = ReadSubtickMove(ref reader);
        reader.RequireConsumed("subticks");
        return subticks;
    }

    private static NativeSubtickMove ReadSubtickMove(ref SectionPayloadReader reader)
    {
        return new NativeSubtickMove
        {
            When = reader.ReadSingle(),
            Button = reader.ReadUInt32(),
            Pressed = reader.ReadSingle(),
            AnalogForward = reader.ReadSingle(),
            AnalogLeft = reader.ReadSingle(),
            PitchDelta = reader.ReadSingle(),
            YawDelta = reader.ReadSingle()
        };
    }

    private static NativeReplayCommandFrame[] ReadCommandFramesFromSection(byte[] body, int count)
    {
        var reader = new SectionPayloadReader(body);
        var frames = new NativeReplayCommandFrame[count];
        ReadDeltaUInt32Column(ref reader, count, "command forward move", (index, value) =>
            frames[index].ForwardMove = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "command left move", (index, value) =>
            frames[index].LeftMove = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "command up move", (index, value) =>
            frames[index].UpMove = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "command pitch", (index, value) =>
            frames[index].Pitch = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "command yaw", (index, value) =>
            frames[index].Yaw = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(ref reader, count, "command roll", (index, value) =>
            frames[index].Roll = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt64Column(ref reader, count, "command buttons", (index, value) =>
            frames[index].Buttons = value);
        ReadDeltaUInt64Column(ref reader, count, "command buttons1", (index, value) =>
            frames[index].Buttons1 = value);
        ReadDeltaUInt64Column(ref reader, count, "command buttons2", (index, value) =>
            frames[index].Buttons2 = value);
        ReadDeltaUInt32Column(ref reader, count, "command mouse dx", (index, value) =>
            frames[index].MouseDx = unchecked((int)value));
        ReadDeltaUInt32Column(ref reader, count, "command mouse dy", (index, value) =>
            frames[index].MouseDy = unchecked((int)value));
        ReadDeltaUInt32Column(ref reader, count, "command weapon select", (_, _) => { });
        ReadDeltaUInt32Column(ref reader, count, "command fields", (index, value) =>
            frames[index].Fields = value & ~(1U << 6));
        ReadDeltaByteColumn(ref reader, count, "command left hand desired", (index, value) =>
            frames[index].LeftHandDesired = value);
        reader.RequireConsumed("command frames");
        return frames;
    }

    private static void ReadDeltaUInt32Column(
        ref SectionPayloadReader reader,
        int count,
        string name,
        Action<int, uint> set)
    {
        if (count == 0)
            return;
        var previous = reader.ReadUInt32();
        set(0, previous);
        for (var index = 1; index < count; index++)
        {
            var delta = unchecked((uint)UnzigzagUInt32(ReadUleb128UInt32(ref reader, name)));
            var current = unchecked(previous + delta);
            set(index, current);
            previous = current;
        }
    }

    private static void ReadDeltaUInt64Column(
        ref SectionPayloadReader reader,
        int count,
        string name,
        Action<int, ulong> set)
    {
        if (count == 0)
            return;
        var previous = reader.ReadUInt64();
        set(0, previous);
        for (var index = 1; index < count; index++)
        {
            var delta = unchecked((ulong)UnzigzagUInt64(ReadUleb128UInt64(ref reader, name)));
            var current = unchecked(previous + delta);
            set(index, current);
            previous = current;
        }
    }

    private static void ReadDeltaByteColumn(
        ref SectionPayloadReader reader,
        int count,
        string name,
        Action<int, byte> set)
    {
        if (count == 0)
            return;
        var previous = reader.ReadByte();
        set(0, previous);
        for (var index = 1; index < count; index++)
        {
            var encoded = ReadUleb128UInt32(ref reader, name);
            if (encoded > byte.MaxValue)
                throw new InvalidDataException($"{name} delta {encoded} exceeds byte");
            var delta = unchecked((byte)(sbyte)UnzigzagByte((byte)encoded));
            var current = unchecked((byte)(previous + delta));
            set(index, current);
            previous = current;
        }
    }

    private static sbyte UnzigzagByte(byte value)
        => unchecked((sbyte)((value >> 1) ^ (uint)-(int)(value & 1)));

    private static int UnzigzagUInt32(uint value)
        => unchecked((int)(value >> 1) ^ -(int)(value & 1));

    private static long UnzigzagUInt64(ulong value)
        => unchecked((long)(value >> 1) ^ -(long)(value & 1));

    private static uint ReadUleb128UInt32(ref SectionPayloadReader reader, string name)
    {
        uint value = 0;
        for (var index = 0; index < 5; index++)
        {
            var next = reader.ReadByte();
            if (index == 4 && (next & 0xf0) != 0)
                throw new InvalidDataException($"{name} varint overflows uint32");
            value |= (uint)(next & 0x7f) << (index * 7);
            if ((next & 0x80) == 0)
            {
                if (index > 0 && next == 0)
                    throw new InvalidDataException($"{name} varint is not canonical");
                return value;
            }
        }
        throw new InvalidDataException($"{name} varint is too long");
    }

    private static ulong ReadUleb128UInt64(ref SectionPayloadReader reader, string name)
    {
        ulong value = 0;
        for (var index = 0; index < 10; index++)
        {
            var next = reader.ReadByte();
            if (index == 9 && (next & 0xfe) != 0)
                throw new InvalidDataException($"{name} varint overflows uint64");
            value |= (ulong)(next & 0x7f) << (index * 7);
            if ((next & 0x80) == 0)
            {
                if (index > 0 && next == 0)
                    throw new InvalidDataException($"{name} varint is not canonical");
                return value;
            }
        }
        throw new InvalidDataException($"{name} varint is too long");
    }

    private static NativeReplayMovementExtra[] ReadMovementExtrasFromSection(byte[] body, int count, bool retainData)
    {
        var reader = new SectionPayloadReader(body);
        var extras = retainData ? new NativeReplayMovementExtra[count] : [];
        for (var i = 0; i < count; i++)
        {
            var extra = new NativeReplayMovementExtra
            {
                Fields = reader.ReadUInt32(),
                JumpPressedTime = reader.ReadSingle(),
                LastDuckTime = reader.ReadSingle(),
                LastActualJumpPressTick = reader.ReadInt32(),
                LastActualJumpPressFrac = reader.ReadSingle(),
                LastUsableJumpPressTick = reader.ReadInt32(),
                LastUsableJumpPressFrac = reader.ReadSingle(),
                LastLandedTick = reader.ReadInt32(),
                LastLandedFrac = reader.ReadSingle(),
                LastLandedVelocityX = reader.ReadSingle(),
                LastLandedVelocityY = reader.ReadSingle(),
                LastLandedVelocityZ = reader.ReadSingle()
            };
            ValidateMovementExtra(in extra, i);
            if (retainData)
                extras[i] = extra;
        }
        reader.RequireConsumed("movement extras");
        return extras;
    }

    // Section bodies are already decoded and bounded. Read directly from that
    // memory instead of routing every delta byte through a second stream.
    private ref struct SectionPayloadReader(ReadOnlySpan<byte> body)
    {
        private ReadOnlySpan<byte> _remaining = body;

        public byte ReadByte()
        {
            if (_remaining.IsEmpty)
                throw new EndOfStreamException();
            var value = _remaining[0];
            _remaining = _remaining[1..];
            return value;
        }

        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Read(sizeof(uint)));
        public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Read(sizeof(ulong)));
        public int ReadInt32() => unchecked((int)ReadUInt32());
        public float ReadSingle() => BitConverter.UInt32BitsToSingle(ReadUInt32());

        public readonly void RequireConsumed(string name)
        {
            if (!_remaining.IsEmpty)
                throw new InvalidDataException($"trailing bytes in {name} section");
        }

        private ReadOnlySpan<byte> Read(int length)
        {
            if (_remaining.Length < length)
                throw new EndOfStreamException();
            var value = _remaining[..length];
            _remaining = _remaining[length..];
            return value;
        }
    }

    private static ReplayProjectileEvent ReadProjectileEvent(ref SectionPayloadReader reader)
    {
        var tickIndex = reader.ReadUInt32();
        var weaponDefIndex = reader.ReadInt32();
        var kindRaw = reader.ReadByte();
        if (kindRaw > (byte)ReplayProjectileKind.Decoy)
            throw new InvalidDataException($"unsupported projectile kind {kindRaw}");
        var kind = (ReplayProjectileKind)kindRaw;
        if (reader.ReadByte() != 0 || reader.ReadByte() != 0 || reader.ReadByte() != 0)
            throw new InvalidDataException("projectile padding must be zero");
        var initialPosition = new ReplayVector3(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());
        var initialVelocity = new ReplayVector3(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());
        // Consume the retired v12 detonation position without using it in playback.
        _ = reader.ReadSingle();
        _ = reader.ReadSingle();
        _ = reader.ReadSingle();
        return new ReplayProjectileEvent(
            tickIndex,
            kind,
            weaponDefIndex,
            initialPosition,
            initialVelocity);
    }

}
