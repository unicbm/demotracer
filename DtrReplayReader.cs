using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

internal sealed record DtrReadLimits
{
    private const long Mebibyte = 1024L * 1024L;

    public static DtrReadLimits Default { get; } = new();

    public long MaxFileBytes { get; init; } = 64 * Mebibyte;
    public int MaxSectionCount { get; init; } = 32;
    public long MaxCompressedSectionBytes { get; init; } = 48 * Mebibyte;
    public long MaxTotalCompressedBytes { get; init; } = 64 * Mebibyte;
    public long MaxDecodedSectionBytes { get; init; } = 48 * Mebibyte;
    public long MaxTotalDecodedBytes { get; init; } = 64 * Mebibyte;
    public int MaxTickCount { get; init; } = 32_768;
    public int MaxSubtickCount { get; init; } = 1_179_648;
    public int MaxSubticksPerTick { get; init; } = 36;
    public int MaxProjectileCount { get; init; } = 4_096;
    public int MaxMetadataJsonBytes { get; init; } = 8 * (int)Mebibyte;

    public void Validate()
    {
        if (MaxFileBytes < 0 ||
            MaxSectionCount < 0 ||
            MaxCompressedSectionBytes < 0 ||
            MaxTotalCompressedBytes < 0 ||
            MaxDecodedSectionBytes < 0 ||
            MaxTotalDecodedBytes < 0 ||
            MaxTickCount < 0 ||
            MaxSubtickCount < 0 ||
            MaxSubticksPerTick < 0 ||
            MaxProjectileCount < 0 ||
            MaxMetadataJsonBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DtrReadLimits), "DTR read limits cannot be negative");
        }
    }
}

internal static class DtrReplayReader
{
    private const byte RecCodecBrotli = 1;
    private const byte SectionCodecNone = 0;
    private const int SectionHeaderByteSize = 36;
    private const int TickMetadataByteSize = 8;
    private const int ProjectileEventByteSize = 48;
    private const uint SectionSnapshots = 1;
    private const uint SectionTickMetadata = 2;
    private const uint SectionProjectiles = 3;
    private const uint SectionHighFidelityJson = 4;
    private const uint SectionSubticks = 5;
    private const uint SectionCommandFrames = 6;
    private const uint SectionMovementExtras = 7;
    private const uint SectionVersionV1 = 1;
    private const uint SectionVersionV2 = 2;
    private const uint CommandFieldsAll = 0xff;

    private static readonly byte[] RecMagic =
    [
        (byte)'C', (byte)'S', (byte)'D', (byte)'T',
        (byte)'R', (byte)'R', (byte)'E', (byte)'C'
    ];

    private static readonly JsonSerializerOptions HifiJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static DtrReplayFile Read(string path)
        => Read(path, DtrReadLimits.Default);

