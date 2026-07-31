using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

internal static partial class DtrReplayReader
{
    private static NativeMovementSnapshot ReadCurrentSnapshot(BinaryReader reader)
    {
        return new NativeMovementSnapshot
        {
            OriginX = reader.ReadSingle(),
            OriginY = reader.ReadSingle(),
            OriginZ = reader.ReadSingle(),
            VelX = reader.ReadSingle(),
            VelY = reader.ReadSingle(),
            VelZ = reader.ReadSingle(),
            Pitch = reader.ReadSingle(),
            Yaw = reader.ReadSingle(),
            Roll = reader.ReadSingle(),
            EntityFlags = reader.ReadUInt32(),
            MoveType = reader.ReadByte(),
            Pad0 = reader.ReadByte(),
            Pad1 = reader.ReadByte(),
            Pad2 = reader.ReadByte(),
            Buttons = reader.ReadUInt64(),
            Buttons1 = reader.ReadUInt64(),
            Buttons2 = reader.ReadUInt64(),
            DuckAmount = reader.ReadSingle(),
            DuckSpeed = reader.ReadSingle(),
            LadderNormalX = reader.ReadSingle(),
            LadderNormalY = reader.ReadSingle(),
            LadderNormalZ = reader.ReadSingle(),
            Ducked = reader.ReadByte(),
            Ducking = reader.ReadByte(),
            DesiresDuck = reader.ReadByte(),
            ActualMoveType = reader.ReadByte()
        };
    }

    private static NativeMovementSnapshot[] ReadSnapshotsFromSection(
        byte[] body,
        int count,
        uint sectionVersion)
        => sectionVersion switch
        {
            SectionVersionV1 => ReadSnapshotsFromSectionV1(body, count),
            SectionVersionV2 => ReadSnapshotsFromSectionV2(body, count),
            _ => throw new InvalidDataException(
                $"unsupported snapshots section version {sectionVersion}")
        };

