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
    private const string VoiceDtvMagic = "DTRVOICE";
    private const ushort VoiceDtvVersion = 2;
    private const byte VoiceDtvFlagFormat = 0x01;
    private const byte VoiceDtvFlagSampleRate = 0x02;
    private const byte VoiceDtvFlagVoiceLevel = 0x04;
    private const byte VoiceDtvFlagSequenceBytes = 0x08;
    private const byte VoiceDtvFlagSectionNumber = 0x10;
    private const byte VoiceDtvFlagUncompressedSampleOffset = 0x20;
    private const byte VoiceDtvFlagNumPackets = 0x40;
    private const byte VoiceDtvFlagPacketOffsets = 0x80;
    private const int VoiceDataFormatOpus = 2;
    private const int DefaultVoiceSampleRate = 48_000;
    private const int VoiceOpusSamplesPerPacket = 480;
    private const float VoicePlaybackEpsilonSeconds = 0.002f;
    private const float VoiceTimelineGapThresholdSeconds = 0.12f;
    private const int VoiceClipCacheMaxEntries = 2;
    private const long VoiceClipCacheMaxBytes = 32L * 1024 * 1024;
    private const long VoiceDtvMaxFileBytes = 64L * 1024 * 1024;
    private const ulong VoiceDtvMaxAudioBytes = 48UL * 1024 * 1024;
    private const uint VoiceDtvMaxSpeakers = 64;
    private const uint VoiceDtvMaxFrames = 500_000;
    private const int VoiceDtvMaxStringBytes = 1024 * 1024;
    private const ulong VoiceDtvMaxPacketOffsetsPerFrame = 64;
    private static readonly byte[] VoiceDtvMagicBytes = Encoding.ASCII.GetBytes(VoiceDtvMagic);

    [DllImport(
        "BotController",
        EntryPoint = "BotController_SendVoiceFrame",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotControllerSendVoiceFrameSlice(
        int recipientSlot,
        int senderClient,
        ulong senderXuid,
        IntPtr audio,
        int audioBytes,
        int sampleRate,
        float voiceLevel,
        int sequenceBytes,
        int sectionNumber,
        int uncompressedSampleOffset,
        uint numPackets,
        [In] uint[] packetOffsets,
        int packetOffsetCount,
        int tick,
        int audibleMask);

    private VoiceClipPlaybackState? _voiceTestPlayback;
    private int _nextVoiceSectionNumber = 1;
    private bool _voiceAutoEnabled = true;
    private string _loadedVoiceClipPath = string.Empty;
    private int _loadedVoiceRound = -1;
    private int _loadedVoiceRecordingStartTick;
    private int _loadedVoiceLiveStartTick;
    private float _loadedVoiceTickRate;
    private readonly object _voiceClipCacheGate = new();
    private readonly Dictionary<string, VoiceClipCacheEntry> _voiceClipCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _voiceClipCacheLru = new();
    private readonly object _voiceClipPreloadGate = new();
    private long _voiceClipCacheBytes;
    private CancellationTokenSource? _voiceClipPreloadCancellation;

    [ConsoleCommand("dtr_voice_auto", "dtr_voice_auto [status|on|off]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void VoiceAutoCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
        {
            var mode = command.GetArg(1);
            if (mode.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                command.ReplyToCommand(FormatVoiceAutoStatus());
                return;
            }

            _voiceAutoEnabled = ParseOnOff(mode, _voiceAutoEnabled);
        }

        command.ReplyToCommand(FormatVoiceAutoStatus());
    }

    [ConsoleCommand("dtr_voice_test", "dtr_voice_test <voice_clip.dtv> <sender_slot> [recipient_slot|all]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void VoiceTestCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (!BotControllerNative.CanSendVoice)
        {
            command.ReplyToCommand($"[DTR ERR] voice send unavailable; {BotControllerNative.RuntimeSummary}");
            return;
        }
        if (command.ArgCount < 3)
        {
            command.ReplyToCommand("usage: dtr_voice_test <voice_clip.dtv> <sender_slot> [recipient_slot|all]");
            return;
        }
        if (!TryParseSlotAt(command, 2, out var senderSlot))
            return;

        var sender = Utilities.GetPlayerFromSlot(senderSlot);
        if (sender is not { IsValid: true } || !IsReplayTargetBot(sender))
        {
            command.ReplyToCommand(
                $"[DTR ERR] sender slot {senderSlot} is not a safe replay bot target");
            return;
        }
        if (!TryResolveVoiceRecipients(player, command, 3, out var recipients))
            return;
        if (!TryLoadVoiceClip(command.GetArg(1), command.ReplyToCommand, out var clip))
            return;

        StopVoiceTestPlayback("voice_test_replace", printSummary: false);

        var senderClient = senderSlot;
        var speakerXuid = clip.Manifest.SelectedXuid != 0
            ? clip.Manifest.SelectedXuid
            : clip.Frames.First().Xuid;
        var speakers = new Dictionary<ulong, VoiceSpeakerPlayback>
        {
            [speakerXuid] = new(
                senderSlot,
                senderClient,
                speakerXuid,
                AllocateVoiceSectionBase(clip.Frames.Count),
                expectedTeam: null,
                followsLoadedReplay: false)
        };
        _voiceTestPlayback = new VoiceClipPlaybackState(
            clip.Path,
            clip.Manifest.TickRate,
            Server.CurrentTime,
            speakers,
            speakerXuid,
            clip.AudioPayload,
            clip.Frames,
            recipients,
            startedFromFreezePreroll: false);

        command.ReplyToCommand(
            $"[DTR OK] voice test started frames={clip.Frames.Count} duration={clip.Manifest.DurationSeconds.ToString("F2", CultureInfo.InvariantCulture)}s " +
            $"sender_slot={senderSlot} sender_client={senderClient} xuid={speakerXuid} recipients={FormatSlotList(recipients)}");
    }

    [ConsoleCommand("dtr_voice_mix", "dtr_voice_mix <voice_clip.dtv> <xuid=slot[,xuid=slot...]|loaded> [recipient_slot|all]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void VoiceMixCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (!BotControllerNative.CanSendVoice)
        {
            command.ReplyToCommand($"[DTR ERR] voice send unavailable; {BotControllerNative.RuntimeSummary}");
            return;
        }
        if (command.ArgCount < 3)
        {
            command.ReplyToCommand("usage: dtr_voice_mix <voice_clip.dtv> <xuid=slot[,xuid=slot...]|loaded> [recipient_slot|all]");
            return;
        }
        if (!TryLoadVoiceClip(command.GetArg(1), command.ReplyToCommand, out var clip))
            return;
        if (!TryBuildVoiceSpeakerMap(command.GetArg(2), clip, command.ReplyToCommand, out var speakers))
            return;
        if (!TryResolveVoiceRecipients(player, command, 3, out var recipients))
            return;

        if (!TryPrepareVoiceMixFrames(clip, speakers, command.ReplyToCommand, out var frames))
            return;

        StopVoiceTestPlayback("voice_mix_replace", printSummary: false);
        _voiceTestPlayback = new VoiceClipPlaybackState(
            clip.Path,
            clip.Manifest.TickRate,
            Server.CurrentTime,
            speakers,
            defaultSpeakerXuid: 0,
            clip.AudioPayload,
            frames,
            recipients,
            startedFromFreezePreroll: false);

        command.ReplyToCommand(
            $"[DTR OK] voice mix started speakers={speakers.Count} frames={frames.Count}/{clip.Frames.Count} " +
            $"duration={clip.Manifest.DurationSeconds.ToString("F2", CultureInfo.InvariantCulture)}s recipients={FormatSlotList(recipients)}");
    }

    [ConsoleCommand("dtr_voice_stop", "dtr_voice_stop")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void VoiceStopCommand(CCSPlayerController? player, CommandInfo command)
    {
        var stopped = StopVoiceTestPlayback("manual_stop", printSummary: false);
        command.ReplyToCommand(stopped ? "dtr: voice test stopped" : "dtr: no active voice test");
    }

    private void ProcessVoiceTestPlayback()
    {
        var state = _voiceTestPlayback;
        if (state == null)
            return;

        if (!BotControllerNative.CanSendVoice)
        {
            StopVoiceTestPlayback("voice_send_unavailable");
            return;
        }

        state.PruneRecipients(IsVoiceRecipient);
        if (state.RecipientSlots.Count == 0)
        {
            StopVoiceTestPlayback("no_live_recipients");
            return;
        }

        var elapsed = Math.Max(0.0f, Server.CurrentTime - state.StartTime);
        while (state.NextFrameIndex < state.Frames.Count)
        {
            var frame = state.Frames[state.NextFrameIndex];
            var due = frame.PlaybackSeconds;
            if (due > elapsed + VoicePlaybackEpsilonSeconds)
                break;

            if (!state.TryResolveSpeaker(frame.Xuid, out var speaker))
            {
                state.NextFrameIndex++;
                continue;
            }

            var sectionNumber = speaker.NextSectionNumber;
            if (!TryResolveLiveVoiceSender(speaker, out var sender))
            {
                // A team join can evict or rebind one replay bot. Keep the
                // other speakers synchronized instead of stopping the mix.
                state.SkippedSenderFrames++;
                state.NextFrameIndex++;
                continue;
            }

            var audibleRecipients = AudibleVoiceRecipientsForSpeaker(
                state.RecipientSlots,
                sender,
                speaker.ExpectedTeam);
            if (audibleRecipients.Count == 0)
            {
                state.NextFrameIndex++;
                continue;
            }

            if (frame.AudioLength <= 0 ||
                frame.AudioOffset < 0 ||
                frame.AudioOffset > state.AudioPayload.Length - frame.AudioLength)
            {
                StopVoiceTestPlayback("invalid_voice_audio_slice");
                return;
            }

            var audioHandle = GCHandle.Alloc(state.AudioPayload, GCHandleType.Pinned);
            try
            {
                var audio = IntPtr.Add(audioHandle.AddrOfPinnedObject(), frame.AudioOffset);
                foreach (var recipientSlot in audibleRecipients)
                {
                    var rc = SendVoiceFrameSlice(
                        recipientSlot,
                        speaker.Client,
                        speaker.Xuid,
                        audio,
                        frame.AudioLength,
                        frame.SampleRate,
                        frame.VoiceLevel,
                        frame.SequenceBytes,
                        sectionNumber,
                        frame.UncompressedSampleOffset,
                        frame.NumPackets,
                        frame.PacketOffsets,
                        tick: -1,
                        audibleMask: 1);
                    state.SentPackets++;
                    if (rc != 0)
                    {
                        state.FailedPackets++;
                        state.LastReturnCode = rc;
                        StopVoiceTestPlayback($"voice_send_failed_rc_{rc}");
                        return;
                    }
                }
            }
            finally
            {
                audioHandle.Free();
            }

            state.SentFrames++;
            speaker.NextSectionNumber++;
            state.NextFrameIndex++;
        }

        if (state.NextFrameIndex >= state.Frames.Count)
        {
            Server.PrintToConsole(
                $"dtr: voice test finished path=\"{EscapeConsoleString(state.Path)}\" sent_frames={state.SentFrames} " +
                $"skipped_sender_frames={state.SkippedSenderFrames} sent_packets={state.SentPackets} " +
                $"failed_packets={state.FailedPackets} last_rc={state.LastReturnCode}");
            _voiceTestPlayback = null;
        }
    }

    private bool TryResolveLiveVoiceSender(
        VoiceSpeakerPlayback speaker,
        out CCSPlayerController sender)
    {
        var current = Utilities.GetPlayerFromSlot(speaker.Slot);
        if (IsLiveVoiceSender(speaker, current))
        {
            sender = current!;
            return true;
        }

        if (speaker.FollowsLoadedReplay)
        {
            var reboundSlot = int.MaxValue;
            CCSPlayerController? reboundSender = null;
            foreach (var (slot, replay) in _session.LoadedReplays)
            {
                if (slot >= reboundSlot || replay.SteamId != speaker.Xuid)
                    continue;

                var candidate = Utilities.GetPlayerFromSlot(slot);
                if (!IsLiveVoiceSender(speaker, candidate, slot))
                    continue;

                reboundSlot = slot;
                reboundSender = candidate;
            }

            if (reboundSender != null)
            {
                speaker.Rebind(reboundSlot);
                sender = reboundSender;
                return true;
            }
        }

        sender = null!;
        return false;
    }

    private bool IsLiveVoiceSender(
        VoiceSpeakerPlayback speaker,
        CCSPlayerController? sender,
        int? slotOverride = null)
    {
        if (sender is not { IsValid: true } || !IsReplayTargetBot(sender))
            return false;
        if (speaker.ExpectedTeam.HasValue && sender.Team != speaker.ExpectedTeam.Value)
            return false;
        if (!speaker.FollowsLoadedReplay)
            return true;

        var slot = slotOverride ?? speaker.Slot;
        return _session.LoadedReplays.TryGetValue(slot, out var replay) &&
               replay.SteamId == speaker.Xuid;
    }

    private static int SendVoiceFrameSlice(
        int recipientSlot,
        int senderClient,
        ulong senderXuid,
        IntPtr audio,
        int audioBytes,
        int sampleRate,
        float voiceLevel,
        int sequenceBytes,
        int sectionNumber,
        int uncompressedSampleOffset,
        uint numPackets,
        uint[] packetOffsets,
        int tick,
        int audibleMask)
    {
        if (recipientSlot is < 0 or >= MaxPlayerSlots ||
            senderClient < 0 ||
            audio == IntPtr.Zero ||
            audioBytes <= 0)
        {
            return -2;
        }

        try
        {
            return BotControllerSendVoiceFrameSlice(
                recipientSlot,
                senderClient,
                senderXuid,
                audio,
                audioBytes,
                sampleRate,
                voiceLevel,
                sequenceBytes,
                sectionNumber,
                uncompressedSampleOffset,
                numPackets,
                packetOffsets,
                packetOffsets.Length,
                tick,
                audibleMask);
        }
        catch (EntryPointNotFoundException)
        {
            return -7;
        }
        catch
        {
            return -8;
        }
    }

    private bool StopVoiceTestPlayback(string reason, bool printSummary = true)
    {
        var state = _voiceTestPlayback;
        if (state == null)
            return false;

        _voiceTestPlayback = null;
        if (printSummary)
        {
            Server.PrintToConsole(
                $"dtr: voice test stopped reason={reason} path=\"{EscapeConsoleString(state.Path)}\" " +
                $"sent_frames={state.SentFrames} skipped_sender_frames={state.SkippedSenderFrames} " +
                $"sent_packets={state.SentPackets} failed_packets={state.FailedPackets} last_rc={state.LastReturnCode}");
        }
        return true;
    }

    private int AllocateVoiceSectionBase(int frameCount)
    {
        if (_nextVoiceSectionNumber > int.MaxValue - Math.Max(frameCount, 1) - 32)
            _nextVoiceSectionNumber = 1;

        var sectionBase = _nextVoiceSectionNumber;
        _nextVoiceSectionNumber += Math.Max(frameCount, 1) + 16;
        return sectionBase;
    }

}