    public static DtrReplayFile Read(string path, DtrReadLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();

        if (!string.Equals(Path.GetExtension(path), ".dtr", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("expected .dtr replay file");

        using var stream = File.OpenRead(path);
        if (stream.Length > limits.MaxFileBytes)
            throw new InvalidDataException(
                $".dtr file length {stream.Length} exceeds limit {limits.MaxFileBytes}");
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadBytes(RecMagic.Length);
        if (!magic.SequenceEqual(RecMagic))
            throw new InvalidDataException("bad .dtr magic");

        var version = reader.ReadUInt32();
        if (version is < BotControllerNative.MinRecFormatVersion or > BotControllerNative.RecFormatVersion)
            throw new InvalidDataException(
                $"unsupported .dtr version {version}; expected {BotControllerNative.MinRecFormatVersion}..{BotControllerNative.RecFormatVersion}");

        var tickRate = reader.ReadSingle();
        _ = reader.ReadUInt32(); // round
        var side = reader.ReadByte();
        if (side is not (0 or 2 or 3))
            throw new InvalidDataException($"side {side} is not 0, 2, or 3");
        _ = reader.ReadUInt32(); // flags
        _ = reader.ReadUInt64(); // steam_id
        var tickCount = CheckedLimitedCount(reader.ReadUInt32(), limits.MaxTickCount, "tick_count");
        var subtickCount = CheckedLimitedCount(reader.ReadUInt32(), limits.MaxSubtickCount, "subtick_count");
        var projectileCount = version >= 4
            ? CheckedLimitedCount(reader.ReadUInt32(), limits.MaxProjectileCount, "projectile_count")
            : 0;
        var playStartTickIndex = version >= 5
            ? CheckedCount(reader.ReadUInt32(), "play_start_tick_index")
            : 0;
        var metadataJsonLength = version >= 6
            ? CheckedLimitedCount(reader.ReadUInt32(), limits.MaxMetadataJsonBytes, "metadata_json_len")
            : 0;
        ValidateHeaderSubtickCounts(tickCount, subtickCount, limits.MaxSubticksPerTick);
        ValidatePlayStartTickIndex(tickCount, playStartTickIndex);
        _ = ReadRecString(reader); // map
        _ = ReadRecString(reader); // player name

        var replay = version >= 7
            ? ReadV7Sections(
                reader,
                version,
                tickRate,
                tickCount,
                subtickCount,
                projectileCount,
                playStartTickIndex,
                metadataJsonLength,
                limits)
            : ReadLegacyBody(
                reader,
                version,
                tickRate,
                tickCount,
                subtickCount,
                projectileCount,
                playStartTickIndex,
                metadataJsonLength,
                limits);

        if (stream.Position != stream.Length)
            throw new InvalidDataException("trailing bytes after top-level .dtr payload");
        ValidateReplaySemantics(replay);
        return replay;
    }

    private static DtrReplayFile ReadLegacyBody(
        BinaryReader reader,
        uint version,
        float tickRate,
        int tickCount,
        int subtickCount,
        int projectileCount,
        int playStartTickIndex,
        int metadataJsonLength,
        DtrReadLimits limits)
    {
        var codec = reader.ReadByte();
        if (codec != RecCodecBrotli)
            throw new InvalidDataException($"unsupported .dtr codec {codec}");

        var bodyUncompressedLength = CheckedLimitedLength(
            reader.ReadUInt64(),
            Math.Min(limits.MaxDecodedSectionBytes, limits.MaxTotalDecodedBytes),
            "body_uncompressed_len");
        var bodyCompressedLength = CheckedLimitedLength(
            reader.ReadUInt64(),
            Math.Min(limits.MaxCompressedSectionBytes, limits.MaxTotalCompressedBytes),
            "body_compressed_len");
        var expectedBodyLength = ExpectedBodyLength(tickCount, subtickCount, projectileCount, metadataJsonLength);
        if (bodyUncompressedLength != expectedBodyLength)
            throw new InvalidDataException($"body length {bodyUncompressedLength} != expected {expectedBodyLength}");

        EnsureRemaining(reader, bodyCompressedLength, "compressed .dtr body");
        var compressed = reader.ReadBytes(bodyCompressedLength);
        if (compressed.Length != bodyCompressedLength)
            throw new EndOfStreamException("truncated compressed .dtr body");

        var body = DecompressBrotli(compressed, bodyUncompressedLength);
        using var bodyStream = new MemoryStream(body, writable: false);
        using var bodyReader = new BinaryReader(bodyStream);

        var snapshotCount = tickCount == 0 ? 0 : tickCount + 1;
        var snapshots = new NativeMovementSnapshot[snapshotCount];
        for (var i = 0; i < snapshotCount; i++)
            snapshots[i] = ReadCurrentSnapshot(bodyReader);

        var ticks = new NativeReplayTick[tickCount];
        long expectedSubticks = 0;
        for (var i = 0; i < tickCount; i++)
        {
            var weaponDefIndex = bodyReader.ReadInt32();
            var numSubtick = bodyReader.ReadUInt32();
            ValidateAndAddTickSubticks(numSubtick, ref expectedSubticks, subtickCount, limits.MaxSubticksPerTick);
            ticks[i] = new NativeReplayTick
            {
                Pre = snapshots[i],
                Post = snapshots[i + 1],
                WeaponDefIndex = weaponDefIndex,
                NumSubtick = numSubtick
            };
        }

        if (expectedSubticks != subtickCount)
            throw new InvalidDataException($"tick subtick sum {expectedSubticks} != header subtick count {subtickCount}");

        var projectiles = new ReplayProjectileEvent[projectileCount];
        for (var i = 0; i < projectileCount; i++)
            projectiles[i] = ReadProjectileEvent(bodyReader);

        var highFidelity = ReplayHighFidelityMetadata.Empty;
        if (metadataJsonLength > 0)
        {
            var metadataJson = bodyReader.ReadBytes(metadataJsonLength);
            if (metadataJson.Length != metadataJsonLength)
                throw new EndOfStreamException("truncated high_fidelity metadata in .dtr");
            highFidelity = ReadHighFidelityMetadata(metadataJson);
        }

        var subticks = new NativeSubtickMove[subtickCount];
        for (var i = 0; i < subtickCount; i++)
        {
            subticks[i] = new NativeSubtickMove
            {
                When = bodyReader.ReadSingle(),
                Button = bodyReader.ReadUInt32(),
                Pressed = bodyReader.ReadSingle(),
                AnalogForward = bodyReader.ReadSingle(),
                AnalogLeft = bodyReader.ReadSingle(),
                PitchDelta = bodyReader.ReadSingle(),
                YawDelta = bodyReader.ReadSingle()
            };
        }

        if (bodyStream.Position != bodyStream.Length)
            throw new InvalidDataException("trailing bytes in .dtr body");

        return new DtrReplayFile(
            version,
            ticks,
            MergeProjectileMetadata(projectiles, highFidelity),
            highFidelity,
            subticks,
            [],
            [],
            tickRate,
            (uint)playStartTickIndex);
    }

    private static DtrReplayFile ReadV7Sections(
        BinaryReader reader,
        uint version,
        float tickRate,
        int tickCount,
        int subtickCount,
        int projectileCount,
        int playStartTickIndex,
        int metadataJsonLength,
        DtrReadLimits limits)
    {
        var sectionCount = CheckedLimitedCount(reader.ReadUInt32(), limits.MaxSectionCount, "section_count");
        var snapshotCount = tickCount == 0 ? 0 : checked(tickCount + 1);
        NativeMovementSnapshot[]? snapshots = null;
        TickMetadata[]? tickMetadata = null;
        ReplayProjectileEvent[]? projectiles = null;
        ReplayHighFidelityMetadata highFidelity = ReplayHighFidelityMetadata.Empty;
        NativeSubtickMove[]? subticks = null;
        NativeReplayCommandFrame[]? commandFrames = null;
        NativeReplayMovementExtra[]? movementExtras = null;
        var seenHighFidelity = false;
        var seenKnownSections = new HashSet<uint>();
        long totalCompressedBytes = 0;
        long totalDecodedBytes = 0;

        for (var i = 0; i < sectionCount; i++)
        {
            var header = ReadSectionHeader(
                reader,
                limits,
                ref totalCompressedBytes,
                ref totalDecodedBytes);
            var remainingHeaderBytes = checked((sectionCount - i - 1) * SectionHeaderByteSize);

            if (!IsKnownSection(header.SectionId))
            {
                EnsureRemaining(
                    reader,
                    checked(header.CompressedLength + remainingHeaderBytes),
                    "section payload and remaining headers");
                SkipExact(reader, header.CompressedLength);
                continue;
            }

            var (name, expectedElementCount, expectedUncompressedLength) = header.SectionId switch
            {
                SectionSnapshots => (
                    "snapshots",
                    snapshotCount,
                    ExpectedSectionLength(snapshotCount, BotControllerNative.MovementSnapshotByteSize, "snapshots")),
                SectionTickMetadata => (
                    "tick metadata",
                    tickCount,
                    ExpectedSectionLength(tickCount, TickMetadataByteSize, "tick metadata")),
                SectionSubticks => (
                    "subticks",
                    subtickCount,
                    ExpectedSectionLength(subtickCount, BotControllerNative.SubtickMoveByteSize, "subticks")),
                SectionProjectiles => (
                    "projectiles",
                    projectileCount,
                    ExpectedSectionLength(projectileCount, ProjectileEventByteSize, "projectiles")),
                SectionHighFidelityJson => (
                    "high fidelity metadata",
                    metadataJsonLength == 0 ? 0 : 1,
                    metadataJsonLength),
                SectionCommandFrames => (
                    "command frames",
                    tickCount,
                    ExpectedSectionLength(tickCount, BotControllerNative.ReplayCommandFrameByteSize, "command frames")),
                SectionMovementExtras => (
                    "movement extras",
                    tickCount,
                    ExpectedSectionLength(tickCount, BotControllerNative.ReplayMovementExtraByteSize, "movement extras")),
                _ => throw new InvalidDataException($"unsupported known section {header.SectionId}")
            };
            RejectDuplicate(!seenKnownSections.Add(header.SectionId), name);
            var usesV2ColumnLayout = version >= 8 &&
                header.SectionId is SectionSnapshots or SectionCommandFrames;
            RequireSectionShape(
                header,
                name,
                expectedElementCount,
                usesV2ColumnLayout ? null : expectedUncompressedLength,
                usesV2ColumnLayout ? SectionVersionV2 : SectionVersionV1);
            ValidateKnownSectionCodec(header, name);

            EnsureRemaining(
                reader,
                checked(header.CompressedLength + remainingHeaderBytes),
                "section payload and remaining headers");
            var compressed = ReadExact(reader, header.CompressedLength, "section payload");
            var body = DecodeSectionBody(compressed, header.Codec, header.UncompressedLength);

            switch (header.SectionId)
            {
                case SectionSnapshots:
                    snapshots = ReadSnapshotsFromSection(
                        body,
                        snapshotCount,
                        header.SectionVersion);
                    break;
                case SectionTickMetadata:
                    tickMetadata = ReadTickMetadataFromSection(body, tickCount);
                    ValidateTickSubtickMetadata(
                        tickMetadata,
                        subtickCount,
                        limits.MaxSubticksPerTick);
                    break;
                case SectionSubticks:
                    subticks = ReadSubticksFromSection(body, subtickCount);
                    break;
                case SectionProjectiles:
                    projectiles = ReadProjectilesFromSection(body, projectileCount);
                    break;
                case SectionHighFidelityJson:
                    highFidelity = metadataJsonLength == 0
                        ? ReplayHighFidelityMetadata.Empty
                        : ReadHighFidelityMetadata(body);
                    seenHighFidelity = true;
                    break;
                case SectionCommandFrames:
                    commandFrames = ReadCommandFramesFromSection(
                        body,
                        tickCount,
                        header.SectionVersion);
                    break;
                case SectionMovementExtras:
                    movementExtras = ReadMovementExtrasFromSection(body, tickCount);
                    break;
            }
        }

        if (snapshots is null)
            throw new InvalidDataException("missing required section snapshots");
        if (tickMetadata is null)
            throw new InvalidDataException("missing required section tick metadata");
        if (subticks is null)
            throw new InvalidDataException("missing required section subticks");
        if (projectileCount > 0 && projectiles is null)
            throw new InvalidDataException("missing required section projectiles");
        if (metadataJsonLength > 0 && !seenHighFidelity)
            throw new InvalidDataException("missing required section high fidelity metadata");

        var ticks = new NativeReplayTick[tickCount];
        long expectedSubticks = 0;
        for (var i = 0; i < tickCount; i++)
        {
            ticks[i] = new NativeReplayTick
            {
                Pre = snapshots[i],
                Post = snapshots[i + 1],
                WeaponDefIndex = tickMetadata[i].WeaponDefIndex,
                NumSubtick = tickMetadata[i].NumSubtick
            };
            expectedSubticks += tickMetadata[i].NumSubtick;
        }

        if (expectedSubticks != subtickCount)
            throw new InvalidDataException($"tick subtick sum {expectedSubticks} != header subtick count {subtickCount}");

        return new DtrReplayFile(
            version,
            ticks,
            MergeProjectileMetadata(projectiles ?? [], highFidelity),
            highFidelity,
            subticks,
            commandFrames ?? [],
            movementExtras ?? [],
            tickRate,
            (uint)playStartTickIndex);
    }

    private static void ValidatePlayStartTickIndex(int tickCount, int playStartTickIndex)
    {
        if (tickCount == 0)
        {
            if (playStartTickIndex == 0)
                return;
            throw new InvalidDataException(
                $"play_start_tick_index {playStartTickIndex} requires at least one tick");
        }
        if (playStartTickIndex >= tickCount)
            throw new InvalidDataException(
                $"play_start_tick_index {playStartTickIndex} out of range for {tickCount} ticks");
    }

    private static int CheckedCount(uint value, string fieldName)
    {
        if (value > int.MaxValue)
            throw new InvalidDataException($"{fieldName} too large: {value}");
        return (int)value;
    }

    private static int CheckedLimitedCount(uint value, int limit, string fieldName)
    {
        if (value > (uint)limit)
            throw new InvalidDataException($"{fieldName} {value} exceeds limit {limit}");
        return CheckedCount(value, fieldName);
    }

    private static int CheckedLength(ulong value, string fieldName)
    {
        if (value > int.MaxValue)
            throw new InvalidDataException($"{fieldName} too large: {value}");
        return (int)value;
    }

    private static int CheckedLimitedLength(ulong value, long limit, string fieldName)
    {
        if (value > (ulong)limit)
            throw new InvalidDataException($"{fieldName} {value} exceeds limit {limit}");
        return CheckedLength(value, fieldName);
    }

    private static DtrSectionHeader ReadSectionHeader(
        BinaryReader reader,
        DtrReadLimits limits,
        ref long totalCompressedBytes,
        ref long totalDecodedBytes)
    {
        var sectionId = reader.ReadUInt32();
        var sectionVersion = reader.ReadUInt32();
        var codec = reader.ReadByte();
        if (reader.ReadByte() != 0 || reader.ReadUInt16() != 0)
            throw new InvalidDataException("section header padding must be zero");
        var flags = reader.ReadUInt32();
        if (flags != 0)
            throw new InvalidDataException($"unsupported section flags 0x{flags:X8}");
        var elementCount = CheckedCount(reader.ReadUInt32(), "section_element_count");
        var uncompressedLength = CheckedLimitedLength(
            reader.ReadUInt64(),
            limits.MaxDecodedSectionBytes,
            "section_uncompressed_len");
        var compressedLength = CheckedLimitedLength(
            reader.ReadUInt64(),
            limits.MaxCompressedSectionBytes,
            "section_compressed_len");
        AddToBudget(
            uncompressedLength,
            ref totalDecodedBytes,
            limits.MaxTotalDecodedBytes,
            "total section decoded bytes");
        AddToBudget(
            compressedLength,
            ref totalCompressedBytes,
            limits.MaxTotalCompressedBytes,
            "total section compressed bytes");
        return new DtrSectionHeader(
            sectionId,
            sectionVersion,
            codec,
            elementCount,
            uncompressedLength,
            compressedLength);
    }

    private static void AddToBudget(int value, ref long total, long limit, string name)
    {
        if (value > limit - total)
            throw new InvalidDataException($"{name} exceeds limit {limit}");
        total += value;
    }

    private static bool IsKnownSection(uint sectionId)
        => sectionId is SectionSnapshots
            or SectionTickMetadata
            or SectionProjectiles
            or SectionHighFidelityJson
            or SectionSubticks
            or SectionCommandFrames
            or SectionMovementExtras;

    private static void RequireSectionShape(
        DtrSectionHeader header,
        string name,
        int expectedElementCount,
        int? expectedUncompressedLength,
        uint expectedSectionVersion)
    {
        if (header.SectionVersion != expectedSectionVersion)
        {
            throw new InvalidDataException(
                $"unsupported {name} section version {header.SectionVersion}; expected {expectedSectionVersion}");
        }
        if (header.ElementCount != expectedElementCount)
            throw new InvalidDataException(
                $"{name} section count {header.ElementCount} != expected {expectedElementCount}");
        if (expectedUncompressedLength.HasValue &&
            header.UncompressedLength != expectedUncompressedLength.Value)
        {
            throw new InvalidDataException(
                $"{name} section length {header.UncompressedLength} != expected {expectedUncompressedLength}");
        }
        if (expectedSectionVersion == SectionVersionV2 &&
            expectedElementCount == 0 &&
            header.UncompressedLength != 0)
        {
            throw new InvalidDataException(
                $"empty {name} section has non-zero length {header.UncompressedLength}");
        }
    }

    private static void ValidateKnownSectionCodec(DtrSectionHeader header, string name)
    {
        if (header.Codec is not SectionCodecNone and not RecCodecBrotli)
            throw new InvalidDataException($"unsupported {name} section codec {header.Codec}");
        if (header.Codec == SectionCodecNone && header.CompressedLength != header.UncompressedLength)
        {
            throw new InvalidDataException(
                $"uncompressed {name} section payload length {header.CompressedLength} != expected {header.UncompressedLength}");
        }
    }

    private static void RejectDuplicate(bool seen, string name)
    {
        if (seen)
            throw new InvalidDataException($"duplicate section {name}");
    }

    private static byte[] DecodeSectionBody(byte[] compressed, byte codec, int expectedLength)
    {
        return codec switch
        {
            SectionCodecNone => RequireExactLength(compressed, expectedLength, "uncompressed section"),
            RecCodecBrotli => DecompressBrotli(compressed, expectedLength),
            _ => throw new InvalidDataException($"unsupported section codec {codec}")
        };
    }

    private static byte[] RequireExactLength(byte[] bytes, int expectedLength, string name)
    {
        if (bytes.Length != expectedLength)
            throw new InvalidDataException($"{name} length {bytes.Length} != expected {expectedLength}");
        return bytes;
    }

    private static byte[] ReadExact(BinaryReader reader, int length, string name)
    {
        EnsureRemaining(reader, length, name);
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException($"truncated {name}");
        return bytes;
    }

    private static void SkipExact(BinaryReader reader, int length)
    {
        Span<byte> buffer = stackalloc byte[4096];
        var remaining = length;
        while (remaining > 0)
        {
            var read = reader.Read(buffer[..Math.Min(buffer.Length, remaining)]);
            if (read == 0)
                throw new EndOfStreamException("truncated skipped section");
            remaining -= read;
        }
    }

    private static int ExpectedBodyLength(int tickCount, int subtickCount, int projectileCount, int metadataJsonLength)
    {
        var snapshotCount = tickCount == 0 ? 0 : (long)tickCount + 1;
        var expected = checked(
            snapshotCount * BotControllerNative.MovementSnapshotByteSize +
            (long)tickCount * TickMetadataByteSize +
            (long)projectileCount * ProjectileEventByteSize +
            metadataJsonLength +
            (long)subtickCount * BotControllerNative.SubtickMoveByteSize);
        if (expected > int.MaxValue)
            throw new InvalidDataException($"expected .dtr body length too large: {expected}");
        return (int)expected;
    }

    private static int ExpectedSectionLength(int count, int elementSize, string name)
    {
        var expected = checked((long)count * elementSize);
        if (expected > int.MaxValue)
            throw new InvalidDataException($"expected {name} section length too large: {expected}");
        return (int)expected;
    }

    private static void EnsureRemaining(BinaryReader reader, int length, string name)
    {
        var stream = reader.BaseStream;
        if (!stream.CanSeek)
            return;
        var remaining = stream.Length - stream.Position;
        if (length > remaining)
            throw new EndOfStreamException($"truncated {name}: need {length} bytes, have {remaining}");
    }

    private static void ValidateHeaderSubtickCounts(
        int tickCount,
        int subtickCount,
        int maxSubticksPerTick)
    {
        var maximumForTicks = checked((long)tickCount * maxSubticksPerTick);
        if (subtickCount > maximumForTicks)
        {
            throw new InvalidDataException(
                $"subtick_count {subtickCount} exceeds {maxSubticksPerTick} per tick for {tickCount} ticks");
        }
    }

    private static void ValidateAndAddTickSubticks(
        uint numSubtick,
        ref long total,
        int declaredSubtickCount,
        int maxSubticksPerTick)
    {
        if (numSubtick > (uint)maxSubticksPerTick)
        {
            throw new InvalidDataException(
                $"tick subtick count {numSubtick} exceeds limit {maxSubticksPerTick}");
        }
        if (numSubtick > declaredSubtickCount - total)
            throw new InvalidDataException($"tick subtick sum exceeds header subtick count {declaredSubtickCount}");
        total += numSubtick;
    }

    private static void ValidateTickSubtickMetadata(
        TickMetadata[] metadata,
        int declaredSubtickCount,
        int maxSubticksPerTick)
    {
        long total = 0;
        foreach (var tick in metadata)
        {
            ValidateAndAddTickSubticks(
                tick.NumSubtick,
                ref total,
                declaredSubtickCount,
                maxSubticksPerTick);
        }
        if (total != declaredSubtickCount)
            throw new InvalidDataException($"tick subtick sum {total} != header subtick count {declaredSubtickCount}");
    }

    private static byte[] DecompressBrotli(byte[] compressed, int expectedLength)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        var output = GC.AllocateUninitializedArray<byte>(expectedLength);
        var totalRead = 0;
        while (totalRead < output.Length)
        {
            var read = brotli.Read(output, totalRead, output.Length - totalRead);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"decompressed body length {totalRead} != expected {expectedLength}");
            }
            totalRead += read;
        }

