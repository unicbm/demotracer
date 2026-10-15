/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private LoadedVoiceClip? _soulDeathSound;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _soulDeathSoundTimer;

    private void LoadSoulDeathSound()
    {
        var path = Path.Combine(ModuleDirectory, "sounds", "demo-souls-death.dtv");
        if (!File.Exists(path))
        {
            Server.PrintToConsole("[Demo Souls] Death sound not installed: sounds/demo-souls-death.dtv");
            return;
        }
        if (TryLoadVoiceClip(path, Server.PrintToConsole, out var clip))
            _soulDeathSound = clip;
    }

    private void PlaySoulDeathSound(CCSPlayerController victim)
    {
        // One server-wide stinger at a time, even when several participants die together.
        if (_soulDeathSoundTimer != null || _soulDeathSound is not { } clip)
            return;
        if (!BotControllerNative.CanSendVoice)
        {
            Server.PrintToConsole("[Demo Souls] Death sound unavailable: native voice sender is not ready.");
            return;
        }
        var start = Server.CurrentTime;
        var nextFrame = 0;
        var section = AllocateVoiceSectionBase(clip.Frames.Count);
        var senderSlot = victim.Slot;
        var senderUserId = victim.UserId;
        var senderXuid = victim.SteamID;
        _soulDeathSoundTimer = AddTimer(0.01f, () =>
        {
            if (!IsSoulHuman(victim) || victim.UserId != senderUserId)
            {
                StopSoulDeathSound();
                return;
            }
            var recipients = Utilities.GetPlayers().Where(IsSoulHuman).ToArray();
            var handle = GCHandle.Alloc(clip.AudioPayload, GCHandleType.Pinned);
            try
            {
                while (nextFrame < clip.Frames.Count &&
                       clip.Frames[nextFrame].PlaybackSeconds <= Server.CurrentTime - start + VoicePlaybackEpsilonSeconds)
                {
                    var frame = clip.Frames[nextFrame++];
                    foreach (var recipient in recipients)
                    {
                        var rc = SendVoiceFrameSlice(recipient.Slot, senderSlot, senderXuid,
                            IntPtr.Add(handle.AddrOfPinnedObject(), frame.AudioOffset), frame.AudioLength,
                            frame.SampleRate, frame.VoiceLevel, frame.SequenceBytes, section,
                            frame.UncompressedSampleOffset, frame.NumPackets, frame.PacketOffsets, -1, 1);
                        if (rc != 0)
                        {
                            Server.PrintToConsole($"[Demo Souls] Death sound send failed: {rc}");
                            StopSoulDeathSound();
                            return;
                        }
                    }
                    section++;
                }
            }
            finally
            {
                handle.Free();
            }
            if (nextFrame == clip.Frames.Count)
                StopSoulDeathSound();
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StopSoulDeathSound()
    {
        _soulDeathSoundTimer?.Kill();
        _soulDeathSoundTimer = null;
    }
}
