/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text;

namespace DemoTracer.Tests;

public sealed class DtrReplayReaderLimitsTests : IDisposable
{
    private const byte CodecNone = 0;
    private readonly string tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"demotracer-reader-tests-{Guid.NewGuid():N}");

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
        var path = WriteFile(writer => WriteHeaderPrefix(writer, version: 12, tickCount: 2, subtickCount: 0));
        var limits = DtrReadLimits.Default with { MaxTickCount = 1 };

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path, limits));

        Assert.Contains("tick_count 2 exceeds limit 1", error.Message);
    }

    [Fact]
    public void RejectsImpossibleHeaderSubtickRatio()
    {
        var path = WriteFile(writer => WriteHeaderPrefix(writer, version: 12, tickCount: 1, subtickCount: 37));

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("exceeds 36 per tick", error.Message);
    }

    [Fact]
    public void RejectsProjectileAndMetadataCountsAtTheirLimits()
    {
        var projectilePath = WriteFile(writer =>
            WriteHeaderPrefix(writer, version: 12, tickCount: 0, subtickCount: 0, projectileCount: 2));
        var metadataPath = WriteFile(writer =>
            WriteHeaderPrefix(writer, version: 12, tickCount: 0, subtickCount: 0, metadataJsonLength: 2));

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
            WriteHeaderPrefix(writer, version: 12, tickCount: 0, subtickCount: 0);
            writer.Write(ushort.MaxValue);
        });

        var error = Assert.Throws<EndOfStreamException>(() => DtrReplayReader.Read(path));

        Assert.Contains("string in .dtr", error.Message);
    }

    [Theory]
    [InlineData(3U)]
    [InlineData(11U)]
    [InlineData(13U)]
    public void RejectsOtherFormatVersions(uint version)
    {
        var path = WriteFile(writer => WriteHeaderPrefix(writer, version, tickCount: 0, subtickCount: 0));
        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));
        Assert.Contains("reconvert the demo", error.Message);
    }

    [Fact]
    public void ReadsColumnDeltaSectionsBitExactly()
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
            DesiresDuck = 1
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
            Fields = 0xff,
            LeftHandDesired = 1
        };
        var snapshotPayload = BuildV2SnapshotPayload([pre, post]);
        var commandPayload = BuildV2CommandPayload([command]);
        var tickMetadata = new byte[8];
        BitConverter.GetBytes(42).CopyTo(tickMetadata, 0);
        var projectilePayload = new byte[48];
        BitConverter.GetBytes(45).CopyTo(projectilePayload, 4);
        projectilePayload[8] = (byte)ReplayProjectileKind.Smoke;
        for (var i = 0; i < 6; i++)
            BitConverter.GetBytes(i + 0.5f).CopyTo(projectilePayload, 12 + i * 4);
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 12, tickCount: 1, subtickCount: 0, projectileCount: 1);
            writer.Write(6U);
            WriteEmptyPlaybackState(writer);
            WriteSection(
                writer,
                sectionId: 1,
                codec: CodecNone,
                elementCount: 2,
                payload: snapshotPayload,
                sectionVersion: 2);
            WriteSection(writer, sectionId: 2, codec: CodecNone, elementCount: 1, payload: tickMetadata);
            WriteSection(writer, sectionId: 3, codec: CodecNone, elementCount: 1, payload: projectilePayload);
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

        Assert.Equal(12U, replay.Version);
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
        var projectile = Assert.Single(replay.Projectiles);
        Assert.Equal(new ReplayProjectileEvent(0, ReplayProjectileKind.Smoke, 45,
            new ReplayVector3(0.5f, 1.5f, 2.5f), new ReplayVector3(3.5f, 4.5f, 5.5f)), projectile);
        var decodedCommand = Assert.Single(replay.CommandFrames);
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(command.UpMove),
            BitConverter.SingleToUInt32Bits(decodedCommand.UpMove));
        Assert.Equal(command.Buttons, decodedCommand.Buttons);
        Assert.Equal(command.MouseDx, decodedCommand.MouseDx);
        Assert.Equal(command.MouseDy, decodedCommand.MouseDy);
        Assert.Equal(command.Fields & ~(1U << 6), decodedCommand.Fields);
        Assert.Equal(command.LeftHandDesired, decodedCommand.LeftHandDesired);
    }

    [Fact]
    public void PlaybackReadValidatesButDoesNotRetainUnusedEvidence()
    {
        var path = WriteAuxiliaryReplay(new byte[48]);
        var full = DtrReplayReader.Read(path);
        var playback = DtrReplayReader.ReadForPlayback(path);

        Assert.Single(full.MovementExtras);
        Assert.Empty(playback.MovementExtras);
        Assert.Equal(full.Ticks, playback.Ticks);
        Assert.Equal(full.Subticks, playback.Subticks);
        Assert.Equal(full.CommandFrames, playback.CommandFrames);
        Assert.Equal(full.SourceState, playback.SourceState);
        Assert.Equal(full.TickRate, playback.TickRate);
        Assert.Equal(48,
            DtrReplayPrefetch.EstimateReplayBytes(full) - DtrReplayPrefetch.EstimateReplayBytes(playback));
    }

    [Fact]
    public void PlaybackReadRejectsInvalidDiscardedMovementExtras()
    {
        var extras = new byte[48];
        BitConverter.GetBytes(float.PositiveInfinity).CopyTo(extras, 4);
        var path = WriteAuxiliaryReplay(extras);
        Assert.Contains("finite", Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path)).Message);
        Assert.Contains("finite", Assert.Throws<InvalidDataException>(() => DtrReplayReader.ReadForPlayback(path)).Message);
    }

    private string WriteAuxiliaryReplay(byte[] extras)
        => WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 12, tickCount: 1, subtickCount: 0);
            writer.Write(5U);
            WriteSection(writer, 9, CodecNone, 0, [], sectionVersion: 2);
            WriteSection(writer, 1, CodecNone, 2,
                BuildV2SnapshotPayload([new NativeMovementSnapshot(), new NativeMovementSnapshot()]), sectionVersion: 2);
            WriteSection(writer, 2, CodecNone, 1, new byte[8]);
            WriteSection(writer, 5, CodecNone, 0, []);
            WriteSection(writer, 7, CodecNone, 1, extras);
        });

    [Fact]
    public void ReadsBackdatedSubtickWhenBitExactly()
    {
        const float when = -1.671875f;
        var path = WriteFile(writer => WriteSingleSubtickReplay(writer, version: 12, when));

        var replay = DtrReplayReader.Read(path);

        Assert.Equal(12U, replay.Version);
        var subtick = Assert.Single(replay.Subticks);
        Assert.Equal(BitConverter.SingleToUInt32Bits(when), BitConverter.SingleToUInt32Bits(subtick.When));
    }

    [Fact]
    public void PreservesStoredVelocitiesWithoutReconstructingFromPositions()
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
            WriteCompleteHeader(writer, version: 12, tickCount: 2, subtickCount: 0);
            writer.Write(4U);
            WriteEmptyPlaybackState(writer);
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

        NativeMovementSnapshot[] expected = [before, artifact, artifact, after];
        NativeMovementSnapshot[] actual = [replay.Ticks[0].Pre, replay.Ticks[0].Post,
            replay.Ticks[1].Pre, replay.Ticks[1].Post];
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(BitConverter.SingleToUInt32Bits(expected[i].VelX), BitConverter.SingleToUInt32Bits(actual[i].VelX));
            Assert.Equal(BitConverter.SingleToUInt32Bits(expected[i].VelY), BitConverter.SingleToUInt32Bits(actual[i].VelY));
            Assert.Equal(BitConverter.SingleToUInt32Bits(expected[i].VelZ), BitConverter.SingleToUInt32Bits(actual[i].VelZ));
        }
    }

    [Fact]
    public void ReadsRoundStartBalanceFromCurrentMetadata()
    {
        var metadata = Encoding.UTF8.GetBytes(
            """{"schema_version":5,"round_start_balance":5250,"events":[],"inventory_snapshots":[]}""");
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(
                writer,
                version: 12,
                tickCount: 0,
                subtickCount: 0,
                metadataJsonLength: (uint)metadata.Length);
            writer.Write(5U);
            WriteEmptyPlaybackState(writer);
            WriteSection(writer, sectionId: 1, codec: CodecNone, elementCount: 0, payload: []);
            WriteSection(writer, sectionId: 2, codec: CodecNone, elementCount: 0, payload: []);
            WriteSection(writer, sectionId: 4, codec: CodecNone, elementCount: 1, payload: metadata);
            WriteSection(writer, sectionId: 5, codec: CodecNone, elementCount: 0, payload: []);
        });

        var replay = DtrReplayReader.Read(path);

        Assert.Equal(5, replay.HighFidelity.SchemaVersion);
        Assert.Equal(5_250U, replay.HighFidelity.RoundStartBalance);
    }

    [Fact]
    public void RejectsTopLevelTrailingBytes()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 12, tickCount: 0, subtickCount: 0);
            writer.Write(4U);
            WriteEmptyPlaybackState(writer);
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

    private string WriteSourceStateFile(byte[] body, int count = 9, int ticks = 8,
        byte codec = CodecNone, int? uncompressedLength = null)
        => WriteFile(writer => {
            WriteCompleteHeader(writer, 12, (uint)ticks, 0);
            writer.Write(4U);
            WriteSection(writer, 1, CodecNone, ticks + 1,
                BuildV2SnapshotPayload(new NativeMovementSnapshot[ticks + 1]), sectionVersion: 2);
            WriteSection(writer, 2, CodecNone, ticks, new byte[ticks * 8]);
            WriteSection(writer, 5, CodecNone, 0, []);
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
    [InlineData(8, 0)]
    [InlineData(10, 0)]
    [InlineData(9, 1)]
    public void CompactSourceRejectsCountAndLengthMismatch(int count, int extraBytes)
    {
        var body = CompactSourceFixture().Concat(new byte[extraBytes]).ToArray();
        Assert.Contains("source state", Assert.Throws<InvalidDataException>(() =>
            DtrReplayReader.Read(WriteSourceStateFile(body, count))).Message);
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
    public void RejectsRetiredBrotliCodec()
    {
        var path = WriteFile(writer => {
            WriteCompleteHeader(writer, 12, 0, 0);
            writer.Write(1U);
            WriteSection(writer, 2, 1, 0, []);
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
    [InlineData("{\"tick_index\":0,\"steam_id\":1,\"gear_acquired\":8}")]
    [InlineData("{\"tick_index\":0,\"steam_id\":1,\"weapon_def_counts\":null}")]
    [InlineData("{\"tick_index\":1,\"steam_id\":1,\"weapon_def_counts\":[]}")]
    [InlineData("{\"tick_index\":0,\"steam_id\":1,\"weapon_def_counts\":[{\"weapon_def_index\":7,\"count\":2147483647}]}")]
    [InlineData("{\"tick_index\":0,\"steam_id\":1,\"weapon_def_counts\":[{\"weapon_def_index\":7,\"count\":1},{\"weapon_def_index\":7,\"count\":1}]}")]
    public void RejectsUnsafeInventoryAcquisitionsBeforePlayback(string snapshot)
    {
        var metadata = Encoding.UTF8.GetBytes("{\"schema_version\":5,\"inventory_snapshots\":[" + snapshot + "]}");
        var snapshots = BuildV2SnapshotPayload([new NativeMovementSnapshot(), new NativeMovementSnapshot()]);
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 12, tickCount: 1, subtickCount: 0,
                metadataJsonLength: (uint)metadata.Length);
            writer.Write(5U);
            WriteEmptyPlaybackState(writer);
            WriteSection(writer, 1, CodecNone, 2, snapshots, sectionVersion: 2);
            WriteSection(writer, 2, CodecNone, 1, new byte[8]);
            WriteSection(writer, 4, CodecNone, 1, metadata);
            WriteSection(writer, 5, CodecNone, 0, []);
        });
        Assert.Contains("inventory", Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path)).Message);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void ReaderAdaptsOnlySchema4Inventory(int schema, bool supported)
    {
        var metadata = Encoding.UTF8.GetBytes($$"""
            {"schema_version":{{schema}},"inventory_snapshots":[{"tick_index":0,"steam_id":1,
            "weapon_def_counts":[{"weapon_def_index":43,"count":2,"acquired":false}],
            "armor_value":100,"has_helmet":true,"gear_acquired":0},
            {"tick_index":1,"steam_id":1,"weapon_def_counts":[{"weapon_def_index":43,"count":1}],
            "armor_value":60,"has_helmet":true},
            {"tick_index":2,"steam_id":1,"weapon_def_counts":[{"weapon_def_index":43,"count":2}],
            "armor_value":60,"has_helmet":true,"has_defuser":true}]}
            """);
        var snapshots = BuildV2SnapshotPayload(new NativeMovementSnapshot[4]);
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 12, tickCount: 3, subtickCount: 0,
                metadataJsonLength: (uint)metadata.Length);
            writer.Write(5U);
            WriteEmptyPlaybackState(writer);
            WriteSection(writer, 1, CodecNone, 4, snapshots, sectionVersion: 2);
            WriteSection(writer, 2, CodecNone, 3, new byte[24]);
            WriteSection(writer, 4, CodecNone, 1, metadata);
            WriteSection(writer, 5, CodecNone, 0, []);
        });
        if (!supported)
        {
            Assert.Contains("metadata schema", Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path)).Message);
            return;
        }
        var decoded = DtrReplayReader.Read(path).HighFidelity;
        Assert.Equal(5, decoded.SchemaVersion);
        Assert.Equal([schema == 4, false, schema == 4],
            decoded.InventorySnapshots.Select(s => Assert.Single(s.WeaponDefCounts).Acquired));
        Assert.Equal(schema == 4 ? new byte[] { 3, 0, 4 } : [0, 0, 0],
            decoded.InventorySnapshots.Select(s => s.GearAcquired));
        var timeline = new ReplayInventoryTimeline(decoded.InventorySnapshots);
        timeline.Start(0);
        timeline.PendingWeapons.Clear();
        timeline.ClearGear();
        Assert.True(timeline.Advance(1));
        Assert.Empty(timeline.PendingWeapons);
        Assert.Null(timeline.Armor);
        Assert.True(timeline.Advance(2));
        Assert.Equal(schema == 4, timeline.PendingWeapons.ContainsKey(43));
        Assert.Equal(schema == 4 ? true : (bool?)null, timeline.Defuser);
        Assert.Null(timeline.Armor);
        Assert.Null(timeline.Helmet);
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
            WriteCompleteHeader(writer, version: 12, tickCount: 1, subtickCount: 0);
            writer.Write(4U);
            WriteEmptyPlaybackState(writer);
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
            WriteCompleteHeader(writer, version: 12, tickCount: 1, subtickCount: 0);
            writer.Write(5U);
            WriteEmptyPlaybackState(writer);
            WriteSection(writer, 1, CodecNone, 2, snapshots, sectionVersion: 2);
            WriteSection(writer, 2, CodecNone, 1, tickMetadata);
            WriteSection(writer, 5, CodecNone, 0, []);
            WriteSection(writer, 6, CodecNone, 1, commands, sectionVersion: 2);
        });

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("unknown fields 0x80000000", error.Message);
    }

    [Fact]
    public void RejectsSectionCountBeforeReadingSectionHeaders()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 12, tickCount: 0, subtickCount: 0);
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
            WriteCompleteHeader(writer, version: 12, tickCount: 0, subtickCount: 0);
            writer.Write(1U);
            WriteSectionHeader(
                writer,
                sectionId: 1,
                codec: CodecNone,
                elementCount: 1,
                uncompressedLength: 0,
                compressedLength: 10);
        });

        var error = Assert.Throws<InvalidDataException>(() => DtrReplayReader.Read(path));

        Assert.Contains("snapshots section count 1 != expected 0", error.Message);
    }

    [Fact]
    public void ReservesBytesForRemainingHeadersBeforeReadingPayload()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 12, tickCount: 0, subtickCount: 0);
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
    public void EnforcesCumulativeSectionBudgets()
    {
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 12, tickCount: 0, subtickCount: 0);
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
            WriteCompleteHeader(writer, version: 12, tickCount: 0, subtickCount: 0);
            writer.Write(5U);
            WriteEmptyPlaybackState(writer);
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
    public void RejectsMoreThanThirtySixSubticksInTickMetadata()
    {
        var tickMetadata = new byte[8];
        BitConverter.GetBytes(37U).CopyTo(tickMetadata, 4);
        var path = WriteFile(writer =>
        {
            WriteCompleteHeader(writer, version: 12, tickCount: 1, subtickCount: 36);
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
        WriteCompleteHeader(writer, version, tickCount: 1, subtickCount: 1);
        writer.Write(4U);
        WriteSection(writer, 9, CodecNone, 0, [], sectionVersion: 2);
        WriteSection(writer, 1, CodecNone, 2, snapshots, sectionVersion: 2);
        WriteSection(writer, 2, CodecNone, 1, tickMetadata);
        WriteSection(writer, 5, CodecNone, 1, subticks);
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
        writer.Write(projectileCount);
        writer.Write(0U);
        writer.Write(metadataJsonLength);
    }

    private static void WriteEmptyPlaybackState(BinaryWriter writer)
    {
        WriteSection(writer, 9, CodecNone, 0, [], sectionVersion: 2);
    }

    private static void WriteSection(
        BinaryWriter writer,
        uint sectionId,
        byte codec,
        int elementCount,
        byte[] payload,
        int? uncompressedLength = null,
        uint? sectionVersion = null)
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
        uint? sectionVersion = null)
    {
        writer.Write(sectionId);
        writer.Write(sectionVersion ?? (sectionId is 1 or 6 or 9 ? 2U : 1U));
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
        WriteDeltaByteColumn(writer, snapshots.Select(_ => byte.MaxValue));
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
        WriteDeltaUInt32Column(writer, frames.Select(_ => uint.MaxValue));
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

}