        if (brotli.ReadByte() != -1)
            throw new InvalidDataException($"decompressed body exceeds expected length {expectedLength}");
        return output;
    }

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

    private static void ValidateReplaySemantics(DtrReplayFile replay)
    {
        if (!float.IsFinite(replay.TickRate) || replay.TickRate <= 0.0f)
            throw new InvalidDataException("tick_rate must be finite and positive");

        for (var i = 0; i < replay.Ticks.Length; i++)
        {
            ValidateSnapshot(replay.Ticks[i].Pre, $"tick {i} pre");
            ValidateSnapshot(replay.Ticks[i].Post, $"tick {i} post");
            if (replay.Ticks[i].WeaponDefIndex < -1)
            {
                throw new InvalidDataException(
                    $"tick {i} weapon_def_index {replay.Ticks[i].WeaponDefIndex} is below -1");
            }
        }

        for (var i = 0; i < replay.Subticks.Length; i++)
        {
            var subtick = replay.Subticks[i];
            if (!float.IsFinite(subtick.When) || subtick.When < 0.0f || subtick.When >= 1.0f)
                throw new InvalidDataException($"subtick {i} when must be finite and in [0, 1)");
            RequireFinite(
                [subtick.Pressed, subtick.AnalogForward, subtick.AnalogLeft, subtick.PitchDelta, subtick.YawDelta],
                $"subtick {i}");
        }

        for (var i = 0; i < replay.CommandFrames.Length; i++)
        {
            var frame = replay.CommandFrames[i];
            var unknownFields = frame.Fields & ~CommandFieldsAll;
            if (unknownFields != 0)
                throw new InvalidDataException($"command frame {i} has unknown fields 0x{unknownFields:X8}");
            if (frame.LeftHandDesired > 1)
                throw new InvalidDataException($"command frame {i} left_hand_desired must be 0 or 1");
            if (frame.Pad0 != 0 || frame.Pad1 != 0 || frame.Pad2 != 0)
                throw new InvalidDataException($"command frame {i} padding must be zero");
            if (frame.WeaponSelect < -1)
            {
                throw new InvalidDataException(
                    $"command frame {i} weapon_select {frame.WeaponSelect} is below -1");
            }
            RequireFinite(
                [frame.ForwardMove, frame.LeftMove, frame.UpMove, frame.Pitch, frame.Yaw, frame.Roll],
                $"command frame {i}");
        }

        for (var i = 0; i < replay.MovementExtras.Length; i++)
        {
            var extra = replay.MovementExtras[i];
            RequireFinite(
                [
                    extra.JumpPressedTime,
                    extra.LastDuckTime,
                    extra.LastActualJumpPressFrac,
                    extra.LastUsableJumpPressFrac,
                    extra.LastLandedFrac,
                    extra.LastLandedVelocityX,
                    extra.LastLandedVelocityY,
                    extra.LastLandedVelocityZ
                ],
                $"movement extra {i}");
        }

        for (var i = 0; i < replay.Projectiles.Length; i++)
        {
            var projectile = replay.Projectiles[i];
            if (projectile.TickIndex >= replay.Ticks.Length)
            {
                throw new InvalidDataException(
                    $"projectile {i} tick_index {projectile.TickIndex} out of range for {replay.Ticks.Length} ticks");
            }
            if (projectile.Kind is < ReplayProjectileKind.Unknown or > ReplayProjectileKind.Decoy)
                throw new InvalidDataException($"projectile {i} kind is out of range");
            if (projectile.WeaponDefIndex < -1)
            {
                throw new InvalidDataException(
                    $"projectile {i} weapon_def_index {projectile.WeaponDefIndex} is below -1");
            }
            RequireFinite(
                [
                    projectile.InitialPosition.X,
                    projectile.InitialPosition.Y,
                    projectile.InitialPosition.Z,
                    projectile.InitialVelocity.X,
                    projectile.InitialVelocity.Y,
                    projectile.InitialVelocity.Z,
                    projectile.DetonationPosition.X,
                    projectile.DetonationPosition.Y,
                    projectile.DetonationPosition.Z,
                    projectile.EffectPosition.X,
                    projectile.EffectPosition.Y,
                    projectile.EffectPosition.Z,
                    projectile.EffectConfidence
                ],
                $"projectile {i}");
        }
    }

    private static void ValidateSnapshot(NativeMovementSnapshot snapshot, string name)
    {
        RequireFinite(
            [
                snapshot.OriginX,
                snapshot.OriginY,
                snapshot.OriginZ,
                snapshot.VelX,
                snapshot.VelY,
                snapshot.VelZ,
                snapshot.Pitch,
                snapshot.Yaw,
                snapshot.Roll,
                snapshot.DuckAmount,
                snapshot.DuckSpeed,
                snapshot.LadderNormalX,
                snapshot.LadderNormalY,
                snapshot.LadderNormalZ
            ],
            name);
        if (snapshot.Pad0 != 0 || snapshot.Pad1 != 0 || snapshot.Pad2 != 0)
            throw new InvalidDataException($"{name} padding must be zero");
        if (snapshot.Ducked > 1 || snapshot.Ducking > 1 || snapshot.DesiresDuck > 1)
            throw new InvalidDataException($"{name} duck state bytes must be 0 or 1");
    }

    private static void RequireFinite(ReadOnlySpan<float> values, string name)
    {
        foreach (var value in values)
        {
            if (!float.IsFinite(value))
                throw new InvalidDataException($"{name} contains a non-finite float");
        }
    }

    private static ReplayHighFidelityMetadata ReadHighFidelityMetadata(byte[] metadataJson)
    {
        var metadata = JsonSerializer.Deserialize<ReplayHighFidelityMetadata>(metadataJson, HifiJsonOptions)
            ?? ReplayHighFidelityMetadata.Empty;
        metadata.Events ??= [];
        metadata.InventorySnapshots ??= [];
        metadata.Projectiles ??= [];
        return metadata;
    }

    private static ReplayProjectileEvent[] MergeProjectileMetadata(
        ReplayProjectileEvent[] projectiles,
        ReplayHighFidelityMetadata highFidelity)
    {
        var metadata = highFidelity.Projectiles ?? [];
        if (projectiles.Length == 0 || metadata.Length == 0)
            return projectiles;

        var used = new bool[metadata.Length];
        var merged = new ReplayProjectileEvent[projectiles.Length];
        for (var i = 0; i < projectiles.Length; i++)
        {
            var projectile = projectiles[i];
            var match = -1;
            for (var j = 0; j < metadata.Length; j++)
            {
                if (used[j])
                    continue;
                if (!ProjectileMetadataMatches(projectile, metadata[j]))
                    continue;
                match = j;
                break;
            }

            if (match < 0)
            {
                merged[i] = projectile;
                continue;
            }

            used[match] = true;
            var item = metadata[match];
            merged[i] = projectile with
            {
                EffectPosition = ReadMetadataVector(item.EffectPosition),
                EffectTickIndex = ReadMetadataTickIndex(item.EffectTickIndex),
                EffectSource = item.EffectSource ?? string.Empty,
                EffectConfidence = item.EffectConfidence
            };
        }

        return merged;
    }

    private static bool ProjectileMetadataMatches(
        ReplayProjectileEvent projectile,
        ReplayProjectileMetadata metadata)
    {
        return metadata.TickIndex == projectile.TickIndex &&
               ProjectileKindFromString(metadata.Kind) == projectile.Kind &&
               metadata.WeaponDefIndex == projectile.WeaponDefIndex;
    }

    private static ReplayProjectileKind ProjectileKindFromString(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "smoke" => ReplayProjectileKind.Smoke,
            "flash" => ReplayProjectileKind.Flash,
            "he" => ReplayProjectileKind.He,
            "molotov" or "incgrenade" or "incendiary" => ReplayProjectileKind.Molotov,
            "decoy" => ReplayProjectileKind.Decoy,
            _ => ReplayProjectileKind.Unknown
        };
    }

    private static ReplayVector3 ReadMetadataVector(float[]? values)
    {
        return values is { Length: >= 3 }
            ? new ReplayVector3(values[0], values[1], values[2])
            : new ReplayVector3(0.0f, 0.0f, 0.0f);
    }

    private static int ReadMetadataTickIndex(uint? value)
        => value.HasValue && value.Value <= int.MaxValue ? (int)value.Value : -1;

    private static string ReadRecString(BinaryReader reader)
    {
        var len = reader.ReadUInt16();
        EnsureRemaining(reader, len, "string in .dtr");
        var bytes = reader.ReadBytes(len);
        if (bytes.Length != len)
            throw new EndOfStreamException("truncated string in .dtr");
        return Encoding.UTF8.GetString(bytes);
    }
}

internal readonly record struct DtrSectionHeader(
    uint SectionId,
    uint SectionVersion,
    byte Codec,
    int ElementCount,
    int UncompressedLength,
    int CompressedLength);

internal readonly record struct TickMetadata(int WeaponDefIndex, uint NumSubtick);

internal readonly record struct DtrReplayFile(
    uint Version,
    NativeReplayTick[] Ticks,
    ReplayProjectileEvent[] Projectiles,
    ReplayHighFidelityMetadata HighFidelity,
    NativeSubtickMove[] Subticks,
    NativeReplayCommandFrame[] CommandFrames,
    NativeReplayMovementExtra[] MovementExtras,
    float TickRate,
    uint PlayStartTickIndex);
