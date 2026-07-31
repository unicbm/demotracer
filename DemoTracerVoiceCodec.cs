using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private bool TryLoadVoiceClip(string clipPath, Action<string> reply, out LoadedVoiceClip clip)
    {
        clip = default;
        try
        {
            var resolvedPath = ResolveReadableManifestPath(clipPath);
            var identity = ReadVoiceClipFileIdentity(resolvedPath);
            if (!TryGetOrReadVoiceClip(identity, reply, CancellationToken.None, out clip))
                return false;

            if (!CurrentMapMatchesManifest(clip.Manifest.Map ?? string.Empty, out var currentMap))
            {
                reply(
                    $"[DTR WARN] map mismatch: server=\"{currentMap}\" voice_clip=\"{clip.Manifest.Map}\" path=\"{clipPath}\"");
            }
            return true;
        }
        catch (Exception ex)
        {
            reply($"[DTR ERR] failed to read voice clip: {ex.Message}");
            return false;
        }
    }

    private bool TryReadVoiceClip(
        VoiceClipFileIdentity identity,
        Action<string> reply,
        out LoadedVoiceClip clip)
    {
        clip = default;
        try
        {
            var resolvedPath = identity.Path;
            var data = File.ReadAllBytes(resolvedPath);
            if (data.LongLength != identity.Length)
                throw new InvalidDataException("voice clip length changed while it was being read");
            if (!LooksLikeVoiceDtvBytes(data))
            {
                reply("[DTR ERR] unsupported voice clip format; expected DTRVOICE v2 .dtv");
                return false;
            }

            var offset = VoiceDtvMagicBytes.Length;
            var version = ReadDtvUInt16(data, ref offset, "version");
            if (version != VoiceDtvVersion)
            {
                reply($"[DTR ERR] unsupported voice clip version={version}; expected={VoiceDtvVersion}");
                return false;
            }
            var flags = ReadDtvUInt16(data, ref offset, "flags");
            if (flags != 0)
            {
                reply($"[DTR ERR] unsupported voice clip flags=0x{flags:X}");
                return false;
            }

            var tickRate = ReadDtvFloat32(data, ref offset, "tick_rate");
            var startTick = ReadDtvInt32(data, ref offset, "start_tick");
            var endTick = ReadDtvInt32(data, ref offset, "end_tick");
            var selectedXuid = ReadDtvUInt64(data, ref offset, "selected_xuid");
            _ = ReadDtvInt32(data, ref offset, "selected_client");
            var speakerCountRaw = ReadDtvUInt32(data, ref offset, "speaker_count");
            var frameCountRaw = ReadDtvUInt32(data, ref offset, "frame_count");
            var audioByteCountRaw = ReadDtvUInt64(data, ref offset, "audio_len");
            if (!float.IsFinite(tickRate) || tickRate <= 0.0f)
            {
                reply("[DTR ERR] voice clip tick_rate must be positive");
                return false;
            }
            if (frameCountRaw == 0)
            {
                reply("[DTR ERR] voice clip contains no frames");
                return false;
            }
            if (speakerCountRaw > VoiceDtvMaxSpeakers ||
                frameCountRaw > VoiceDtvMaxFrames ||
                audioByteCountRaw > VoiceDtvMaxAudioBytes)
            {
                reply(
                    $"[DTR ERR] voice clip exceeds limits speakers={speakerCountRaw}/{VoiceDtvMaxSpeakers} " +
                    $"frames={frameCountRaw}/{VoiceDtvMaxFrames} audio={audioByteCountRaw}/{VoiceDtvMaxAudioBytes}");
                return false;
            }
            var speakerCount = (int)speakerCountRaw;
            var frameCount = (int)frameCountRaw;

            _ = ReadDtvString(data, ref offset, "demo_stem");
            _ = ReadDtvString(data, ref offset, "demo_sha256");
            var map = ReadDtvString(data, ref offset, "map");

            const int speakerRecordBytes = sizeof(ulong) + sizeof(int) + sizeof(uint);
            if (speakerCount > (data.Length - offset) / speakerRecordBytes)
                throw new InvalidDataException("speaker_count exceeds remaining voice clip bytes");

            var speakers = new List<VoiceClipSpeaker>(speakerCount);
            ulong totalSpeakerFrames = 0;
            for (var i = 0; i < speakerCount; i++)
            {
                var xuid = ReadDtvUInt64(data, ref offset, $"speakers[{i}].xuid");
                var client = ReadDtvInt32(data, ref offset, $"speakers[{i}].client");
                var speakerFrameCount = ReadDtvUInt32(data, ref offset, $"speakers[{i}].frame_count");
                if (speakerFrameCount > frameCountRaw ||
                    totalSpeakerFrames > frameCountRaw - speakerFrameCount)
                {
                    throw new InvalidDataException($"speaker {i} frame_count exceeds total frame_count");
                }
                totalSpeakerFrames += speakerFrameCount;
                speakers.Add(new VoiceClipSpeaker
                {
                    Xuid = xuid,
                    Client = client,
                    FrameCount = (int)speakerFrameCount
                });
            }

            if (speakers.Count == 0)
            {
                reply("[DTR ERR] voice clip contains no speakers");
                return false;
            }
            if (totalSpeakerFrames != frameCountRaw)
            {
                throw new InvalidDataException(
                    $"speaker frame_count sum {totalSpeakerFrames} != frame_count {frameCountRaw}");
            }

            var frameMetadataBytes = data.Length - offset - checked((int)audioByteCountRaw);
            const int minimumFrameMetadataBytes = 4;
            if (frameMetadataBytes < 0 || frameCount > frameMetadataBytes / minimumFrameMetadataBytes)
                throw new InvalidDataException("frame_count exceeds remaining voice metadata bytes");

            var decodedFrames = new List<DtvFrameInfo>(frameCount);
            var relativeTick = 0U;
            ulong totalFrameAudioBytes = 0;
            for (var i = 0; i < frameCount; i++)
            {
                var tickDelta = ReadDtvUVarint(data, ref offset, $"frames[{i}].tick_delta");
                if (tickDelta > uint.MaxValue - relativeTick)
                    throw new InvalidDataException($"voice frame {i} relative_tick overflow");
                relativeTick += (uint)tickDelta;

                var speakerIndex = ReadDtvUVarint(data, ref offset, $"frames[{i}].speaker_index");
                if (speakerIndex >= (ulong)speakers.Count)
                    throw new InvalidDataException($"voice frame {i} speaker_index={speakerIndex} out of range");

                var audioLengthRaw = ReadDtvUVarint(data, ref offset, $"frames[{i}].audio_len");
                if (audioLengthRaw == 0 || audioLengthRaw > int.MaxValue)
                    throw new InvalidDataException($"voice frame {i} invalid audio_len={audioLengthRaw}");
                if (audioLengthRaw > audioByteCountRaw ||
                    totalFrameAudioBytes > audioByteCountRaw - audioLengthRaw)
                {
                    throw new InvalidDataException($"voice frame {i} audio_len exceeds declared audio blob");
                }
                totalFrameAudioBytes += audioLengthRaw;

                var frameFlags = ReadDtvByte(data, ref offset, $"frames[{i}].flags");
                var knownFlags = VoiceDtvFlagFormat |
                                 VoiceDtvFlagSampleRate |
                                 VoiceDtvFlagVoiceLevel |
                                 VoiceDtvFlagSequenceBytes |
                                 VoiceDtvFlagSectionNumber |
                                 VoiceDtvFlagUncompressedSampleOffset |
                                 VoiceDtvFlagNumPackets |
                                 VoiceDtvFlagPacketOffsets;
                if ((frameFlags & ~knownFlags) != 0)
                    throw new InvalidDataException($"voice frame {i} has unsupported flags=0x{frameFlags:X2}");

                var speaker = speakers[(int)speakerIndex];
                var frame = new DtvFrameInfo
                {
                    RelativeTick = relativeTick,
                    Xuid = speaker.Xuid,
                    AudioLength = (int)audioLengthRaw
                };
                if ((frameFlags & VoiceDtvFlagFormat) != 0)
                    frame.Format = checked((int)ReadDtvSVarint(data, ref offset, $"frames[{i}].format"));
                if ((frameFlags & VoiceDtvFlagSampleRate) != 0)
                    frame.SampleRate = checked((int)ReadDtvUVarint(data, ref offset, $"frames[{i}].sample_rate"));
                if ((frameFlags & VoiceDtvFlagVoiceLevel) != 0)
                    frame.VoiceLevel = ReadDtvFloat32(data, ref offset, $"frames[{i}].voice_level");
                if ((frameFlags & VoiceDtvFlagSequenceBytes) != 0)
                    frame.SequenceBytes = checked((int)ReadDtvSVarint(data, ref offset, $"frames[{i}].sequence_bytes"));
                if ((frameFlags & VoiceDtvFlagSectionNumber) != 0)
                    frame.SectionNumber = checked((int)ReadDtvUVarint(data, ref offset, $"frames[{i}].section_number"));
                if ((frameFlags & VoiceDtvFlagUncompressedSampleOffset) != 0)
                    frame.UncompressedSampleOffset = checked((int)ReadDtvUVarint(data, ref offset, $"frames[{i}].uncompressed_sample_offset"));
                if ((frameFlags & VoiceDtvFlagNumPackets) != 0)
                    frame.NumPackets = checked((uint)ReadDtvUVarint(data, ref offset, $"frames[{i}].num_packets"));
                if ((frameFlags & VoiceDtvFlagPacketOffsets) != 0)
                {
                    var packetOffsetCount = ReadDtvUVarint(data, ref offset, $"frames[{i}].packet_offset_count");
                    if (packetOffsetCount > VoiceDtvMaxPacketOffsetsPerFrame ||
                        packetOffsetCount > (ulong)(data.Length - offset))
                    {
                        throw new InvalidDataException(
                            $"voice frame {i} packet_offset_count={packetOffsetCount} exceeds limit {VoiceDtvMaxPacketOffsetsPerFrame}");
                    }
                    frame.PacketOffsets = new uint[(int)packetOffsetCount];
                    for (var packetIndex = 0; packetIndex < frame.PacketOffsets.Length; packetIndex++)
                    {
                        frame.PacketOffsets[packetIndex] = checked((uint)ReadDtvUVarint(
                            data,
                            ref offset,
                            $"frames[{i}].packet_offsets[{packetIndex}]"));
                    }
                }
                decodedFrames.Add(frame);
            }

            if (totalFrameAudioBytes != audioByteCountRaw)
            {
                throw new InvalidDataException(
                    $"voice frame audio sum {totalFrameAudioBytes} != audio_len {audioByteCountRaw}");
            }

            var audioByteCount = (int)audioByteCountRaw;
            if (data.Length - offset != audioByteCount)
                throw new InvalidDataException(
                    $"voice audio blob length mismatch expected={audioByteCount} actual={data.Length - offset}");

            var audioOffset = offset;
            for (var i = 0; i < decodedFrames.Count; i++)
            {
                var frame = decodedFrames[i];
                if (audioOffset > data.Length - frame.AudioLength)
                    throw new InvalidDataException($"voice frame {i} audio extends beyond blob");
                frame.AudioOffset = audioOffset;
                audioOffset += frame.AudioLength;
            }

            if (audioOffset != data.Length)
                throw new InvalidDataException("voice frame audio lengths do not consume the full audio blob");

            var manifest = new VoiceClipManifest
            {
                Map = string.IsNullOrWhiteSpace(map) ? null : map,
                TickRate = tickRate,
                SelectedXuid = selectedXuid,
                StartTick = startTick,
                EndTick = endTick,
                DurationSeconds = Math.Max(0, endTick - startTick) / tickRate,
                Speakers = speakers
            };
            if (!TryBuildVoiceFrames(manifest, decodedFrames, reply, out var frames))
                return false;

            clip = new LoadedVoiceClip(resolvedPath, manifest, data, frames);
            return true;
        }
        catch (Exception ex)
        {
            reply($"[DTR ERR] failed to read voice clip: {ex.Message}");
            return false;
        }
    }

    private static VoiceClipFileIdentity ReadVoiceClipFileIdentity(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException($"voice clip not found: {path}", path);
        if (info.Length > VoiceDtvMaxFileBytes)
        {
            throw new InvalidDataException(
                $"voice clip length {info.Length} exceeds limit {VoiceDtvMaxFileBytes}");
        }
        return new VoiceClipFileIdentity(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private bool TryGetOrReadVoiceClip(
        VoiceClipFileIdentity identity,
        Action<string> reply,
        CancellationToken cancellationToken,
        out LoadedVoiceClip clip)
    {
        if (TryGetCachedVoiceClip(identity, out clip))
            return true;
        if (cancellationToken.IsCancellationRequested)
        {
            clip = default;
            return false;
        }
        if (!TryReadVoiceClip(identity, reply, out clip))
            return false;
        if (cancellationToken.IsCancellationRequested)
        {
            clip = default;
            return false;
        }

        var currentIdentity = ReadVoiceClipFileIdentity(identity.Path);
        if (!identity.Matches(currentIdentity))
        {
            reply("[DTR ERR] voice clip changed while it was being read");
            clip = default;
            return false;
        }

        clip = CacheVoiceClip(identity, clip, cancellationToken);
        return true;
    }

    private bool TryGetCachedVoiceClip(VoiceClipFileIdentity identity, out LoadedVoiceClip clip)
    {
        lock (_voiceClipCacheGate)
        {
            if (!_voiceClipCache.TryGetValue(identity.Path, out var entry))
            {
                clip = default;
                return false;
            }
            if (!entry.Identity.Matches(identity))
            {
                RemoveVoiceClipCacheEntry(entry);
                clip = default;
                return false;
            }

            _voiceClipCacheLru.Remove(entry.LruNode);
            _voiceClipCacheLru.AddFirst(entry.LruNode);
            clip = entry.Clip;
            return true;
        }
    }

    private LoadedVoiceClip CacheVoiceClip(
        VoiceClipFileIdentity identity,
        LoadedVoiceClip clip,
        CancellationToken cancellationToken)
    {
        var size = clip.AudioPayload.LongLength;
        if (size > VoiceClipCacheMaxBytes)
            return clip;

        lock (_voiceClipCacheGate)
        {
            if (cancellationToken.IsCancellationRequested)
                return clip;

            if (_voiceClipCache.TryGetValue(identity.Path, out var existing))
            {
                if (existing.Identity.Matches(identity))
                {
                    _voiceClipCacheLru.Remove(existing.LruNode);
                    _voiceClipCacheLru.AddFirst(existing.LruNode);
                    return existing.Clip;
                }
                RemoveVoiceClipCacheEntry(existing);
            }

            while ((_voiceClipCache.Count >= VoiceClipCacheMaxEntries ||
                    _voiceClipCacheBytes + size > VoiceClipCacheMaxBytes) &&
                   _voiceClipCacheLru.Last is { } oldest)
            {
                RemoveVoiceClipCacheEntry(_voiceClipCache[oldest.Value]);
            }

            var node = _voiceClipCacheLru.AddFirst(identity.Path);
            var entry = new VoiceClipCacheEntry(identity, clip, node, size);
            _voiceClipCache.Add(identity.Path, entry);
            _voiceClipCacheBytes += size;
            return clip;
        }
    }

    private void RemoveVoiceClipCacheEntry(VoiceClipCacheEntry entry)
    {
        _voiceClipCache.Remove(entry.Identity.Path);
        _voiceClipCacheLru.Remove(entry.LruNode);
        _voiceClipCacheBytes -= entry.Size;
    }

    private void ClearVoiceClipCache()
    {
        lock (_voiceClipCacheGate)
        {
            _voiceClipCache.Clear();
            _voiceClipCacheLru.Clear();
            _voiceClipCacheBytes = 0;
        }
    }

    private void QueueVoiceClipPreload(string clipPath)
    {
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        lock (_voiceClipPreloadGate)
        {
            _voiceClipPreloadCancellation?.Cancel();
            _voiceClipPreloadCancellation = cancellation;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (token.IsCancellationRequested)
                    return;
                var identity = ReadVoiceClipFileIdentity(clipPath);
                if (identity.Length > VoiceClipCacheMaxBytes)
                    return;
                _ = TryGetOrReadVoiceClip(identity, static _ => { }, token, out _);
            }
            catch
            {
                // A foreground load reports errors. Preload stays silent and never touches game state.
            }
            finally
            {
                lock (_voiceClipPreloadGate)
                {
                    if (ReferenceEquals(_voiceClipPreloadCancellation, cancellation))
                        _voiceClipPreloadCancellation = null;
                    cancellation.Dispose();
                }
            }
        });
    }

    private void CancelVoiceClipPreload()
    {
        lock (_voiceClipPreloadGate)
        {
            _voiceClipPreloadCancellation?.Cancel();
            _voiceClipPreloadCancellation = null;
        }
    }

    private static bool TryBuildVoiceFrames(
        VoiceClipManifest manifest,
        IReadOnlyList<DtvFrameInfo> decodedFrames,
        Action<string> reply,
        out List<VoiceClipRuntimeFrame> frames)
    {
        frames = new List<VoiceClipRuntimeFrame>(decodedFrames.Count);
        var nextContinuousSecondsByXuid = new Dictionary<ulong, float>();
        var orderedFrames = decodedFrames
            .Select((frame, index) => (Frame: frame, Index: index))
            .OrderBy(entry => entry.Frame.RelativeTick)
            .ThenBy(entry => entry.Index);

        foreach (var entry in orderedFrames)
        {
            var frame = entry.Frame;
            var i = entry.Index;
            if (frame.Format != VoiceDataFormatOpus)
            {
                reply(
                    $"[DTR ERR] voice frame {i} format={frame.Format}; only Opus format={VoiceDataFormatOpus} is supported");
                return false;
            }

            if (frame.AudioLength == 0)
            {
                reply($"[DTR ERR] voice frame {i} has empty audio");
                return false;
            }

            var sampleRate = frame.SampleRate;
            if (sampleRate <= 0)
                sampleRate = DefaultVoiceSampleRate;
            var voiceLevel = frame.VoiceLevel;
            if (!TryBuildPacketOffsets(
                    i,
                    frame.AudioLength,
                    frame.PacketOffsets,
                    frame.NumPackets,
                    reply,
                    out var packetOffsets,
                    out var numPackets))
                return false;

            var xuid = frame.Xuid == 0 ? manifest.SelectedXuid : frame.Xuid;
            var demoSeconds = frame.RelativeTick / manifest.TickRate;
            nextContinuousSecondsByXuid.TryGetValue(xuid, out var nextContinuousSeconds);
            var playbackSeconds = demoSeconds - nextContinuousSeconds > VoiceTimelineGapThresholdSeconds
                ? demoSeconds
                : nextContinuousSeconds;
            frames.Add(new VoiceClipRuntimeFrame(
                frame.RelativeTick,
                playbackSeconds,
                xuid,
                frame.AudioOffset,
                frame.AudioLength,
                sampleRate,
                voiceLevel,
                frame.SequenceBytes,
                frame.SectionNumber,
                frame.UncompressedSampleOffset,
                numPackets,
                packetOffsets));
            var packetCountForTiming = Math.Max(1U, numPackets);
            nextContinuousSecondsByXuid[xuid] = playbackSeconds +
                (packetCountForTiming * VoiceOpusSamplesPerPacket) / (float)sampleRate;
        }

        frames.Sort(static (left, right) =>
        {
            var cmp = left.PlaybackSeconds.CompareTo(right.PlaybackSeconds);
            if (cmp != 0)
                return cmp;
            cmp = left.RelativeTick.CompareTo(right.RelativeTick);
            return cmp != 0 ? cmp : left.Xuid.CompareTo(right.Xuid);
        });
        return true;
    }

    private static bool TryBuildPacketOffsets(
        int frameIndex,
        int audioLength,
        IReadOnlyList<uint> rawOffsets,
        uint? rawNumPackets,
        Action<string> reply,
        out uint[] packetOffsets,
        out uint numPackets)
    {
        numPackets = rawNumPackets.GetValueOrDefault(0);
        if (numPackets > 0)
        {
            if (numPackets > int.MaxValue)
            {
                reply($"[DTR ERR] voice frame {frameIndex} num_packets={numPackets} is too large");
                packetOffsets = [];
                return false;
            }
            var requiredPackets = (int)numPackets;
            if (rawOffsets.Count < requiredPackets)
            {
                reply(
                    $"[DTR ERR] voice frame {frameIndex} num_packets={numPackets} but packet_offsets={rawOffsets.Count}");
                packetOffsets = [];
                return false;
            }
            packetOffsets = rawOffsets.Take(requiredPackets).ToArray();
        }
        else
        {
            packetOffsets = rawOffsets.Where(offset => offset != 0).ToArray();
            numPackets = (uint)packetOffsets.Length;
        }

        uint previous = 0;
        for (var i = 0; i < packetOffsets.Length; i++)
        {
            var offset = packetOffsets[i];
            if (offset == 0 || offset <= previous || offset > audioLength)
            {
                reply(
                    $"[DTR ERR] voice frame {frameIndex} has invalid packet_offsets[{i}]={offset} for audio_len={audioLength}");
                return false;
            }
            previous = offset;
        }

        return true;
    }

    private static bool LooksLikeVoiceDtvBytes(byte[] data)
    {
        if (data.Length < VoiceDtvMagicBytes.Length)
            return false;
        for (var i = 0; i < VoiceDtvMagicBytes.Length; i++)
        {
            if (data[i] != VoiceDtvMagicBytes[i])
                return false;
        }
        return true;
    }

    private static byte ReadDtvByte(byte[] data, ref int offset, string name)
    {
        EnsureDtvAvailable(data, offset, 1, name);
        return data[offset++];
    }

    private static ushort ReadDtvUInt16(byte[] data, ref int offset, string name)
    {
        EnsureDtvAvailable(data, offset, sizeof(ushort), name);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        return value;
    }

    private static int ReadDtvInt32(byte[] data, ref int offset, string name)
    {
        EnsureDtvAvailable(data, offset, sizeof(int), name);
        var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static uint ReadDtvUInt32(byte[] data, ref int offset, string name)
    {
        EnsureDtvAvailable(data, offset, sizeof(uint), name);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));
        offset += sizeof(uint);
        return value;
    }

    private static ulong ReadDtvUInt64(byte[] data, ref int offset, string name)
    {
        EnsureDtvAvailable(data, offset, sizeof(ulong), name);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, sizeof(ulong)));
        offset += sizeof(ulong);
        return value;
    }

    private static float ReadDtvFloat32(byte[] data, ref int offset, string name)
    {
        EnsureDtvAvailable(data, offset, sizeof(float), name);
        var raw = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(float)));
        offset += sizeof(float);
        return BitConverter.Int32BitsToSingle(raw);
    }

    private static string ReadDtvString(byte[] data, ref int offset, string name)
    {
        var lengthRaw = ReadDtvUVarint(data, ref offset, $"{name}.length");
        if (lengthRaw > VoiceDtvMaxStringBytes)
            throw new InvalidDataException($"{name} exceeds limit {VoiceDtvMaxStringBytes}");
        var length = (int)lengthRaw;
        EnsureDtvAvailable(data, offset, length, name);
        var value = Encoding.UTF8.GetString(data, offset, length);
        offset += length;
        return value;
    }

    private static ulong ReadDtvUVarint(byte[] data, ref int offset, string name)
    {
        ulong value = 0;
        var shift = 0;
        for (var i = 0; i < 10; i++)
        {
            EnsureDtvAvailable(data, offset, 1, name);
            var b = data[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
        }

        throw new InvalidDataException($"{name} has malformed varint encoding");
    }

    private static long ReadDtvSVarint(byte[] data, ref int offset, string name)
    {
        var raw = ReadDtvUVarint(data, ref offset, name);
        return (long)(raw >> 1) ^ -((long)raw & 1);
    }

    private static void EnsureDtvAvailable(byte[] data, int offset, int count, string name)
    {
        if (offset < 0 || count < 0 || offset > data.Length - count)
            throw new InvalidDataException($"truncated voice clip while reading {name}");
    }

}
