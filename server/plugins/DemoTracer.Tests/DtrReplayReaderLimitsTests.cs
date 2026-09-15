/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.IO.Compression;
using System.Text;

namespace DemoTracer.Tests;

public sealed class DtrReplayReaderLimitsTests : IDisposable
{
    private const byte CodecNone = 0;
    private const byte CodecBrotli = 1;
    private readonly string tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"demotracer-reader-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(0U, 2U, 0U, 1U, true)]
    [InlineData(0U, 2U, 0U, 0U, true)]
    [InlineData(0U, 2U, 1U, 0U, false)]
    [InlineData(0U, 2U, 0x7fc00000U, 1U, false)]
    [InlineData(0U, 5U, 2U, 1U, false)]
    [InlineData(1U, 2U, 0U, 1U, false)]
    [InlineData(0U, 999U, 0U, 1U, false)]
    public void SourceStateValidatesPresenceTypeAndTick(uint tick, uint field, uint bits, uint present, bool valid)
    {
        var path = WriteFile(writer =>
        {
            var snapshots = BuildV2SnapshotPayload([new NativeMovementSnapshot(), new NativeMovementSnapshot()]);
            WriteCompleteHeader(writer, 11, 1, 0);
            writer.Write(5U);
            WriteSection(writer, 1, CodecNone, 2, snapshots, sectionVersion: 2);
            WriteSection(writer, 2, CodecNone, 1, new byte[8]);
            WriteSection(writer, 5, CodecNone, 0, []);
            var history = new byte[16];
            BitConverter.GetBytes(-1).CopyTo(history, 4); BitConverter.GetBytes(-1).CopyTo(history, 8);
            WriteSection(writer, 8, CodecNone, 1, history);
            using var body = new MemoryStream();
            using var payload = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true);
            payload.Write(tick); payload.Write(field); payload.Write(bits); payload.Write(present); payload.Flush();
            WriteSection(writer, 9, CodecNone, 1, body.ToArray());
        });
        if (!valid) { Assert.Contains("source state", Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path)).Message); return; }
        var change = Assert.Single(DtrReplayReader.Read(path).SourceState);
        Assert.Equal(bits, change.ValueBits); Assert.Equal(present, change.Present);
    }

    [Fact]
    public void DefaultLimitsAreGenerousButFinite()
    {
        var limits = DtrReadLimits.Default;

        Assert.Equal(64L * 1024 * 1024, limits.MaxFileBytes);
        Assert.Equal(32, limits.MaxSectionCount);
        Assert.Equal(48L * 1024 * 1024, limits.MaxCompressedSectionBytes);
        Assert.Equal(64L * 1024 * 1024, limits.MaxTotalCompressedBytes);
        Assert.Equal(48L * 1024 * 1024, limits.MaxDecodedSectionBytes);
        Assert.Equal(64L * 1024 * 1024, limits.MaxTotalDecodedBytes);
        Assert.Equal(32_768, limits.MaxTickCount);
        Assert.Equal(1_179_648, limits.MaxSubtickCount);
        Assert.Equal(36, limits.MaxSubticksPerTick);
        Assert.Equal(4_096, limits.MaxProjectileCount);
        Assert.Equal(8 * 1024 * 1024, limits.MaxMetadataJsonBytes);
    }

    [Fact]
    public void RejectsFileBeforeParsingWhenItExceedsLimit()
    {
        var path = WriteFile(writer => writer.Write(new byte[32]));
        var limits = DtrReadLimits.Default with { MaxFileBytes = 16 };

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path, limits));

        Assert.Contains("file length", error.Message);
    }

    [Fact]
    public void RejectsTickCountBeforeReadingTheRestOfTheHeader()
    {
        var path = WriteFile(writer => WriteHeaderPrefix(writer, version: 7, tickCount: 2, subtickCount: 0));
        var limits = DtrReadLimits.Default with { MaxTickCount = 1 };

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path, limits));

        Assert.Contains("tick_count 2 exceeds limit 1", error.Message);
    }

    [Fact]
    public void RejectsImpossibleHeaderSubtickRatio()
    {
        var path = WriteFile(writer => WriteHeaderPrefix(writer, version: 3, tickCount: 1, subtickCount: 37));

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("exceeds 36 per tick", error.Message);
    }

    [Fact]
    public void RejectsProjectileAndMetadataCountsAtTheirLimits()
    {
        var projectilePath = WriteFile(writer =>
            WriteHeaderPrefix(writer, version: 4, tickCount: 0, subtickCount: 0, projectileCount: 2));
        var metadataPath = WriteFile(writer =>
            WriteHeaderPrefix(writer, version: 6, tickCount: 0, subtickCount: 0, metadataJsonLength: 2));

        var projectileError = Assert.Throws<InvalidDataException>(() =>
            DtrReplayReader.Read(
                projectilePath,
                DtrReadLimits.Default with { MaxProjectileCount = 1 }));
        var metadataError = Assert.Throws<InvalidDataException>(() =>
            DtrReplayReader.Read(
                metadataPath,
                DtrReadLimits.Default with { MaxMetadataJsonBytes = 1 }));

        Assert.Contains("projectile_count 2 exceeds limit 1", projectileError.Message);
        Assert.Contains("metadata_json_len 2 exceeds limit 1", metadataError.Message);
    }

    [Fact]
    public void RejectsTruncatedDeclaredStringBeforeReadingItsPayload()
    {
        var path = WriteFile(writer =>
        {
            WriteHeaderPrefix(writer, version: 3, tickCount: 0, subtickCount: 0);
            writer.Write(ushort.MaxValue);
        });

        var error = Assert.Throws<EndOfStreamException>(() => DtrReplayReader.Read(path));

        Assert.Contains("string in .dtr", error.Message);
    }

    [Fact]
    public void RejectsLegacyBodyBudgetsBeforeReadingPayload()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 6, tickCount: 0, subtickCount: 0);
            writer.Write(CodecBrotli);
            writer.Write(0UL);
            writer.Write(11UL);
        });
        var limits = DtrReadLimits.Default with { MaxCompressedSectionBytes = 10 };

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path, limits));

        Assert.Contains("body_compressed_len 11 exceeds limit 10", error.Message);
    }

    [Fact]
    public void RejectsLegacyDecodedBodyBudgetBeforeCheckingItsShape()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 6, tickCount: 0, subtickCount: 0);
            writer.Write(CodecBrotli);
            writer.Write(11UL);
            writer.Write(0UL);
        });
        var limits = DtrReadLimits.Default with { MaxDecodedSectionBytes = 10 };

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path, limits));

        Assert.Contains("body_uncompressed_len 11 exceeds limit 10", error.Message);
    }

    [Theory]
    [InlineData(3U)]
    [InlineData(5U)]
    public void KeepsLegacyVersionsReadable(uint version)
    {
        var compressed = Compress([]);
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version, tickCount: 0, subtickCount: 0);
            writer.Write(CodecBrotli);
            writer.Write(0UL);
            writer.Write((ulong)compressed.Length);
            writer.Write(compressed);
        });

        var replay = DtrReplayReader.Read(path);

        Assert.Equal(version, replay.Version);
        Assert.Empty(replay.Ticks);
        Assert.Empty(replay.Subticks);
    }

    [Fact]
    public void ReadsV8ColumnDeltaSectionsBitExactly()
    {
        var pre = new NativeMovementSnapshot
        {
            OriginX = -0.0f,
            OriginY = 2.0f,
            OriginZ = 3.0f,
            VelX = 4.0f,
            VelY = 5.0f,
            VelZ = 6.0f,
            Pitch = 7.0f,
            Yaw = 8.0f,
            Roll = -0.0f,
            EntityFlags = 9,
            MoveType = 10,
            Buttons = 11,
            Buttons1 = 12,
            Buttons2 = 13,
            DuckAmount = 0.25f,
            DuckSpeed = 8.0f,
            LadderNormalX = -0.0f,
            LadderNormalY = 0.0f,
            LadderNormalZ = 1.0f,
            Ducked = 1,
            Ducking = 1,
            DesiresDuck = 1,
            ActualMoveType = 10
        };
        var post = pre;
        post.OriginX = 1.5f;
        post.Yaw = -179.5f;
        post.Buttons = ulong.MaxValue;
        post.Ducked = 0;
        var command = new NativeReplayCommandFrame
        {
            ForwardMove = 123.5f,
            LeftMove = -45.25f,
            UpMove = -0.0f,
            Pitch = 17.0f,
            Yaw = -91.0f,
            Roll = -0.0f,
            Buttons = ulong.MaxValue,
            Buttons1 = 2,
            Buttons2 = 3,
            MouseDx = int.MinValue,
            MouseDy = int.MaxValue,
            WeaponSelect = -1,
            Fields = 0xff,
            LeftHandDesired = 1
        };
        var snapshotPayload = BuildV2SnapshotPayload([pre, post]);
        var commandPayload = BuildV2CommandPayload([command]);
        var tickMetadata = new byte[8];
        BitConverter.GetBytes(42).CopyTo(tickMetadata, 0);
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 8, tickCount: 1, subtickCount: 0);
            writer.Write(4U);
            WriteSection(
                writer,
                sectionId: 1,
                codec: CodecNone,
                elementCount: 2,
                payload: snapshotPayload,
                sectionVersion: 2);
            WriteSection(writer, sectionId: 2, codec: CodecNone, elementCount: 1, payload: tickMetadata);
            WriteSection(writer, sectionId: 5, codec: CodecNone, elementCount: 0, payload: []);
            WriteSection(
                writer,
                sectionId: 6,
                codec: CodecNone,
                elementCount: 1,
                payload: commandPayload,
                sectionVersion: 2);
        });

        var replay = DtrReplayReader.Read(path);

        Assert.Equal(8U, replay.Version);
        Assert.Single(replay.Ticks);
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(pre.OriginX),
            BitConverter.SingleToUInt32Bits(replay.Ticks[0].Pre.OriginX));
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(post.OriginX),
            BitConverter.SingleToUInt32Bits(replay.Ticks[0].Post.OriginX));
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(post.Yaw),
            BitConverter.SingleToUInt32Bits(replay.Ticks[0].Post.Yaw));
        Assert.Equal(post.Buttons, replay.Ticks[0].Post.Buttons);
        Assert.Equal(post.Ducked, replay.Ticks[0].Post.Ducked);
        Assert.Equal(42, replay.Ticks[0].WeaponDefIndex);
        var decodedCommand = Assert.Single(replay.CommandFrames);
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(command.UpMove),
            BitConverter.SingleToUInt32Bits(decodedCommand.UpMove));
        Assert.Equal(command.Buttons, decodedCommand.Buttons);
        Assert.Equal(command.MouseDx, decodedCommand.MouseDx);
        Assert.Equal(command.MouseDy, decodedCommand.MouseDy);
        Assert.Equal(command.WeaponSelect, decodedCommand.WeaponSelect);
        Assert.Equal(command.Fields, decodedCommand.Fields);
        Assert.Equal(command.LeftHandDesired, decodedCommand.LeftHandDesired);
    }

    [Fact]
    public void ReadsV9InputHistoryWithAttackIndexes()
    {
        var snapshots = BuildV2SnapshotPayload([new NativeMovementSnapshot(), new NativeMovementSnapshot()]);
        byte[] tickMetadata;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(-1);
            writer.Write(0U);
            writer.Flush();
            tickMetadata = stream.ToArray();
        }
        byte[] inputHistory;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(100); // source client tick
            writer.Write(0);   // attack1 history index
            writer.Write(-1);  // attack2 history index
            writer.Write(1U);
            writer.Write((1U << 1) | (1U << 2) | (1U << 16));
            writer.Write(0.0f); writer.Write(0.0f); writer.Write(0.0f);
            writer.Write(99); writer.Write(0.75f);
            writer.Write(0); writer.Write(0.0f); writer.Write(0.0f);
            for (var i = 0; i < 3; i++)
            {
                writer.Write(-1); writer.Write(-1); writer.Write(0.0f);
            }
            writer.Write(0); writer.Write(123);
            for (var i = 0; i < 12; i++)
                writer.Write(0.0f);
            writer.Flush();
            inputHistory = stream.ToArray();
        }
        Assert.Equal(144, inputHistory.Length);

        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 9, tickCount: 1, subtickCount: 0);
            writer.Write(4U);
            WriteSection(writer, 1, CodecNone, 2, snapshots, sectionVersion: 2);
            WriteSection(writer, 2, CodecNone, 1, tickMetadata);
            WriteSection(writer, 5, CodecNone, 0, []);
            WriteSection(writer, 8, CodecNone, 1, inputHistory);
        });

        var replay = DtrReplayReader.Read(path);

        var tick = Assert.Single(replay.InputHistoryTicks);
        Assert.Equal(100, tick.SourceClientTick);
        Assert.Equal(0, tick.Attack1StartHistoryIndex);
        Assert.Equal(-1, tick.Attack2StartHistoryIndex);
        var entry = Assert.Single(replay.InputHistoryEntries);
        Assert.Equal(99, entry.RenderTickCount);
        Assert.Equal(0.75f, entry.RenderTickFraction);
        Assert.Equal(123, entry.TargetEntIndex);
    }

    [Fact]
    public void ReadsV10BackdatedSubtickWhenBitExactly()
    {
        const float when = -1.671875f;
        var path = WriteFile(writer => WriteSingleSubtickReplay(writer, version: 10, when));

        var replay = DtrReplayReader.Read(path);

        Assert.Equal(10U, replay.Version);
        var subtick = Assert.Single(replay.Subticks);
        Assert.Equal(BitConverter.SingleToUInt32Bits(when), BitConverter.SingleToUInt32Bits(subtick.When));
    }

    [Fact]
    public void RejectsBackdatedSubtickWhenBeforeV10()
    {
        var path = WriteFile(writer =>
            WriteSingleSubtickReplay(writer, version: 9, when: -1.0f / 128.0f));

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("when must be finite and in [0, 1)", error.Message);
    }

    [Fact]
    public void ClearsImpossibleSharedSpawnTransitionVelocity()
    {
        var before = new NativeMovementSnapshot
        {
            OriginX = 128.0f,
            OriginY = -64.0f,
            OriginZ = 32.0f,
            VelX = 10.0f,
            VelY = 20.0f,
            VelZ = 30.0f
        };
        var artifact = before;
        artifact.VelX = 126_715.7f;
        artifact.VelY = 91_870.14f;
        artifact.VelZ = 256.0f;
        var after = before;
        after.OriginX = 129.0f;
        after.VelX = 0.0f;
        after.VelY = 0.0f;
        after.VelZ = 0.0f;

        var snapshotPayload = BuildV2SnapshotPayload([before, artifact, after]);
        var tickMetadata = new byte[16];
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 8, tickCount: 2, subtickCount: 0);
            writer.Write(3U);
            WriteSection(
                writer,
                sectionId: 1,
                codec: CodecNone,
                elementCount: 3,
                payload: snapshotPayload,
                sectionVersion: 2);
            WriteSection(writer, sectionId: 2, codec: CodecNone, elementCount: 2, payload: tickMetadata);
            WriteSection(writer, sectionId: 5, codec: CodecNone, elementCount: 0, payload: []);
        });

        var replay = DtrReplayReader.Read(path);

        Assert.Equal(0.0f, replay.Ticks[0].Pre.VelX);
        Assert.Equal(0.0f, replay.Ticks[0].Post.VelX);
        Assert.Equal(0.0f, replay.Ticks[0].Post.VelY);
        Assert.Equal(0.0f, replay.Ticks[0].Post.VelZ);
        Assert.Equal(0.0f, replay.Ticks[1].Pre.VelX);
        Assert.Equal(0.0f, replay.Ticks[1].Pre.VelY);
        Assert.Equal(0.0f, replay.Ticks[1].Pre.VelZ);
        Assert.Equal(128.0f, replay.Ticks[1].Post.VelX);
    }

    [Fact]
    public void ReadsRoundStartBalanceFromHighFidelitySchemaFour()
    {
        var metadata = Encoding.UTF8.GetBytes(
            """{"schema_version":4,"round_start_balance":5250,"events":[],"inventory_snapshots":[],"projectiles":[]}""");
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(
                writer,
                version: 7,
                tickCount: 0,
                subtickCount: 0,
                metadataJsonLength: (uint)metadata.Length);
            writer.Write(4U);
            WriteSection(writer, sectionId: 1, codec: CodecNone, elementCount: 0, payload: []);
            WriteSection(writer, sectionId: 2, codec: CodecNone, elementCount: 0, payload: []);
            WriteSection(writer, sectionId: 4, codec: CodecNone, elementCount: 1, payload: metadata);
            WriteSection(writer, sectionId: 5, codec: CodecNone, elementCount: 0, payload: []);
        });

        var replay = DtrReplayReader.Read(path);

        Assert.Equal(4, replay.HighFidelity.SchemaVersion);
        Assert.Equal(5_250U, replay.HighFidelity.RoundStartBalance);
    }

    [Fact]
    public void RejectsTopLevelTrailingBytes()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 0, subtickCount: 0);
            writer.Write(3U);
            WriteSection(writer, sectionId: 1, codec: CodecNone, elementCount: 0, payload: []);
            WriteSection(writer, sectionId: 2, codec: CodecNone, elementCount: 0, payload: []);
            WriteSection(writer, sectionId: 5, codec: CodecNone, elementCount: 0, payload: []);
            writer.Write((byte)0x42);
        });

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("trailing bytes after top-level .dtr payload", error.Message);
    }

    private static byte[] CompactSourceFixture() => new uint[] {
        0, 0, 1, 2, 1, 1, // tick deltas
        0x281, 0x82, 0x82, 1, 0x181, 2, // descriptors
    }.SelectMany(BitConverter.GetBytes).Concat(new byte[] {
        0xfe, 0xfe, 100, 0xff, 0xff, 0, 0xff, 0xff, 0, 0xff, 0xff, 0, // PlayerTick XOR planes
        0, 0, 0, 0, 0, 0, 0, 0x80, 0x80, 0x80, 0xbf, 0x3f // DuckRoot XOR planes
    }).ToArray();

    private string WriteSourceStateFile(byte[] body, int count = 9, uint version = 12, int ticks = 8,
        byte codec = CodecNone, int? uncompressedLength = null)
        => WriteFile(writer => {
            WriteCompleteHeader(writer, version, (uint)ticks, 0);
            writer.Write(5U);
            WriteSection(writer, 1, CodecNone, ticks + 1,
                BuildV2SnapshotPayload(new NativeMovementSnapshot[ticks + 1]), sectionVersion: 2);
            WriteSection(writer, 2, CodecNone, ticks, new byte[ticks * 8]);
            WriteSection(writer, 5, CodecNone, 0, []);
            var history = new byte[ticks * 16];
            for (var i = 0; i < ticks; ++i) {
                BitConverter.GetBytes(-1).CopyTo(history, i * 16 + 4);
                BitConverter.GetBytes(-1).CopyTo(history, i * 16 + 8);
            }
            WriteSection(writer, 8, CodecNone, ticks, history);
            WriteSection(writer, 9, codec, count, body, uncompressedLength, sectionVersion: 2);
        });

    [Fact]
    public void CompactSourcePreservesClockRunsWithoutExpandingNativeArray()
    {
        var replay = DtrReplayReader.Read(WriteSourceStateFile(CompactSourceFixture()));
        Assert.Equal(6, replay.SourceState.Length);
        Assert.Equal(5U, replay.SourceState[0].Present); // three ticks, including u32 wrap
        Assert.Equal(0xfffffffeU, replay.SourceState[0].ValueBits);
        Assert.Equal(0x80000000U, replay.SourceState[1].ValueBits); // negative zero
        Assert.Equal(0x3f800000U, replay.SourceState[2].ValueBits);
        Assert.Equal(0U, replay.SourceState[3].Present);
        Assert.Equal(100U, replay.SourceState[4].ValueBits);
        Assert.Equal(3U, replay.SourceState[4].Present);
        Assert.Equal(0U, replay.SourceState[5].Present);
        Assert.Equal(0U, replay.SourceState[5].ValueBits);
    }

    [Theory]
    [InlineData(0, 0xffffffffU)] // tick overflow/out of range
    [InlineData(3, 1U)] // next clock starts inside previous run
    [InlineData(6, 0x282U)] // non-clock run
    [InlineData(6, 0x201U)] // absent run
    [InlineData(6, 0x881U)] // run beyond replay
    [InlineData(7, 0xffU)] // unknown field
    public void CompactSourceRejectsMalformedEvidence(int word, uint value)
    {
        var body = CompactSourceFixture();
        BitConverter.GetBytes(value).CopyTo(body, word * 4);
        Assert.Contains("source state", Assert.Throws<InvalidDataException>(() =>
            DtrReplayReader.Read(WriteSourceStateFile(body))).Message);
    }

    [Theory]
    [InlineData(0x82U, 0x7fc00000U)]
    [InlineData(2U, 1U)]
    [InlineData(0x85U, 2U)]
    public void CompactSourceRejectsInvalidTypedValues(uint descriptor, uint bits)
    {
        var body = new uint[] { 0, descriptor, bits }.SelectMany(BitConverter.GetBytes).ToArray();
        Assert.Contains("source state", Assert.Throws<InvalidDataException>(() =>
            DtrReplayReader.Read(WriteSourceStateFile(body, 1))).Message);
    }

    [Theory]
    [InlineData(8, 12U, 0)]
    [InlineData(10, 12U, 0)]
    [InlineData(9, 11U, 0)]
    [InlineData(9, 12U, 1)]
    public void CompactSourceRejectsCountVersionAndLengthMismatch(int count, uint version, int extraBytes)
    {
        var body = CompactSourceFixture().Concat(new byte[extraBytes]).ToArray();
        Assert.Contains("source state", Assert.Throws<InvalidDataException>(() =>
            DtrReplayReader.Read(WriteSourceStateFile(body, count, version))).Message);
    }

    [Fact]
    public void CompactSourceAcceptsEmptySection()
    {
        Assert.Empty(DtrReplayReader.Read(WriteSourceStateFile([], 0)).SourceState);
    }

    [Fact]
    public void ZstdSourceMatchesUncompressedSource()
    {
        var body = CompactSourceFixture();
        using var compressor = new ZstdSharp.Compressor(9);
        var packed = compressor.Wrap(body).ToArray();
        var raw = DtrReplayReader.Read(WriteSourceStateFile(body));
        var compressed = DtrReplayReader.Read(WriteSourceStateFile(packed, codec: 2, uncompressedLength: body.Length));
        Assert.Equal(raw.SourceState, compressed.SourceState);
    }

    [Fact]
    public void ZstdCodecRequiresV12EvenForExistingSectionLayouts()
    {
        var path = WriteFile(writer => {
            WriteCompleteHeader(writer, 11, 1, 0);
            writer.Write(1U);
            var body = BuildV2SnapshotPayload(new NativeMovementSnapshot[2]);
            using var compressor = new ZstdSharp.Compressor(9);
            WriteSection(writer, 1, 2, 2, compressor.Wrap(body).ToArray(), body.Length, sectionVersion: 2);
        });
        Assert.Contains("codec", Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path)).Message);
    }

    [Theory]
    [InlineData(-1)] // truncated frame
    [InlineData(1)] // trailing garbage
    public void ZstdSourceRejectsInvalidFrame(int lengthChange)
    {
        using var compressor = new ZstdSharp.Compressor(9);
        var body = CompactSourceFixture();
        var packed = compressor.Wrap(body).ToArray();
        Array.Resize(ref packed, packed.Length + lengthChange);
        Assert.Contains("zstd", Assert.Throws<InvalidDataException>(() =>
            DtrReplayReader.Read(WriteSourceStateFile(packed, codec: 2, uncompressedLength: body.Length))).Message);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(84)]
    public void ZstdSourceCannotExceedOrUnderrunDeclaredOutput(int declaredLength)
    {
        using var compressor = new ZstdSharp.Compressor(9);
        var packed = compressor.Wrap(CompactSourceFixture()).ToArray();
        Assert.Contains("zstd", Assert.Throws<InvalidDataException>(() =>
            DtrReplayReader.Read(WriteSourceStateFile(packed, codec: 2, uncompressedLength: declaredLength))).Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"tick_index\":0,\"steam_id\":1,\"weapon_def_counts\":null}")]
    [InlineData("{\"tick_index\":1,\"steam_id\":1,\"weapon_def_counts\":[]}")]
    [InlineData("{\"tick_index\":0,\"steam_id\":1,\"weapon_def_counts\":[{\"weapon_def_index\":7,\"count\":2147483647}]}")]
    [InlineData("{\"tick_index\":0,\"steam_id\":1,\"weapon_def_counts\":[{\"weapon_def_index\":7,\"count\":1},{\"weapon_def_index\":7,\"count\":1}]}")]
    public void RejectsUnsafeInventoryAcquisitionsBeforePlayback(string snapshot)
    {
        var metadata = Encoding.UTF8.GetBytes("{\"inventory_snapshots\":[" + snapshot + "]}");
        var snapshots = BuildV2SnapshotPayload([new NativeMovementSnapshot(), new NativeMovementSnapshot()]);
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 8, tickCount: 1, subtickCount: 0,
                metadataJsonLength: (uint)metadata.Length);
            writer.Write(4U);
            WriteSection(writer, 1, CodecNone, 2, snapshots, sectionVersion: 2);
            WriteSection(writer, 2, CodecNone, 1, new byte[8]);
            WriteSection(writer, 4, CodecNone, 1, metadata);
            WriteSection(writer, 5, CodecNone, 0, []);
        });
        Assert.Contains("inventory", Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path)).Message);
    }

    [Fact]
    public void RejectsNonFiniteSnapshotValues()
    {
        var invalid = new NativeMovementSnapshot { OriginX = float.NaN };
        var snapshots = BuildV2SnapshotPayload([invalid, new NativeMovementSnapshot()]);
        var tickMetadata = new byte[8];
        BitConverter.GetBytes(-1).CopyTo(tickMetadata, 0);
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 8, tickCount: 1, subtickCount: 0);
            writer.Write(3U);
            WriteSection(writer, 1, CodecNone, 2, snapshots, sectionVersion: 2);
            WriteSection(writer, 2, CodecNone, 1, tickMetadata);
            WriteSection(writer, 5, CodecNone, 0, []);
        });

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("contains a non-finite float", error.Message);
    }

    [Fact]
    public void RejectsUnknownCommandFields()
    {
        var snapshots = BuildV2SnapshotPayload(
            [new NativeMovementSnapshot(), new NativeMovementSnapshot()]);
        var tickMetadata = new byte[8];
        BitConverter.GetBytes(-1).CopyTo(tickMetadata, 0);
        var commands = BuildV2CommandPayload(
            [new NativeReplayCommandFrame { WeaponSelect = -1, Fields = 1U << 31 }]);
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 8, tickCount: 1, subtickCount: 0);
            writer.Write(4U);
            WriteSection(writer, 1, CodecNone, 2, snapshots, sectionVersion: 2);
            WriteSection(writer, 2, CodecNone, 1, tickMetadata);
            WriteSection(writer, 5, CodecNone, 0, []);
            WriteSection(writer, 6, CodecNone, 1, commands, sectionVersion: 2);
        });

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("unknown fields 0x80000000", error.Message);
    }

    [Fact]
    public void RejectsV7SectionCountBeforeReadingSectionHeaders()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 0, subtickCount: 0);
            writer.Write(2U);
        });
        var limits = DtrReadLimits.Default with { MaxSectionCount = 1 };

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path, limits));

        Assert.Contains("section_count 2 exceeds limit 1", error.Message);
    }

    [Fact]
    public void RejectsKnownSectionShapeBeforeMissingPayload()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 0, subtickCount: 0);
            writer.Write(1U);
            WriteSectionHeader(
                writer,
                sectionId: 1,
                codec: CodecBrotli,
                elementCount: 1,
                uncompressedLength: 0,
                compressedLength: 10);
        });

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("snapshots section count 1 != expected 0", error.Message);
    }

    [Fact]
    public void ReservesBytesForRemainingV7HeadersBeforeReadingPayload()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 0, subtickCount: 0);
            writer.Write(2U);
            WriteSectionHeader(
                writer,
                sectionId: 99,
                codec: CodecNone,
                elementCount: 0,
                uncompressedLength: 4,
                compressedLength: 4);
            writer.Write(new byte[4]);
        });

        var error = Assert.Throws<EndOfStreamException>(() => DtrReplayReader.Read(path));

        Assert.Contains("section payload and remaining headers", error.Message);
    }

    [Fact]
    public void EnforcesCumulativeV7SectionBudgets()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 0, subtickCount: 0);
            writer.Write(2U);
            WriteSection(writer, sectionId: 99, codec: CodecNone, elementCount: 0, payload: new byte[5]);
            WriteSectionHeader(
                writer,
                sectionId: 100,
                codec: CodecNone,
                elementCount: 0,
                uncompressedLength: 5,
                compressedLength: 5);
        });
        var limits = DtrReadLimits.Default with
        {
            MaxTotalCompressedBytes = 9,
            MaxTotalDecodedBytes = 9
        };

        var decodedError = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path, limits));
        var compressedError = Assert.Throws<InvalidDataException>(() =>
            DtrReplayReader.Read(
                path,
                limits with
                {
                    MaxTotalCompressedBytes = 9,
                    MaxTotalDecodedBytes = 100
                }));

        Assert.Contains("total section decoded bytes exceeds limit 9", decodedError.Message);
        Assert.Contains("total section compressed bytes exceeds limit 9", compressedError.Message);
    }

    [Fact]
    public void SkipsLargeUnknownSectionAndReadsRequiredEmptySections()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 0, subtickCount: 0);
            writer.Write(4U);
            WriteSection(
                writer,
                sectionId: 99,
                codec: 255,
                elementCount: int.MaxValue,
                payload: new byte[5_000],
                uncompressedLength: 0);
            WriteSection(writer, sectionId: 1, codec: CodecNone, elementCount: 0, payload: []);
            WriteSection(writer, sectionId: 2, codec: CodecNone, elementCount: 0, payload: []);
            WriteSection(writer, sectionId: 5, codec: CodecNone, elementCount: 0, payload: []);
        });

        var replay = DtrReplayReader.Read(path);

        Assert.Empty(replay.Ticks);
        Assert.Empty(replay.Subticks);
    }

    [Fact]
    public void RejectsBrotliOutputBeyondDeclaredSectionLength()
    {
        var compressed = Compress([42]);
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 0, subtickCount: 0);
            writer.Write(1U);
            WriteSection(
                writer,
                sectionId: 1,
                codec: CodecBrotli,
                elementCount: 0,
                payload: compressed,
                uncompressedLength: 0);
        });

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("exceeds expected length 0", error.Message);
    }

    [Fact]
    public void RejectsMoreThanThirtySixSubticksInTickMetadata()
    {
        var tickMetadata = new byte[8];
        BitConverter.GetBytes(37U).CopyTo(tickMetadata, 4);
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 7, tickCount: 1, subtickCount: 36);
            writer.Write(1U);
            WriteSection(
                writer,
                sectionId: 2,
                codec: CodecNone,
                elementCount: 1,
                payload: tickMetadata);
        });

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("tick subtick count 37 exceeds limit 36", error.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, recursive: true);
    }

    private string WriteFile(Action<BinaryWriter> write)
    {
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.dtr");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        write(writer);
        return path;
    }

    private static void WriteCompleteHeader(
        BinaryWriter writer,
        uint version,
        uint tickCount,
        uint subtickCount,
        uint projectileCount = 0,
        uint metadataJsonLength = 0)
    {
        WriteHeaderPrefix(writer, version, tickCount, subtickCount, projectileCount, metadataJsonLength);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
    }

    private static void WriteSingleSubtickReplay(BinaryWriter writer, uint version, float when)
    {
        var snapshots = BuildV2SnapshotPayload(
            [new NativeMovementSnapshot(), new NativeMovementSnapshot()]);
        byte[] tickMetadata;
        using (var stream = new MemoryStream())
        using (var payload = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            payload.Write(-1);
            payload.Write(1U);
            payload.Flush();
            tickMetadata = stream.ToArray();
        }
        byte[] subticks;
        using (var stream = new MemoryStream())
        using (var payload = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            payload.Write(when);
            payload.Write(8U);
            payload.Write(1.0f);
            payload.Write(0.0f);
            payload.Write(0.0f);
            payload.Write(0.0f);
            payload.Write(0.0f);
            payload.Flush();
            subticks = stream.ToArray();
        }
        byte[] inputHistory;
        using (var stream = new MemoryStream())
        using (var payload = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            payload.Write(100);
            payload.Write(-1);
            payload.Write(-1);
            payload.Write(0U);
            payload.Flush();
            inputHistory = stream.ToArray();
        }

        WriteCompleteHeader(writer, version, tickCount: 1, subtickCount: 1);
        writer.Write(4U);
        WriteSection(writer, 1, CodecNone, 2, snapshots, sectionVersion: 2);
        WriteSection(writer, 2, CodecNone, 1, tickMetadata);
        WriteSection(writer, 5, CodecNone, 1, subticks);
        WriteSection(writer, 8, CodecNone, 1, inputHistory);
    }

    private static void WriteHeaderPrefix(
        BinaryWriter writer,
        uint version,
        uint tickCount,
        uint subtickCount,
        uint projectileCount = 0,
        uint metadataJsonLength = 0)
    {
        writer.Write("CSDTRREC"u8);
        writer.Write(version);
        writer.Write(128.0f);
        writer.Write(1U);
        writer.Write((byte)0);
        writer.Write(0U);
        writer.Write(0UL);
        writer.Write(tickCount);
        writer.Write(subtickCount);
        if (version >= 4)
            writer.Write(projectileCount);
        if (version >= 5)
            writer.Write(0U);
        if (version >= 6)
            writer.Write(metadataJsonLength);
    }

    private static void WriteSection(
        BinaryWriter writer,
        uint sectionId,
        byte codec,
        int elementCount,
        byte[] payload,
        int? uncompressedLength = null,
        uint sectionVersion = 1)
    {
        WriteSectionHeader(
            writer,
            sectionId,
            codec,
            elementCount,
            uncompressedLength ?? payload.Length,
            payload.Length,
            sectionVersion);
        writer.Write(payload);
    }

    private static void WriteSectionHeader(
        BinaryWriter writer,
        uint sectionId,
        byte codec,
        int elementCount,
        int uncompressedLength,
        int compressedLength,
        uint sectionVersion = 1)
    {
        writer.Write(sectionId);
        writer.Write(sectionVersion);
        writer.Write(codec);
        writer.Write((byte)0);
        writer.Write((ushort)0);
        writer.Write(0U);
        writer.Write((uint)elementCount);
        writer.Write((ulong)uncompressedLength);
        writer.Write((ulong)compressedLength);
    }

    private static byte[] BuildV2SnapshotPayload(NativeMovementSnapshot[] snapshots)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        WriteDeltaUInt32Column(writer, snapshots.Select(value => BitConverter.SingleToUInt32Bits(value.OriginX)));
        WriteDeltaUInt32Column(writer, snapshots.Select(value => BitConverter.SingleToUInt32Bits(value.OriginY)));
        WriteDeltaUInt32Column(writer, snapshots.Select(value => BitConverter.SingleToUInt32Bits(value.OriginZ)));
        WriteDeltaUInt32Column(writer, snapshots.Select(value => BitConverter.SingleToUInt32Bits(value.VelX)));
        WriteDeltaUInt32Column(writer, snapshots.Select(value => BitConverter.SingleToUInt32Bits(value.VelY)));
        WriteDeltaUInt32Column(writer, snapshots.Select(value => BitConverter.SingleToUInt32Bits(value.VelZ)));
        WriteDeltaUInt32Column(writer, snapshots.Select(value => BitConverter.SingleToUInt32Bits(value.Pitch)));
        WriteDeltaUInt32Column(writer, snapshots.Select(value => BitConverter.SingleToUInt32Bits(value.Yaw)));
        WriteDeltaUInt32Column(writer, snapshots.Select(value => BitConverter.SingleToUInt32Bits(value.Roll)));
        WriteDeltaUInt32Column(writer, snapshots.Select(value => value.EntityFlags));
        WriteDeltaByteColumn(writer, snapshots.Select(value => value.MoveType));
        WriteDeltaUInt64Column(writer, snapshots.Select(value => value.Buttons));
        WriteDeltaUInt64Column(writer, snapshots.Select(value => value.Buttons1));
        WriteDeltaUInt64Column(writer, snapshots.Select(value => value.Buttons2));
        WriteDeltaUInt32Column(writer, snapshots.Select(value => BitConverter.SingleToUInt32Bits(value.DuckAmount)));
        WriteDeltaUInt32Column(writer, snapshots.Select(value => BitConverter.SingleToUInt32Bits(value.DuckSpeed)));
        WriteDeltaUInt32Column(writer, snapshots.Select(value => BitConverter.SingleToUInt32Bits(value.LadderNormalX)));
        WriteDeltaUInt32Column(writer, snapshots.Select(value => BitConverter.SingleToUInt32Bits(value.LadderNormalY)));
        WriteDeltaUInt32Column(writer, snapshots.Select(value => BitConverter.SingleToUInt32Bits(value.LadderNormalZ)));
        WriteDeltaByteColumn(writer, snapshots.Select(value => value.Ducked));
        WriteDeltaByteColumn(writer, snapshots.Select(value => value.Ducking));
        WriteDeltaByteColumn(writer, snapshots.Select(value => value.DesiresDuck));
        WriteDeltaByteColumn(writer, snapshots.Select(value => value.ActualMoveType));
        writer.Flush();
        return output.ToArray();
    }

    private static byte[] BuildV2CommandPayload(NativeReplayCommandFrame[] frames)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        WriteDeltaUInt32Column(writer, frames.Select(value => BitConverter.SingleToUInt32Bits(value.ForwardMove)));
        WriteDeltaUInt32Column(writer, frames.Select(value => BitConverter.SingleToUInt32Bits(value.LeftMove)));
        WriteDeltaUInt32Column(writer, frames.Select(value => BitConverter.SingleToUInt32Bits(value.UpMove)));
        WriteDeltaUInt32Column(writer, frames.Select(value => BitConverter.SingleToUInt32Bits(value.Pitch)));
        WriteDeltaUInt32Column(writer, frames.Select(value => BitConverter.SingleToUInt32Bits(value.Yaw)));
        WriteDeltaUInt32Column(writer, frames.Select(value => BitConverter.SingleToUInt32Bits(value.Roll)));
        WriteDeltaUInt64Column(writer, frames.Select(value => value.Buttons));
        WriteDeltaUInt64Column(writer, frames.Select(value => value.Buttons1));
        WriteDeltaUInt64Column(writer, frames.Select(value => value.Buttons2));
        WriteDeltaUInt32Column(writer, frames.Select(value => unchecked((uint)value.MouseDx)));
        WriteDeltaUInt32Column(writer, frames.Select(value => unchecked((uint)value.MouseDy)));
        WriteDeltaUInt32Column(writer, frames.Select(value => unchecked((uint)value.WeaponSelect)));
        WriteDeltaUInt32Column(writer, frames.Select(value => value.Fields));
        WriteDeltaByteColumn(writer, frames.Select(value => value.LeftHandDesired));
        writer.Flush();
        return output.ToArray();
    }

    private static void WriteDeltaUInt32Column(BinaryWriter writer, IEnumerable<uint> source)
    {
        using var values = source.GetEnumerator();
        if (!values.MoveNext())
            return;
        var previous = values.Current;
        writer.Write(previous);
        while (values.MoveNext())
        {
            var current = values.Current;
            var delta = unchecked((int)(current - previous));
            WriteUleb128(writer, unchecked((uint)((delta << 1) ^ (delta >> 31))));
            previous = current;
        }
    }

    private static void WriteDeltaUInt64Column(BinaryWriter writer, IEnumerable<ulong> source)
    {
        using var values = source.GetEnumerator();
        if (!values.MoveNext())
            return;
        var previous = values.Current;
        writer.Write(previous);
        while (values.MoveNext())
        {
            var current = values.Current;
            var delta = unchecked((long)(current - previous));
            WriteUleb128(writer, unchecked((ulong)((delta << 1) ^ (delta >> 63))));
            previous = current;
        }
    }

    private static void WriteDeltaByteColumn(BinaryWriter writer, IEnumerable<byte> source)
    {
        using var values = source.GetEnumerator();
        if (!values.MoveNext())
            return;
        var previous = values.Current;
        writer.Write(previous);
        while (values.MoveNext())
        {
            var current = values.Current;
            var delta = unchecked((sbyte)(current - previous));
            WriteUleb128(writer, unchecked((uint)((delta << 1) ^ (delta >> 7))));
            previous = current;
        }
    }

    private static void WriteUleb128(BinaryWriter writer, uint value)
    {
        do
        {
            var next = (byte)(value & 0x7f);
            value >>= 7;
            writer.Write(value == 0 ? next : (byte)(next | 0x80));
        } while (value != 0);
    }

    private static void WriteUleb128(BinaryWriter writer, ulong value)
    {
        do
        {
            var next = (byte)(value & 0x7f);
            value >>= 7;
            writer.Write(value == 0 ? next : (byte)(next | 0x80));
        } while (value != 0);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            brotli.Write(bytes);
        return output.ToArray();
    }
}
