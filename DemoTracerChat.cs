using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using System.Globalization;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const float ChatPlaybackEpsilonSeconds = 0.010f;

    private static readonly HashSet<char> ChatConsoleSeparators = [';', '`', '\u001b'];

    private bool _chatAutoEnabled = true;
    private List<ReplayChatMessage> _loadedChatMessages = new();
    private int _loadedChatRound = -1;
    private int _loadedChatRecordingStartTick;
    private int _loadedChatLiveStartTick;
    private float _loadedChatTickRate;
    private ChatPlaybackState? _chatPlayback;

    [ConsoleCommand("dtr_chat_auto", "dtr_chat_auto [status|on|off]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void ChatAutoCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
        {
            var mode = command.GetArg(1);
            if (mode.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                command.ReplyToCommand(FormatChatAutoStatus());
                return;
            }

            _chatAutoEnabled = ParseOnOff(mode, _chatAutoEnabled);
        }

        command.ReplyToCommand(FormatChatAutoStatus());
    }

    [ConsoleCommand("dtr_chat_test", "dtr_chat_test <loaded|any|slot> [all|team] <message>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void ChatTestCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 3)
        {
            command.ReplyToCommand("usage: dtr_chat_test <loaded|any|slot> [all|team] <message>");
            return;
        }

        if (!TryResolveChatTestSender(command.GetArg(1), out var sender, out var error))
        {
            command.ReplyToCommand($"[DTR ERR] chat test target unavailable: {error}");
            return;
        }

        var scope = ReplayChatScope.All;
        var textStart = 2;
        if (textStart < command.ArgCount &&
            TryParseReplayChatScope(command.GetArg(textStart), out var parsedScope) &&
            parsedScope != ReplayChatScope.Server)
        {
            scope = parsedScope;
            textStart++;
        }

        var text = SanitizeReplayChatText(string.Join(
            " ",
            Enumerable.Range(textStart, command.ArgCount - textStart).Select(command.GetArg)));
        if (string.IsNullOrWhiteSpace(text))
        {
            command.ReplyToCommand("[DTR ERR] chat test message is empty");
            return;
        }

        var sent = TrySendBotChatCommand(sender, scope, text, out var detail);
        command.ReplyToCommand(
            sent
                ? $"[DTR OK] chat test called slot={sender.Slot} method={detail}"
                : $"[DTR ERR] chat test failed slot={sender.Slot} method={detail}");
    }

    private string ConfigureLoadedAutoChat(
        int round,
        ManifestRound? roundMetadata,
        float manifestTickRate)
    {
        ClearLoadedAutoChat();
        if (roundMetadata?.ChatMessages is not { Count: > 0 } messages)
            return string.Empty;

        _loadedChatMessages = messages
            .Where(IsValidReplayChatMessage)
            .OrderBy(message => message.Tick)
            .ThenBy(message => message.SenderSteamId)
            .ToList();
        if (_loadedChatMessages.Count == 0)
        {
            ClearLoadedAutoChat();
            return string.Empty;
        }

        _loadedChatRound = round;
        _loadedChatRecordingStartTick = roundMetadata.RecordingStartTick;
        _loadedChatLiveStartTick = roundMetadata.StartTick;
        _loadedChatTickRate = manifestTickRate > 0.0f ? manifestTickRate : 0.0f;
        return _loadedChatMessages.Count.ToString(CultureInfo.InvariantCulture);
    }

    private void ClearLoadedAutoChat()
    {
        StopChatPlayback("clear_chat");
        _loadedChatMessages = new List<ReplayChatMessage>();
        _loadedChatRound = -1;
        _loadedChatRecordingStartTick = 0;
        _loadedChatLiveStartTick = 0;
        _loadedChatTickRate = 0.0f;
    }

    private string TryStartLoadedAutoChatPlayback(
        ReplayStartAnchor anchor,
        float? freezeTimeSeconds,
        int startedSlots)
    {
        if (!_chatAutoEnabled || startedSlots <= 0 || _loadedChatMessages.Count == 0)
            return string.Empty;

        var tickRate = _loadedChatTickRate > 0.0f ? _loadedChatTickRate : 64.0f;
        var anchorTick = ChatAnchorTick(anchor, freezeTimeSeconds, tickRate);
        var frames = BuildChatPlaybackFrames(_loadedChatMessages, anchorTick, tickRate);
        if (frames.Count == 0)
            return "; chat_auto=no_messages";

        StopChatPlayback("chat_auto_replace");
        _chatPlayback = new ChatPlaybackState(Server.CurrentTime, frames);
        return
            $"; chat_auto=started messages={frames.Count}/{_loadedChatMessages.Count} " +
            $"anchor={anchor.ToString().ToLowerInvariant()} offset_tick={anchorTick.ToString(CultureInfo.InvariantCulture)}";
    }

    private int ChatAnchorTick(ReplayStartAnchor anchor, float? freezeTimeSeconds, float tickRate)
    {
        var anchorTick = _loadedChatLiveStartTick > 0
            ? _loadedChatLiveStartTick
            : _loadedChatRecordingStartTick;
        if (anchor == ReplayStartAnchor.FreezePreroll &&
            _loadedChatLiveStartTick > 0 &&
            _loadedChatRecordingStartTick > 0)
        {
            var prerollSeconds = LoadedReplayChatPrerollSeconds(freezeTimeSeconds, tickRate);
            var prerollTicks = (int)MathF.Round(prerollSeconds * tickRate);
            anchorTick = Math.Max(_loadedChatRecordingStartTick, _loadedChatLiveStartTick - prerollTicks);
        }

        return anchorTick;
    }

    private float LoadedReplayChatPrerollSeconds(float? freezeTimeSeconds, float fallbackTickRate)
    {
        var maxRecordedPrerollSeconds = 0.0f;
        foreach (var replay in _loadedReplays.Values)
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

        if (_loadedChatLiveStartTick > 0 &&
            _loadedChatRecordingStartTick > 0 &&
            _loadedChatLiveStartTick > _loadedChatRecordingStartTick &&
            fallbackTickRate > 0.0f)
        {
            return (_loadedChatLiveStartTick - _loadedChatRecordingStartTick) / fallbackTickRate;
        }

        return maxRecordedPrerollSeconds;
    }

    private static List<ChatPlaybackFrame> BuildChatPlaybackFrames(
        IEnumerable<ReplayChatMessage> messages,
        int anchorTick,
        float tickRate)
    {
        var frames = new List<ChatPlaybackFrame>();
        if (tickRate <= 0.0f)
            return frames;

        foreach (var message in messages)
        {
            var text = SanitizeReplayChatText(message.Text);
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var playbackSeconds = Math.Max(0.0f, (message.Tick - anchorTick) / tickRate);
            frames.Add(new ChatPlaybackFrame(
                playbackSeconds,
                message.SenderSteamId,
                NormalizeReplayChatScope(message.Scope),
                text,
                SanitizeReplayChatName(message.SenderName)));
        }

        return frames;
    }

    private void ProcessChatPlayback()
    {
        var playback = _chatPlayback;
        if (playback == null)
            return;

        if (!_chatAutoEnabled)
        {
            StopChatPlayback("chat_auto_off");
            return;
        }

        var elapsed = Server.CurrentTime - playback.StartTime;
        while (playback.NextFrameIndex < playback.Frames.Count &&
               playback.Frames[playback.NextFrameIndex].PlaybackSeconds <= elapsed + ChatPlaybackEpsilonSeconds)
        {
            var frame = playback.Frames[playback.NextFrameIndex++];
            if (DispatchReplayChatFrame(frame))
                playback.SentMessages++;
            else
                playback.SkippedMessages++;
        }

        if (playback.NextFrameIndex >= playback.Frames.Count)
            _chatPlayback = null;
    }

    private bool DispatchReplayChatFrame(ChatPlaybackFrame frame)
    {
        if (frame.Scope == ReplayChatScope.Server)
        {
            var speaker = string.IsNullOrWhiteSpace(frame.SenderName)
                ? "server"
                : frame.SenderName;
            Server.PrintToChatAll($"[DTR] {speaker}: {frame.Text}");
            Server.PrintToConsole($"[DTR CHAT] {speaker}: {frame.Text}");
            return true;
        }

        if (!TryResolveChatSender(frame.SenderSteamId, out var sender))
            return false;

        return TrySendBotChatCommand(sender, frame.Scope, frame.Text, out _);
    }

    private bool TrySendBotChatCommand(
        CCSPlayerController sender,
        ReplayChatScope scope,
        string text,
        out string detail)
    {
        var commandName = scope == ReplayChatScope.Team ? "say_team" : "say";
        var command = $"{commandName} {QuoteClientCommandArgument(text)}";
        var scopeName = scope.ToString().ToLowerInvariant();

        try
        {
            sender.ExecuteClientCommandFromServer(command);
            detail = "execute_from_server";
            Server.PrintToConsole($"dtr: chat sent slot={sender.Slot} method={detail} scope={scopeName}");
            return true;
        }
        catch (Exception ex)
        {
            detail = $"execute_from_server_failed:{ex.Message}";
            Server.PrintToConsole(
                $"dtr: chat execute_from_server failed slot={sender.Slot} scope={scopeName}: {ex.Message}");
            return false;
        }
    }

    private bool TryResolveChatSender(ulong senderSteamId, out CCSPlayerController sender)
    {
        foreach (var (slot, replay) in _loadedReplays)
        {
            if (replay.SteamId != senderSteamId || !IsReplaySlotStillSafe(slot))
                continue;
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is { IsValid: true } && IsReplayTargetBot(player))
            {
                sender = player;
                return true;
            }
        }

        sender = null!;
        return false;
    }

    private bool TryResolveChatTestSender(
        string rawTarget,
        out CCSPlayerController sender,
        out string error)
    {
        if (rawTarget.Equals("loaded", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var slot in _loadedSlots)
            {
                if (!_loadedReplays.TryGetValue(slot, out var replay) ||
                    !IsReplaySlotStillSafe(slot))
                {
                    continue;
                }

                var player = Utilities.GetPlayerFromSlot(slot);
                if (player is { IsValid: true } && IsReplayTargetBot(player))
                {
                    sender = player;
                    error = string.Empty;
                    return true;
                }
            }

            sender = null!;
            error = "no loaded safe replay bot";
            return false;
        }

        if (rawTarget.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            var player = Utilities.GetPlayers()
                .FirstOrDefault(candidate =>
                    candidate is { IsValid: true } &&
                    IsReplayTargetBot(candidate) &&
                    candidate.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist);
            if (player != null)
            {
                sender = player;
                error = string.Empty;
                return true;
            }

            sender = null!;
            error = "no safe bot";
            return false;
        }

        if (int.TryParse(rawTarget, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSlot))
        {
            var player = Utilities.GetPlayerFromSlot(parsedSlot);
            if (player is { IsValid: true } && IsReplayTargetBot(player))
            {
                sender = player;
                error = string.Empty;
                return true;
            }

            sender = null!;
            error = $"slot {parsedSlot} is not a safe bot";
            return false;
        }

        sender = null!;
        error = $"invalid target \"{rawTarget}\"";
        return false;
    }

    private void StopChatPlayback(string reason)
    {
        if (_chatPlayback != null)
            Server.PrintToConsole($"dtr: stopped chat playback reason={reason}");
        _chatPlayback = null;
    }

    private string FormatChatAutoStatus()
    {
        var loaded = _loadedChatMessages.Count == 0
            ? "none"
            : $"round={_loadedChatRound} messages={_loadedChatMessages.Count}";
        var active = _chatPlayback == null
            ? "none"
            : $"sent={_chatPlayback.SentMessages} skipped={_chatPlayback.SkippedMessages}/{_chatPlayback.Frames.Count}";
        return $"[DTR OK] chat_auto={FormatOnOff(_chatAutoEnabled)} sender=execute_from_server loaded={loaded} active={active}";
    }

    private string FormatChatAutoStatusInline()
    {
        var loaded = _loadedChatMessages.Count == 0
            ? "none"
            : $"{_loadedChatRound}:{_loadedChatMessages.Count}";
        var active = _chatPlayback == null
            ? "none"
            : $"{_chatPlayback.SentMessages + _chatPlayback.SkippedMessages}/{_chatPlayback.Frames.Count}";
        return $"chat_auto={FormatOnOff(_chatAutoEnabled)} chat_sender=execute_from_server chat_loaded={loaded} chat_active={active}";
    }

    private static bool IsValidReplayChatMessage(ReplayChatMessage message)
        => message.Tick >= 0 &&
           !string.IsNullOrWhiteSpace(message.Text) &&
           (message.SenderSteamId != 0 ||
            NormalizeReplayChatScope(message.Scope) == ReplayChatScope.Server);

    private static ReplayChatScope NormalizeReplayChatScope(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "team" => ReplayChatScope.Team,
            "server" or "admin" => ReplayChatScope.Server,
            _ => ReplayChatScope.All
        };
    }

    private static bool TryParseReplayChatScope(string? value, out ReplayChatScope scope)
    {
        scope = NormalizeReplayChatScope(value);
        return value?.Trim().ToLowerInvariant() is "all" or "team" or "server" or "admin";
    }

    private static string SanitizeReplayChatText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var cleaned = new string(value
            .Trim()
            .Select(ch => ChatConsoleSeparators.Contains(ch) || char.IsControl(ch) ? ' ' : ch)
            .Take(256)
            .ToArray())
            .Trim();
        return CollapseWhitespace(cleaned);
    }

    private static string? SanitizeReplayChatName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var cleaned = new string(value
            .Trim()
            .Where(ch => !char.IsControl(ch))
            .Take(64)
            .ToArray())
            .Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string QuoteClientCommandArgument(string value)
        => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "'", StringComparison.Ordinal)}\"";

    private static string CollapseWhitespace(string value)
    {
        if (value.Length == 0)
            return string.Empty;
        var chars = new List<char>(value.Length);
        var lastWasSpace = false;
        foreach (var current in value)
        {
            if (char.IsWhiteSpace(current))
            {
                if (!lastWasSpace)
                    chars.Add(' ');
                lastWasSpace = true;
                continue;
            }

            chars.Add(current);
            lastWasSpace = false;
        }

        return new string(chars.ToArray()).Trim();
    }

    private enum ReplayChatScope
    {
        All,
        Team,
        Server
    }

    private sealed class ChatPlaybackState(float startTime, List<ChatPlaybackFrame> frames)
    {
        public float StartTime { get; } = startTime;
        public List<ChatPlaybackFrame> Frames { get; } = frames;
        public int NextFrameIndex { get; set; }
        public int SentMessages { get; set; }
        public int SkippedMessages { get; set; }
    }

    private sealed class ChatPlaybackFrame(
        float playbackSeconds,
        ulong senderSteamId,
        ReplayChatScope scope,
        string text,
        string? senderName)
    {
        public float PlaybackSeconds { get; } = playbackSeconds;
        public ulong SenderSteamId { get; } = senderSteamId;
        public ReplayChatScope Scope { get; } = scope;
        public string Text { get; } = text;
        public string? SenderName { get; } = senderName;
    }
}
