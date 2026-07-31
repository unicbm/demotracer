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
    private bool TryBuildVoiceSpeakerMap(
        string mapping,
        LoadedVoiceClip clip,
        Action<string> reply,
        out Dictionary<ulong, VoiceSpeakerPlayback> speakers)
    {
        speakers = new Dictionary<ulong, VoiceSpeakerPlayback>();
        if (mapping.Equals("loaded", StringComparison.OrdinalIgnoreCase) ||
            mapping.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var xuid in UniqueVoiceXuids(clip).OrderBy(value => value))
            {
                var match = _session.LoadedReplays
                    .Where(entry => entry.Value.SteamId == xuid && IsReplaySlotStillSafe(entry.Key))
                    .Select(entry => entry.Key)
                    .OrderBy(slot => slot)
                    .FirstOrDefault(-1);
                if (match >= 0)
                {
                    speakers[xuid] = new VoiceSpeakerPlayback(
                        match,
                        match,
                        xuid,
                        0,
                        _session.LoadedReplays[match].ManifestTeam,
                        followsLoadedReplay: true);
                }
            }
        }
        else
        {
            foreach (var rawPart in mapping.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = rawPart.Contains('=') ? '=' : ':';
                var parts = rawPart.Split(separator, 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 ||
                    !ulong.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var xuid) ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot))
                {
                    reply($"[DTR ERR] invalid voice speaker mapping \"{rawPart}\"; expected xuid=slot");
                    return false;
                }
                speakers[xuid] = new VoiceSpeakerPlayback(
                    slot,
                    slot,
                    xuid,
                    0,
                    expectedTeam: null,
                    followsLoadedReplay: false);
            }
        }

        if (speakers.Count == 0)
        {
            reply($"[DTR ERR] no voice speaker mapping matched; speakers={DescribeVoiceSpeakers(clip)}");
            return false;
        }

        foreach (var (xuid, speaker) in speakers.OrderBy(entry => entry.Value.Slot))
        {
            var sender = Utilities.GetPlayerFromSlot(speaker.Slot);
            if (sender is not { IsValid: true } || !IsReplayTargetBot(sender))
            {
                reply(
                    $"[DTR ERR] voice speaker xuid={xuid} slot={speaker.Slot} is not a safe replay bot target");
                return false;
            }
        }

        var mappedSpeakers = speakers;
        var unmapped = UniqueVoiceXuids(clip)
            .Where(xuid => !mappedSpeakers.ContainsKey(xuid))
            .Take(5)
            .ToArray();
        if (unmapped.Length > 0)
        {
            reply(
                $"[DTR WARN] unmapped voice speakers will be skipped: {string.Join(",", unmapped)}");
        }

        return true;
    }

    private bool TryPrepareVoiceMixFrames(
        LoadedVoiceClip clip,
        Dictionary<ulong, VoiceSpeakerPlayback> speakers,
        Action<string> reply,
        out List<VoiceClipRuntimeFrame> frames)
    {
        frames = clip.Frames
            .Where(frame => speakers.ContainsKey(frame.Xuid) ||
                            (frame.Xuid == 0 && speakers.ContainsKey(clip.Manifest.SelectedXuid)))
            .ToList();
        if (frames.Count == 0)
        {
            reply("[DTR ERR] no voice frames matched mapped speakers");
            return false;
        }

        foreach (var group in frames.GroupBy(frame => frame.Xuid == 0 ? clip.Manifest.SelectedXuid : frame.Xuid))
        {
            if (speakers.TryGetValue(group.Key, out var speaker))
                speaker.NextSectionNumber = AllocateVoiceSectionBase(group.Count());
        }

        return true;
    }

    private string ConfigureLoadedAutoVoiceClip(
        string manifestPath,
        int round,
        ManifestRound? roundMetadata,
        float manifestTickRate)
    {
        ClearLoadedAutoVoiceClip();
        if (!TryResolveVoiceSidecarForRound(manifestPath, round, out var clipPath))
            return string.Empty;

        _loadedVoiceClipPath = clipPath;
        _loadedVoiceRound = round;
        _loadedVoiceRecordingStartTick = roundMetadata?.RecordingStartTick ?? 0;
        _loadedVoiceLiveStartTick = roundMetadata?.StartTick ?? 0;
        _loadedVoiceTickRate = manifestTickRate > 0.0f
            ? manifestTickRate
            : 0.0f;
        if (_voiceAutoEnabled)
            QueueVoiceClipPreload(clipPath);
        return Path.GetFileName(clipPath);
    }

    private void ClearLoadedAutoVoiceClip()
    {
        CancelVoiceClipPreload();
        _loadedVoiceClipPath = string.Empty;
        _loadedVoiceRound = -1;
        _loadedVoiceRecordingStartTick = 0;
        _loadedVoiceLiveStartTick = 0;
        _loadedVoiceTickRate = 0.0f;
    }

    private string TryStartLoadedAutoVoicePlayback(
        ReplayStartAnchor anchor,
        float? freezeTimeSeconds,
        int startedSlots)
    {
        if (!_voiceAutoEnabled ||
            startedSlots <= 0 ||
            string.IsNullOrWhiteSpace(_loadedVoiceClipPath))
        {
            return string.Empty;
        }

        if (!BotControllerNative.CanSendVoice)
            return $"; voice_auto=unavailable {BotControllerNative.RuntimeSummary}";

        var diagnostics = new List<string>();
        void Collect(string message) => diagnostics.Add(message);

        if (!TryLoadVoiceClip(_loadedVoiceClipPath, Collect, out var clip))
            return $"; voice_auto=load_failed {FirstVoiceDiagnostic(diagnostics)}";
        if (anchor == ReplayStartAnchor.FreezePreroll &&
            _loadedVoiceLiveStartTick > 0 &&
            clip.Manifest.StartTick >= _loadedVoiceLiveStartTick)
        {
            return
                $"; voice_auto=deferred_live file={Path.GetFileName(clip.Path)} " +
                $"clip_start={clip.Manifest.StartTick} live_start={_loadedVoiceLiveStartTick}";
        }
        if (!TryBuildVoiceSpeakerMap("loaded", clip, Collect, out var speakers))
            return $"; voice_auto=map_failed {FirstVoiceDiagnostic(diagnostics)}";
        if (!TryPrepareVoiceMixFrames(clip, speakers, Collect, out var frames))
            return $"; voice_auto=frames_failed {FirstVoiceDiagnostic(diagnostics)}";

        var recipients = ResolveAllVoiceRecipients();
        if (recipients.Count == 0)
            return "; voice_auto=no_human_recipients";

        if (anchor == ReplayStartAnchor.Live &&
            _voiceTestPlayback is { StartedFromFreezePreroll: true } activePlayback &&
            string.Equals(activePlayback.Path, clip.Path, StringComparison.OrdinalIgnoreCase) &&
            activePlayback.NextFrameIndex < activePlayback.Frames.Count)
        {
            return
                $"; voice_auto=continued file={Path.GetFileName(clip.Path)} " +
                $"frames={activePlayback.NextFrameIndex}/{activePlayback.Frames.Count}";
        }

        var (startTime, initialFrameIndex, offsetSeconds) =
            ComputeAutoVoiceStart(clip.Manifest, frames, anchor, freezeTimeSeconds);
        StopVoiceTestPlayback("voice_auto_replace", printSummary: false);
        _voiceTestPlayback = new VoiceClipPlaybackState(
            clip.Path,
            clip.Manifest.TickRate,
            startTime,
            speakers,
            defaultSpeakerXuid: 0,
            clip.AudioPayload,
            frames,
            recipients,
            startedFromFreezePreroll: anchor == ReplayStartAnchor.FreezePreroll)
        {
            NextFrameIndex = initialFrameIndex
        };

        var fileName = Path.GetFileName(clip.Path);
        return
            $"; voice_auto=started file={fileName} speakers={speakers.Count} frames={frames.Count}/{clip.Frames.Count} " +
            $"recipients={FormatSlotList(recipients)} anchor={anchor.ToString().ToLowerInvariant()} offset={offsetSeconds.ToString("F2", CultureInfo.InvariantCulture)}s";
    }

    private (float StartTime, int InitialFrameIndex, float OffsetSeconds) ComputeAutoVoiceStart(
        VoiceClipManifest manifest,
        IReadOnlyList<VoiceClipRuntimeFrame> frames,
        ReplayStartAnchor anchor,
        float? freezeTimeSeconds)
    {
        var tickRate = manifest.TickRate > 0.0f
            ? manifest.TickRate
            : _loadedVoiceTickRate > 0.0f
                ? _loadedVoiceTickRate
                : DefaultVoiceSampleRate;
        var clipStartTick = manifest.StartTick;
        var anchorDemoTick = _loadedVoiceLiveStartTick;
        if (anchor == ReplayStartAnchor.FreezePreroll && _loadedVoiceLiveStartTick > 0)
        {
            var prerollSeconds = LoadedReplayVoicePrerollSeconds(freezeTimeSeconds, tickRate);
            var prerollTicks = (int)MathF.Round(prerollSeconds * tickRate);
            anchorDemoTick = Math.Max(_loadedVoiceRecordingStartTick, _loadedVoiceLiveStartTick - prerollTicks);
        }

        if (clipStartTick <= 0 || anchorDemoTick <= 0 || tickRate <= 0.0f)
            return (Server.CurrentTime, 0, 0.0f);

        var offsetSeconds = (anchorDemoTick - clipStartTick) / tickRate;
        var initialFrameIndex = offsetSeconds > 0.0f
            ? FirstVoiceFrameAtOrAfter(frames, offsetSeconds)
            : 0;
        return (Server.CurrentTime - offsetSeconds, initialFrameIndex, offsetSeconds);
    }

    private float LoadedReplayVoicePrerollSeconds(float? freezeTimeSeconds, float fallbackTickRate)
    {
        var maxRecordedPrerollSeconds = 0.0f;
        foreach (var replay in _session.LoadedReplays.Values)
        {
            var tickRate = replay.TickRate > 0.0f ? replay.TickRate : fallbackTickRate;
            if (replay.PlayStartTickIndex == 0 || tickRate <= 0.0f)
                continue;
            maxRecordedPrerollSeconds = Math.Max(
                maxRecordedPrerollSeconds,
                replay.PlayStartTickIndex / tickRate);
        }

        if (freezeTimeSeconds.HasValue && freezeTimeSeconds.Value > 0.0f)
            return Math.Min(freezeTimeSeconds.Value, maxRecordedPrerollSeconds);

        if (_loadedVoiceLiveStartTick > 0 &&
            _loadedVoiceRecordingStartTick > 0 &&
            _loadedVoiceLiveStartTick > _loadedVoiceRecordingStartTick &&
            fallbackTickRate > 0.0f)
        {
            return (_loadedVoiceLiveStartTick - _loadedVoiceRecordingStartTick) / fallbackTickRate;
        }

        return maxRecordedPrerollSeconds;
    }

    private static int FirstVoiceFrameAtOrAfter(IReadOnlyList<VoiceClipRuntimeFrame> frames, float offsetSeconds)
    {
        var threshold = Math.Max(0.0f, offsetSeconds - VoicePlaybackEpsilonSeconds);
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].PlaybackSeconds >= threshold)
                return i;
        }

        return frames.Count;
    }

    private static string FirstVoiceDiagnostic(IReadOnlyList<string> diagnostics)
        => diagnostics.Count == 0 ? string.Empty : diagnostics[0];

    private bool TryResolveVoiceSidecarForRound(
        string manifestPath,
        int round,
        out string clipPath)
    {
        clipPath = string.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in CandidateVoiceSidecarDirectories(manifestPath))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            var root = Path.GetFullPath(directory);
            foreach (var fileName in ExactVoiceSidecarFileNames(round))
            {
                var candidate = Path.Combine(root, fileName);
                if (!seen.Add(candidate) || !File.Exists(candidate))
                    continue;
                if (LooksLikeVoiceDtvClip(candidate))
                {
                    clipPath = candidate;
                    return true;
                }
            }

            IEnumerable<string> matches;
            try
            {
                matches = Directory.EnumerateFiles(root, "*.dtv", SearchOption.TopDirectoryOnly)
                    .Where(path => seen.Add(path))
                    .Where(path => VoiceSidecarFileNameMatchesRound(Path.GetFileName(path), round))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .Take(32)
                    .ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var candidate in matches)
            {
                if (LooksLikeVoiceDtvClip(candidate))
                {
                    clipPath = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private IEnumerable<string> CandidateVoiceSidecarDirectories(string manifestPath)
    {
        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        if (!string.IsNullOrWhiteSpace(manifestDir))
        {
            yield return Path.Combine(manifestDir, "voice");
            yield return manifestDir;
        }

        foreach (var gameDir in CandidateGameDirectories())
        {
            yield return Path.Combine(gameDir, "voice");
            yield return gameDir;
        }
    }

    private static IEnumerable<string> ExactVoiceSidecarFileNames(int round)
    {
        var plain = round.ToString(CultureInfo.InvariantCulture);
        var padded = round.ToString("D2", CultureInfo.InvariantCulture);
        yield return $"round{padded}.dtv";
        yield return $"round{plain}.dtv";
        yield return $"round{padded}_all.dtv";
        yield return $"round{plain}_all.dtv";
        yield return $"voice_round{padded}.dtv";
        yield return $"voice_round{plain}.dtv";
        yield return $"voice_round{padded}_all.dtv";
        yield return $"voice_round{plain}_all.dtv";
        yield return $"demotracer_voice_round{padded}.dtv";
        yield return $"demotracer_voice_round{plain}.dtv";
        yield return $"demotracer_voice_round{padded}_all.dtv";
        yield return $"demotracer_voice_round{plain}_all.dtv";
    }

    private static bool VoiceSidecarFileNameMatchesRound(string fileName, int round)
    {
        if (round < 0)
            return false;
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var pattern = $@"(^|[^0-9])round0*{round.ToString(CultureInfo.InvariantCulture)}([^0-9]|$)";
        return Regex.IsMatch(stem, pattern, RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeVoiceDtvClip(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < VoiceDtvMagicBytes.Length)
                return false;
            Span<byte> magic = stackalloc byte[VoiceDtvMagicBytes.Length];
            return stream.Read(magic) == VoiceDtvMagicBytes.Length &&
                   magic.SequenceEqual(VoiceDtvMagicBytes);
        }
        catch
        {
            return false;
        }
    }

    private static List<int> ResolveAllVoiceRecipients()
        => Utilities.GetPlayers()
            .Where(IsVoiceRecipient)
            .Select(candidate => candidate.Slot)
            .Distinct()
            .OrderBy(slot => slot)
            .ToList();

    private string FormatVoiceAutoStatus()
    {
        var loaded = string.IsNullOrWhiteSpace(_loadedVoiceClipPath)
            ? "none"
            : $"round={_loadedVoiceRound} file={Path.GetFileName(_loadedVoiceClipPath)}";
        var active = _voiceTestPlayback == null
            ? "none"
            : $"file={Path.GetFileName(_voiceTestPlayback.Path)} speakers={_voiceTestPlayback.Speakers.Count} sent={_voiceTestPlayback.SentFrames}/{_voiceTestPlayback.Frames.Count}";
        return $"[DTR OK] voice_auto={FormatOnOff(_voiceAutoEnabled)} loaded={loaded} active={active}";
    }

    private string FormatVoiceAutoStatusInline()
    {
        var loaded = string.IsNullOrWhiteSpace(_loadedVoiceClipPath)
            ? "none"
            : $"{_loadedVoiceRound}:{Path.GetFileName(_loadedVoiceClipPath)}";
        var active = _voiceTestPlayback == null
            ? "none"
            : $"{Path.GetFileName(_voiceTestPlayback.Path)}:{_voiceTestPlayback.SentFrames}/{_voiceTestPlayback.Frames.Count}";
        return $"voice_auto={FormatOnOff(_voiceAutoEnabled)} voice_loaded={loaded} voice_active={active}";
    }

    private static IEnumerable<ulong> UniqueVoiceXuids(LoadedVoiceClip clip)
        => clip.Frames
            .Select(frame => frame.Xuid == 0 ? clip.Manifest.SelectedXuid : frame.Xuid)
            .Where(xuid => xuid != 0)
            .Distinct();

    private static string DescribeVoiceSpeakers(LoadedVoiceClip clip)
    {
        var speakers = clip.Manifest.Speakers.Count > 0
            ? clip.Manifest.Speakers.Select(speaker => $"{speaker.Xuid}:client{speaker.Client}:frames{speaker.FrameCount}")
            : clip.Frames
                .GroupBy(frame => frame.Xuid)
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Key}:frames{group.Count()}");
        return string.Join(",", speakers.Take(12));
    }

    private bool TryResolveVoiceRecipients(
        CCSPlayerController? caller,
        CommandInfo command,
        int argIndex,
        out List<int> recipients)
    {
        recipients = new List<int>();
        if (command.ArgCount <= argIndex)
        {
            if (caller is { IsValid: true } liveCaller && IsVoiceRecipient(liveCaller))
            {
                recipients.Add(liveCaller.Slot);
                return true;
            }

            command.ReplyToCommand("usage: dtr_voice_test <voice_clip.dtv> <sender_slot> [recipient_slot|all]");
            return false;
        }

        var arg = command.GetArg(argIndex);
        if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            recipients = Utilities.GetPlayers()
                .Where(IsVoiceRecipient)
                .Select(candidate => candidate.Slot)
                .Distinct()
                .OrderBy(slot => slot)
                .ToList();
            if (recipients.Count == 0)
            {
                command.ReplyToCommand("[DTR ERR] no live human recipients");
                return false;
            }
            return true;
        }

        if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot) ||
            slot < 0 ||
            slot >= MaxPlayerSlots)
        {
            command.ReplyToCommand($"dtr: recipient slot must be an integer from 0 to {MaxPlayerSlots - 1}, or all");
            return false;
        }

        var recipient = Utilities.GetPlayerFromSlot(slot);
        if (!IsVoiceRecipient(recipient))
        {
            command.ReplyToCommand($"[DTR ERR] recipient slot {slot} is not a live human client");
            return false;
        }

        recipients.Add(slot);
        return true;
    }

    private static bool IsVoiceRecipient(CCSPlayerController? player)
        => player is { IsValid: true } && !player.IsHLTV && !player.IsBot;

    private static List<int> AudibleVoiceRecipientsForSpeaker(
        IReadOnlyList<int> recipientSlots,
        CCSPlayerController sender,
        CsTeam? expectedTeam)
        => recipientSlots
            .Where(slot => CanVoiceRecipientHearSpeaker(
                Utilities.GetPlayerFromSlot(slot),
                sender,
                expectedTeam))
            .Distinct()
            .OrderBy(slot => slot)
            .ToList();

    private static bool CanVoiceRecipientHearSpeaker(
        CCSPlayerController? recipient,
        CCSPlayerController sender,
        CsTeam? expectedTeam)
    {
        if (recipient is not { IsValid: true } || recipient.IsHLTV || recipient.IsBot)
            return false;

        if (IsObserverVoiceRecipient(recipient))
            return true;

        if (!IsTeamVoiceParticipant(recipient))
            return false;

        var speakerTeam = expectedTeam ?? sender.Team;
        if (!IsTeamVoiceParticipant(speakerTeam))
            return false;

        return recipient.Team == speakerTeam;
    }

    private static bool IsObserverVoiceRecipient(CCSPlayerController player)
        => player.Team == CsTeam.Spectator;

    private static bool IsTeamVoiceParticipant(CCSPlayerController player)
        => IsTeamVoiceParticipant(player.Team);

    private static bool IsTeamVoiceParticipant(CsTeam team)
        => team is CsTeam.Terrorist or CsTeam.CounterTerrorist;

    private static string FormatSlotList(IReadOnlyList<int> slots)
        => slots.Count == 0 ? "none" : string.Join(",", slots);

}