    private static NativeMovementSnapshot[] ReadSnapshotsFromSectionV1(byte[] body, int count)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var snapshots = new NativeMovementSnapshot[count];
        for (var i = 0; i < count; i++)
            snapshots[i] = ReadCurrentSnapshot(reader);
        RequireConsumed(stream, "snapshots");
        return snapshots;
    }

    private static NativeMovementSnapshot[] ReadSnapshotsFromSectionV2(byte[] body, int count)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var snapshots = new NativeMovementSnapshot[count];
        ReadDeltaUInt32Column(reader, count, "snapshot origin x", (index, value) =>
            snapshots[index].OriginX = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "snapshot origin y", (index, value) =>
            snapshots[index].OriginY = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "snapshot origin z", (index, value) =>
            snapshots[index].OriginZ = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "snapshot velocity x", (index, value) =>
            snapshots[index].VelX = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "snapshot velocity y", (index, value) =>
            snapshots[index].VelY = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "snapshot velocity z", (index, value) =>
            snapshots[index].VelZ = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "snapshot pitch", (index, value) =>
            snapshots[index].Pitch = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "snapshot yaw", (index, value) =>
            snapshots[index].Yaw = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "snapshot roll", (index, value) =>
            snapshots[index].Roll = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "snapshot entity flags", (index, value) =>
            snapshots[index].EntityFlags = value);
        ReadDeltaByteColumn(reader, count, "snapshot move type", (index, value) =>
            snapshots[index].MoveType = value);
        ReadDeltaUInt64Column(reader, count, "snapshot buttons", (index, value) =>
            snapshots[index].Buttons = value);
        ReadDeltaUInt64Column(reader, count, "snapshot buttons1", (index, value) =>
            snapshots[index].Buttons1 = value);
        ReadDeltaUInt64Column(reader, count, "snapshot buttons2", (index, value) =>
            snapshots[index].Buttons2 = value);
        ReadDeltaUInt32Column(reader, count, "snapshot duck amount", (index, value) =>
            snapshots[index].DuckAmount = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "snapshot duck speed", (index, value) =>
            snapshots[index].DuckSpeed = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "snapshot ladder x", (index, value) =>
            snapshots[index].LadderNormalX = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "snapshot ladder y", (index, value) =>
            snapshots[index].LadderNormalY = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "snapshot ladder z", (index, value) =>
            snapshots[index].LadderNormalZ = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaByteColumn(reader, count, "snapshot ducked", (index, value) =>
            snapshots[index].Ducked = value);
        ReadDeltaByteColumn(reader, count, "snapshot ducking", (index, value) =>
            snapshots[index].Ducking = value);
        ReadDeltaByteColumn(reader, count, "snapshot desires duck", (index, value) =>
            snapshots[index].DesiresDuck = value);
        ReadDeltaByteColumn(reader, count, "snapshot actual move type", (index, value) =>
            snapshots[index].ActualMoveType = value);
        RequireConsumed(stream, "snapshots");
        return snapshots;
    }

    private static TickMetadata[] ReadTickMetadataFromSection(byte[] body, int count)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var metadata = new TickMetadata[count];
        for (var i = 0; i < count; i++)
        {
            metadata[i] = new TickMetadata(
                reader.ReadInt32(),
                reader.ReadUInt32());
        }
        RequireConsumed(stream, "tick metadata");
        return metadata;
    }

    private static ReplayProjectileEvent[] ReadProjectilesFromSection(byte[] body, int count)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var projectiles = new ReplayProjectileEvent[count];
        for (var i = 0; i < count; i++)
            projectiles[i] = ReadProjectileEvent(reader);
        RequireConsumed(stream, "projectiles");
        return projectiles;
    }

    private static NativeSubtickMove[] ReadSubticksFromSection(byte[] body, int count)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var subticks = new NativeSubtickMove[count];
        for (var i = 0; i < count; i++)
            subticks[i] = ReadSubtickMove(reader);
        RequireConsumed(stream, "subticks");
        return subticks;
    }

    private static NativeSubtickMove ReadSubtickMove(BinaryReader reader)
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

    private static NativeReplayCommandFrame[] ReadCommandFramesFromSection(
        byte[] body,
        int count,
        uint sectionVersion)
        => sectionVersion switch
        {
            SectionVersionV1 => ReadCommandFramesFromSectionV1(body, count),
            SectionVersionV2 => ReadCommandFramesFromSectionV2(body, count),
            _ => throw new InvalidDataException(
                $"unsupported command frames section version {sectionVersion}")
        };

    private static NativeReplayCommandFrame[] ReadCommandFramesFromSectionV1(byte[] body, int count)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var frames = new NativeReplayCommandFrame[count];
        for (var i = 0; i < count; i++)
        {
            frames[i] = new NativeReplayCommandFrame
            {
                ForwardMove = reader.ReadSingle(),
                LeftMove = reader.ReadSingle(),
                UpMove = reader.ReadSingle(),
                Pitch = reader.ReadSingle(),
                Yaw = reader.ReadSingle(),
                Roll = reader.ReadSingle(),
                Buttons = reader.ReadUInt64(),
                Buttons1 = reader.ReadUInt64(),
                Buttons2 = reader.ReadUInt64(),
                MouseDx = reader.ReadInt32(),
                MouseDy = reader.ReadInt32(),
                WeaponSelect = reader.ReadInt32(),
                Fields = reader.ReadUInt32(),
                LeftHandDesired = reader.ReadByte(),
                Pad0 = reader.ReadByte(),
                Pad1 = reader.ReadByte(),
                Pad2 = reader.ReadByte()
            };
        }
        RequireConsumed(stream, "command frames");
        return frames;
    }

    private static NativeReplayCommandFrame[] ReadCommandFramesFromSectionV2(byte[] body, int count)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var frames = new NativeReplayCommandFrame[count];
        ReadDeltaUInt32Column(reader, count, "command forward move", (index, value) =>
            frames[index].ForwardMove = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "command left move", (index, value) =>
            frames[index].LeftMove = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "command up move", (index, value) =>
            frames[index].UpMove = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "command pitch", (index, value) =>
            frames[index].Pitch = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "command yaw", (index, value) =>
            frames[index].Yaw = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt32Column(reader, count, "command roll", (index, value) =>
            frames[index].Roll = BitConverter.UInt32BitsToSingle(value));
        ReadDeltaUInt64Column(reader, count, "command buttons", (index, value) =>
            frames[index].Buttons = value);
        ReadDeltaUInt64Column(reader, count, "command buttons1", (index, value) =>
            frames[index].Buttons1 = value);
        ReadDeltaUInt64Column(reader, count, "command buttons2", (index, value) =>
            frames[index].Buttons2 = value);
        ReadDeltaUInt32Column(reader, count, "command mouse dx", (index, value) =>
            frames[index].MouseDx = unchecked((int)value));
        ReadDeltaUInt32Column(reader, count, "command mouse dy", (index, value) =>
            frames[index].MouseDy = unchecked((int)value));
        ReadDeltaUInt32Column(reader, count, "command weapon select", (index, value) =>
            frames[index].WeaponSelect = unchecked((int)value));
        ReadDeltaUInt32Column(reader, count, "command fields", (index, value) =>
            frames[index].Fields = value);
        ReadDeltaByteColumn(reader, count, "command left hand desired", (index, value) =>
            frames[index].LeftHandDesired = value);
        RequireConsumed(stream, "command frames");
        return frames;
    }

    private static void ReadDeltaUInt32Column(
        BinaryReader reader,
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
            var delta = unchecked((uint)UnzigzagUInt32(ReadUleb128UInt32(reader, name)));
            var current = unchecked(previous + delta);
            set(index, current);
            previous = current;
        }
    }

    private static void ReadDeltaUInt64Column(
        BinaryReader reader,
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
            var delta = unchecked((ulong)UnzigzagUInt64(ReadUleb128UInt64(reader, name)));
            var current = unchecked(previous + delta);
            set(index, current);
            previous = current;
        }
    }

    private static void ReadDeltaByteColumn(
        BinaryReader reader,
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
            var encoded = ReadUleb128UInt32(reader, name);
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

    private static uint ReadUleb128UInt32(BinaryReader reader, string name)
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

    private static ulong ReadUleb128UInt64(BinaryReader reader, string name)
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

    private static NativeReplayMovementExtra[] ReadMovementExtrasFromSection(byte[] body, int count)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream);
        var extras = new NativeReplayMovementExtra[count];
        for (var i = 0; i < count; i++)
        {
            extras[i] = new NativeReplayMovementExtra
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
        }
        RequireConsumed(stream, "movement extras");
        return extras;
    }

    private static void RequireConsumed(Stream stream, string name)
    {
        if (stream.Position != stream.Length)
            throw new InvalidDataException($"trailing bytes in {name} section");
    }

    private static ReplayProjectileEvent ReadProjectileEvent(BinaryReader reader)
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
        var detonationPosition = new ReplayVector3(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());
        return new ReplayProjectileEvent(
            tickIndex,
            kind,
            weaponDefIndex,
            initialPosition,
            initialVelocity,
            detonationPosition,
            new ReplayVector3(0.0f, 0.0f, 0.0f),
            -1,
            string.Empty,
            0.0f);
    }

}
