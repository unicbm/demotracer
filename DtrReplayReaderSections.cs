/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

internal static partial class DtrReplayReader
{
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
            or SectionMovementExtras
            or SectionInputHistory;

    private static void RequireInputHistorySectionShape(DtrSectionHeader header, int tickCount)
    {
        if (header.SectionVersion != SectionVersionV1)
            throw new InvalidDataException(
                $"unsupported input history section version {header.SectionVersion}");
        if (header.ElementCount != tickCount)
            throw new InvalidDataException(
                $"input history section count {header.ElementCount} != expected {tickCount}");
        var minimum = checked(tickCount * BotControllerNative.ReplayInputHistoryTickByteSize);
        var maximum = checked(minimum +
            tickCount * MaxInputHistoryPerTick * BotControllerNative.ReplayInputHistoryEntryByteSize);
        if (header.UncompressedLength < minimum || header.UncompressedLength > maximum)
            throw new InvalidDataException(
                $"input history section length {header.UncompressedLength} is outside {minimum}..={maximum}");
    }

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

}
