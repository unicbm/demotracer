/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

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
    private readonly record struct LoadedVoiceClip(
        string Path,
        VoiceClipManifest Manifest,
        byte[] AudioPayload,
        List<VoiceClipRuntimeFrame> Frames);

    private readonly record struct VoiceClipFileIdentity(
        string Path,
        long Length,
        long LastWriteTimeUtcTicks)
    {
        public bool Matches(VoiceClipFileIdentity other)
            => string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) &&
               Length == other.Length &&
               LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks;
    }

    private sealed class VoiceClipCacheEntry(
        VoiceClipFileIdentity identity,
        LoadedVoiceClip clip,
        LinkedListNode<string> lruNode,
        long size)
    {
        public VoiceClipFileIdentity Identity { get; } = identity;
        public LoadedVoiceClip Clip { get; } = clip;
        public LinkedListNode<string> LruNode { get; } = lruNode;
        public long Size { get; } = size;
    }

    private sealed class VoiceClipPlaybackState(
        string path,
        float tickRate,
        float startTime,
        Dictionary<ulong, VoiceSpeakerPlayback> speakers,
        ulong defaultSpeakerXuid,
        byte[] audioPayload,
        List<VoiceClipRuntimeFrame> frames,
        List<int> recipientSlots,
        bool startedFromFreezePreroll)
    {
        public string Path { get; } = path;
        public float TickRate { get; } = tickRate;
        public float StartTime { get; } = startTime;
        public Dictionary<ulong, VoiceSpeakerPlayback> Speakers { get; } = speakers;
        public ulong DefaultSpeakerXuid { get; } = defaultSpeakerXuid;
        public byte[] AudioPayload { get; } = audioPayload;
        public List<VoiceClipRuntimeFrame> Frames { get; } = frames;
        public List<int> RecipientSlots { get; private set; } = recipientSlots;
        public bool StartedFromFreezePreroll { get; } = startedFromFreezePreroll;
        public int NextFrameIndex { get; set; }
        public int SentFrames { get; set; }
        public int SkippedSenderFrames { get; set; }
        public int SentPackets { get; set; }
        public int FailedPackets { get; set; }
        public int LastReturnCode { get; set; }

        public void PruneRecipients(Func<CCSPlayerController?, bool> predicate)
        {
            RecipientSlots = RecipientSlots
                .Where(slot => predicate(Utilities.GetPlayerFromSlot(slot)))
                .ToList();
        }

        public bool TryResolveSpeaker(ulong xuid, out VoiceSpeakerPlayback speaker)
        {
            if (xuid != 0 && Speakers.TryGetValue(xuid, out speaker!))
                return true;
            if (DefaultSpeakerXuid != 0 && Speakers.TryGetValue(DefaultSpeakerXuid, out speaker!))
                return true;
            speaker = null!;
            return false;
        }
    }

    private sealed class VoiceSpeakerPlayback(
        int slot,
        int client,
        ulong xuid,
        int nextSectionNumber,
        CsTeam? expectedTeam,
        bool followsLoadedReplay)
    {
        public int Slot { get; private set; } = slot;
        public int Client { get; private set; } = client;
        public ulong Xuid { get; } = xuid;
        public int NextSectionNumber { get; set; } = nextSectionNumber;
        public CsTeam? ExpectedTeam { get; } = expectedTeam;
        public bool FollowsLoadedReplay { get; } = followsLoadedReplay;

        public void Rebind(int newSlot)
        {
            Slot = newSlot;
            Client = newSlot;
        }
    }

    private sealed class VoiceClipRuntimeFrame(
        uint relativeTick,
        float playbackSeconds,
        ulong xuid,
        int audioOffset,
        int audioLength,
        int sampleRate,
        float voiceLevel,
        int sequenceBytes,
        int sectionNumber,
        int uncompressedSampleOffset,
        uint numPackets,
        uint[] packetOffsets)
    {
        public uint RelativeTick { get; } = relativeTick;
        public float PlaybackSeconds { get; } = playbackSeconds;
        public ulong Xuid { get; } = xuid;
        public int AudioOffset { get; } = audioOffset;
        public int AudioLength { get; } = audioLength;
        public int SampleRate { get; } = sampleRate;
        public float VoiceLevel { get; } = voiceLevel;
        public int SequenceBytes { get; } = sequenceBytes;
        public int SectionNumber { get; } = sectionNumber;
        public int UncompressedSampleOffset { get; } = uncompressedSampleOffset;
        public uint NumPackets { get; } = numPackets;
        public uint[] PacketOffsets { get; } = packetOffsets;
    }

    private sealed class VoiceClipManifest
    {
        public string? Map { get; init; }

        public float TickRate { get; init; }

        public ulong SelectedXuid { get; init; }

        public int StartTick { get; init; }

        public int EndTick { get; init; }

        public float DurationSeconds { get; init; }

        public List<VoiceClipSpeaker> Speakers { get; init; } = new();
    }

    private sealed class VoiceClipSpeaker
    {
        public ulong Xuid { get; init; }

        public int Client { get; init; }

        public int FrameCount { get; init; }
    }

    private sealed class DtvFrameInfo
    {
        public uint RelativeTick { get; init; }

        public ulong Xuid { get; init; }

        public int Format { get; set; } = VoiceDataFormatOpus;

        public int SampleRate { get; set; } = DefaultVoiceSampleRate;

        public float VoiceLevel { get; set; } = float.NaN;

        public int SequenceBytes { get; set; } = -1;

        public int SectionNumber { get; set; } = -1;

        public int UncompressedSampleOffset { get; set; } = -1;

        public uint? NumPackets { get; set; }

        public uint[] PacketOffsets { get; set; } = Array.Empty<uint>();

        public int AudioLength { get; init; }

        public int AudioOffset { get; set; }
    }
}
