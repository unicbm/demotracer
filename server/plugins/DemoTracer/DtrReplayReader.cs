/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

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

internal static partial class DtrReplayReader
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
    private const uint SectionInputHistory = 8;
    private const uint SectionVersionV1 = 1;
    private const uint SectionVersionV2 = 2;
    private const uint CommandFieldsAll = 0xff;
    private const uint InputHistoryFieldsAll = (1U << 21) - 1;
    private const int MaxInputHistoryPerTick = 64;

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
        RepairLaggedPlayerVelocities(snapshots, tickRate);

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
        NativeReplayInputHistoryTick[]? inputHistoryTicks = null;
        NativeReplayInputHistoryEntry[]? inputHistoryEntries = null;
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
                SectionInputHistory => (
                    "input history",
                    tickCount,
                    0),
                _ => throw new InvalidDataException($"unsupported known section {header.SectionId}")
            };
            RejectDuplicate(!seenKnownSections.Add(header.SectionId), name);
            var usesV2ColumnLayout = version >= 8 &&
                header.SectionId is SectionSnapshots or SectionCommandFrames;
            if (header.SectionId == SectionInputHistory)
                RequireInputHistorySectionShape(header, tickCount);
            else
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
                case SectionInputHistory:
                    (inputHistoryTicks, inputHistoryEntries) =
                        ReadInputHistoryFromSection(body, tickCount);
                    break;
            }
        }

        if (snapshots is null)
            throw new InvalidDataException("missing required section snapshots");
        RepairLaggedPlayerVelocities(snapshots, tickRate);
        if (tickMetadata is null)
            throw new InvalidDataException("missing required section tick metadata");
        if (subticks is null)
            throw new InvalidDataException("missing required section subticks");
        if (projectileCount > 0 && projectiles is null)
            throw new InvalidDataException("missing required section projectiles");
        if (metadataJsonLength > 0 && !seenHighFidelity)
            throw new InvalidDataException("missing required section high fidelity metadata");
        if (version >= 9 && inputHistoryTicks is null)
            throw new InvalidDataException("missing required section input history");

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
            inputHistoryTicks ?? [],
            inputHistoryEntries ?? [],
            tickRate,
            (uint)playStartTickIndex);
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
    NativeReplayInputHistoryTick[] InputHistoryTicks,
    NativeReplayInputHistoryEntry[] InputHistoryEntries,
    float TickRate,
    uint PlayStartTickIndex);
