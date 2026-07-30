using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CounterStrikeSharp.API;
using DemoTracerBotHiderApi;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const float BotHiderLeaseHeartbeatSeconds = 1.0f;
    private const float BotHiderLeaseRetrySeconds = 1.0f;
    private readonly Dictionary<int, string> _companionCrosshairOverrides = new();
    private readonly Dictionary<int, BotHiderPresentationEvidence> _retainedBotHiderPresentation = new();
    private readonly Dictionary<int, ulong> _activeBotHiderReplaySteamIds = new();
    private string _botHiderPresentationLeaseToken = string.Empty;
    private string _botHiderPresentationSignature = string.Empty;
    private string _lastBotHiderPresentationError = string.Empty;
    private float _nextBotHiderLeaseHeartbeatAt;
    private float _nextBotHiderLeaseRetryAt;
    private int _botHiderPresentationTransitionDepth;

    private void BeginBotHiderPresentationTransition()
    {
        _botHiderPresentationTransitionDepth++;
    }

    private void EndBotHiderPresentationTransition()
    {
        if (_botHiderPresentationTransitionDepth <= 0)
            return;
        if (--_botHiderPresentationTransitionDepth > 0)
            return;

        _ = SyncBotHiderPresentationLease(announce: false);
    }

    private void EnsureBotHiderPresentationLease()
    {
        if (_botHiderPresentationTransitionDepth > 0)
            return;

        if (_loadedSlots.Count == 0 &&
            _retainedBotHiderPresentation.Count == 0 &&
            _companionCrosshairOverrides.Count == 0)
        {
            ReleaseBotHiderPresentationLease("no_overrides");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_botHiderPresentationLeaseToken) &&
            Server.CurrentTime >= _nextBotHiderLeaseHeartbeatAt)
        {
            _nextBotHiderLeaseHeartbeatAt = Server.CurrentTime + BotHiderLeaseHeartbeatSeconds;
            if (!_botHiderBridge.Heartbeat(_botHiderPresentationLeaseToken))
            {
                _botHiderPresentationLeaseToken = string.Empty;
                _botHiderPresentationSignature = string.Empty;
                _activeBotHiderReplaySteamIds.Clear();
                _nextBotHiderLeaseRetryAt = Server.CurrentTime;
            }
        }

        if (Server.CurrentTime >= _nextBotHiderLeaseRetryAt &&
            (string.IsNullOrWhiteSpace(_botHiderPresentationLeaseToken) ||
             !string.IsNullOrWhiteSpace(_lastBotHiderPresentationError)))
        {
            SyncBotHiderPresentationLease(announce: false);
        }
    }

    private bool SyncBotHiderPresentationLease(bool announce)
    {
        if (_botHiderPresentationTransitionDepth > 0)
            return true;

        var wantsPresentation = WantsBotHiderPresentationLease();
        if (!wantsPresentation)
        {
            ReleaseBotHiderPresentationLease("no_requested_overrides");
            return true;
        }
        if (!_botHiderBridge.IsAvailable())
        {
            ReleaseBotHiderPresentationLease("provider_unavailable");
            _nextBotHiderLeaseRetryAt = Server.CurrentTime + BotHiderLeaseRetrySeconds;
            ReportBotHiderPresentationError("provider_unavailable", announce);
            return false;
        }

        var requests = BuildBotHiderPresentationOverrides(out var signature);
        if (requests.Length == 0)
        {
            ReleaseBotHiderPresentationLease("no_managed_override_slots");
            _nextBotHiderLeaseRetryAt = Server.CurrentTime + BotHiderLeaseRetrySeconds;
            ReportBotHiderPresentationError("no_managed_override_slots", announce);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_botHiderPresentationLeaseToken) &&
            signature.Equals(_botHiderPresentationSignature, StringComparison.Ordinal))
        {
            if (_botHiderBridge.Heartbeat(_botHiderPresentationLeaseToken))
            {
                _lastBotHiderPresentationError = string.Empty;
                _nextBotHiderLeaseRetryAt = 0.0f;
                _nextBotHiderLeaseHeartbeatAt = Server.CurrentTime + BotHiderLeaseHeartbeatSeconds;
                return true;
            }

            // The provider can revoke a multi-slot lease when one bot gets a
            // new userid/incarnation. Recover in this same synchronization pass
            // instead of treating the stale local token as a healthy lease.
            _botHiderPresentationLeaseToken = string.Empty;
            _botHiderPresentationSignature = string.Empty;
            _activeBotHiderReplaySteamIds.Clear();
            _nextBotHiderLeaseHeartbeatAt = 0.0f;
        }

        BotHiderPresentationLeaseResult result;
        if (string.IsNullOrWhiteSpace(_botHiderPresentationLeaseToken))
        {
            result = _botHiderBridge.Acquire(
                DemoTracerBotHiderContract.DemoTracerOwner,
                requests);
            if (!result.Ok && result.Reason.StartsWith("slot_leased:", StringComparison.Ordinal))
            {
                _ = _botHiderBridge.ReleaseOwner(DemoTracerBotHiderContract.DemoTracerOwner);
                result = _botHiderBridge.Acquire(
                    DemoTracerBotHiderContract.DemoTracerOwner,
                    requests);
            }
        }
        else
        {
            result = _botHiderBridge.Replace(_botHiderPresentationLeaseToken, requests);
            if (!result.Ok &&
                result.Reason.Equals("lease_not_found", StringComparison.Ordinal))
            {
                _botHiderPresentationLeaseToken = string.Empty;
                _botHiderPresentationSignature = string.Empty;
                result = _botHiderBridge.Acquire(
                    DemoTracerBotHiderContract.DemoTracerOwner,
                    requests);
                if (!result.Ok &&
                    result.Reason.StartsWith("slot_leased:", StringComparison.Ordinal))
                {
                    _ = _botHiderBridge.ReleaseOwner(DemoTracerBotHiderContract.DemoTracerOwner);
                    result = _botHiderBridge.Acquire(
                        DemoTracerBotHiderContract.DemoTracerOwner,
                        requests);
                }
            }
        }

        if (!result.Ok)
        {
            _nextBotHiderLeaseRetryAt = Server.CurrentTime + BotHiderLeaseRetrySeconds;
            ReportBotHiderPresentationError(result.Reason, announce);
            return false;
        }

        _botHiderPresentationLeaseToken = result.LeaseToken;
        _botHiderPresentationSignature = signature;
        _activeBotHiderReplaySteamIds.Clear();
        foreach (var request in requests)
        {
            if (request.SteamId is { } steamId && steamId != 0)
                _activeBotHiderReplaySteamIds[request.Slot] = steamId;
        }
        _lastBotHiderPresentationError = string.Empty;
        _nextBotHiderLeaseHeartbeatAt = Server.CurrentTime + BotHiderLeaseHeartbeatSeconds;
        _nextBotHiderLeaseRetryAt = 0.0f;
        if (announce)
        {
            Server.PrintToConsole(
                $"dtr: BotHider presentation lease active slots={string.Join(',', result.Slots)} " +
                $"provider_epoch={result.ProviderEpoch}");
        }
        return true;
    }

    private bool WantsBotHiderPresentationLease()
    {
        if (_companionCrosshairOverrides.Count > 0)
            return true;

        foreach (var slot in _loadedSlots)
        {
            if (!_loadedReplays.TryGetValue(slot, out var replay))
            {
                continue;
            }

            if (_replayIdentityMode != ReplayIdentityMode.Off ||
                (_crosshairAlignEnabled && HasCrosshairEvidence(replay.View)))
            {
                return true;
            }
        }

        foreach (var evidence in _retainedBotHiderPresentation.Values)
        {
            if (_replayIdentityMode != ReplayIdentityMode.Off ||
                (_crosshairAlignEnabled && HasCrosshairEvidence(evidence.View)))
            {
                return true;
            }
        }

        return false;
    }

    private void ReportBotHiderPresentationError(string reason, bool announce)
    {
        if (announce || !_lastBotHiderPresentationError.Equals(reason, StringComparison.Ordinal))
        {
            Server.PrintToConsole(
                $"dtr: BotHider presentation lease unavailable: {reason}");
        }
        _lastBotHiderPresentationError = reason;
    }

    private BotHiderPresentationOverride[] BuildBotHiderPresentationOverrides(out string signature)
    {
        var bySlot = new Dictionary<int, BotHiderPresentationOverride>();
        foreach (var slot in _loadedSlots.ToArray())
        {
            if (!_loadedReplays.TryGetValue(slot, out var replay))
                continue;
            AddBotHiderPresentationOverride(
                bySlot,
                new BotHiderPresentationEvidence(
                    slot,
                    replay.PlayerName,
                    replay.SteamId,
                    replay.RetentionRank,
                    replay.ScoreboardFlair,
                    replay.View));
        }

        foreach (var evidence in _retainedBotHiderPresentation.Values)
        {
            if (!bySlot.ContainsKey(evidence.Slot))
                AddBotHiderPresentationOverride(bySlot, evidence);
        }

        foreach (var pair in _companionCrosshairOverrides.ToArray())
        {
            var slot = pair.Key;
            if (bySlot.ContainsKey(slot) ||
                !IsReplaySlotStillSafe(slot) ||
                !_botHiderBridge.TryGetManagedSlot(slot, out var managed))
            {
                continue;
            }

            bySlot[slot] = new BotHiderPresentationOverride
            {
                Slot = slot,
                Incarnation = managed.Incarnation,
                CrosshairCode = pair.Value
            };
        }

        var requests = bySlot.Values.OrderBy(request => request.Slot).ToArray();
        var signatureBuilder = new StringBuilder();
        var provider = _botHiderBridge.GetProviderInfo();
        signatureBuilder.Append(provider?.ProviderEpoch).Append(':').Append(provider?.MapEpoch).Append('|');
        foreach (var request in requests)
        {
            signatureBuilder
                .Append(request.Slot).Append(':')
                .Append(request.Incarnation).Append(':')
                .Append(request.PlayerName).Append(':')
                .Append(request.SteamId).Append(':')
                .Append(request.ScoreboardFlair).Append(':')
                .Append(request.CrosshairCode).Append('|');
        }
        signature = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(signatureBuilder.ToString())));
        return requests;
    }

    private void AddBotHiderPresentationOverride(
        IDictionary<int, BotHiderPresentationOverride> bySlot,
        BotHiderPresentationEvidence evidence)
    {
        var slot = evidence.Slot;
        if (!IsReplaySlotStillSafe(slot) ||
            !_botHiderBridge.TryGetManagedSlot(slot, out var managed))
        {
            return;
        }

        var playerName = _replayIdentityMode == ReplayIdentityMode.Off
            ? null
            : DeriveBotHiderPresentationName(evidence.PlayerName);
        ulong? steamId = _replayIdentityMode is ReplayIdentityMode.Steam or ReplayIdentityMode.Avatar &&
                         evidence.SteamId != 0
            ? evidence.SteamId
            : null;
        uint? flair = ReplayIdentityShouldApplyScoreboardFlair() && evidence.ScoreboardFlair != null
            ? evidence.ScoreboardFlair.ItemDefIndex
            : null;
        string? crosshair = null;
        if (_companionCrosshairOverrides.TryGetValue(slot, out var companionCrosshair))
            crosshair = companionCrosshair;
        else if (_crosshairAlignEnabled && HasCrosshairEvidence(evidence.View))
            crosshair = evidence.View.CrosshairCode;

        if (playerName == null && !steamId.HasValue && !flair.HasValue && crosshair == null)
            return;

        bySlot[slot] = new BotHiderPresentationOverride
        {
            Slot = slot,
            Incarnation = managed.Incarnation,
            PlayerName = playerName,
            SteamId = steamId,
            ScoreboardFlair = flair,
            CrosshairCode = crosshair
        };
    }

    internal static string? DeriveBotHiderPresentationName(string? source)
    {
        var derived = BuildBoundedVisiblePresentationName(source);
        return derived.Length == 0 ? null : derived;
    }

    private static string BuildBoundedVisiblePresentationName(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return string.Empty;

        var visibleElements = new List<string>();
        var elements = StringInfo.GetTextElementEnumerator(source);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (!IsInvisiblePresentationElement(element))
                visibleElements.Add(element);
        }

        var first = 0;
        while (first < visibleElements.Count && IsWhitespacePresentationElement(visibleElements[first]))
            first++;

        var last = visibleElements.Count;
        while (last > first && IsWhitespacePresentationElement(visibleElements[last - 1]))
            last--;

        var boundedElements = new List<string>();
        var utf8Bytes = 0;
        for (var index = first; index < last; index++)
        {
            var element = visibleElements[index];
            var elementBytes = Encoding.UTF8.GetByteCount(element);
            if (utf8Bytes + elementBytes > DemoTracerBotHiderContract.MaxPlayerNameUtf8Bytes)
                break;

            boundedElements.Add(element);
            utf8Bytes += elementBytes;
        }

        while (boundedElements.Count > 0 && IsWhitespacePresentationElement(boundedElements[^1]))
            boundedElements.RemoveAt(boundedElements.Count - 1);

        return string.Concat(boundedElements);
    }

    private static bool IsInvisiblePresentationElement(string element)
    {
        foreach (var rune in element.EnumerateRunes())
        {
            if (!IsInvisiblePresentationRune(rune))
                return false;
        }

        return true;
    }

    private static bool IsWhitespacePresentationElement(string element)
    {
        var hasWhitespace = false;
        foreach (var rune in element.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                hasWhitespace = true;
                continue;
            }

            if (!IsInvisiblePresentationRune(rune))
                return false;
        }

        return hasWhitespace;
    }

    private static bool IsInvisiblePresentationRune(Rune rune)
    {
        return Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.Control or
            UnicodeCategory.Format or
            UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator or
            UnicodeCategory.Surrogate or
            UnicodeCategory.OtherNotAssigned or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark;
    }

    private void RetainLoadedBotHiderPresentation()
    {
        var replacement = new Dictionary<int, BotHiderPresentationEvidence>();
        foreach (var pair in _loadedReplays)
        {
            var replay = pair.Value;
            replacement[pair.Key] = new BotHiderPresentationEvidence(
                pair.Key,
                replay.PlayerName,
                replay.SteamId,
                replay.RetentionRank,
                replay.ScoreboardFlair,
                replay.View);
        }

        _retainedBotHiderPresentation.Clear();
        foreach (var pair in replacement)
            _retainedBotHiderPresentation[pair.Key] = pair.Value;
    }

    private void ForgetRetainedBotHiderPresentation(int slot)
        => _retainedBotHiderPresentation.Remove(slot);

    private void ClearRetainedBotHiderPresentation()
        => _retainedBotHiderPresentation.Clear();

    private void ReleaseBotHiderPresentationLease(string reason)
    {
        if (_botHiderPresentationTransitionDepth > 0)
            return;

        var token = _botHiderPresentationLeaseToken;
        _botHiderPresentationLeaseToken = string.Empty;
        _botHiderPresentationSignature = string.Empty;
        _activeBotHiderReplaySteamIds.Clear();
        _nextBotHiderLeaseHeartbeatAt = 0.0f;
        _nextBotHiderLeaseRetryAt = 0.0f;
        if (string.IsNullOrWhiteSpace(token))
            return;

        if (!_botHiderBridge.Release(token))
            _ = _botHiderBridge.ReleaseOwner(DemoTracerBotHiderContract.DemoTracerOwner);
        Server.PrintToConsole($"dtr: BotHider presentation lease released reason={reason}");
    }

    private bool HasActiveBotHiderReplayIdentity(int slot, ulong replaySteamId)
    {
        if (replaySteamId == 0 ||
            string.IsNullOrWhiteSpace(_botHiderPresentationLeaseToken) ||
            !_activeBotHiderReplaySteamIds.TryGetValue(slot, out var leasedSteamId) ||
            leasedSteamId != replaySteamId ||
            !_botHiderBridge.TryGetManagedSlot(slot, out _))
        {
            return false;
        }
        if (!_botHiderBridge.Heartbeat(_botHiderPresentationLeaseToken))
        {
            _botHiderPresentationLeaseToken = string.Empty;
            _botHiderPresentationSignature = string.Empty;
            _activeBotHiderReplaySteamIds.Clear();
            _nextBotHiderLeaseRetryAt = Server.CurrentTime;
            return false;
        }
        return true;
    }

    private int CountActiveBotHiderCrosshairOverrides()
    {
        var slots = new HashSet<int>(_companionCrosshairOverrides.Keys);
        if (_crosshairAlignEnabled)
        {
            foreach (var pair in _loadedReplays)
            {
                if (HasCrosshairEvidence(pair.Value.View))
                    slots.Add(pair.Key);
            }
            foreach (var evidence in _retainedBotHiderPresentation.Values)
            {
                if (HasCrosshairEvidence(evidence.View))
                    slots.Add(evidence.Slot);
            }
        }
        return slots.Count;
    }

    private readonly record struct BotHiderPresentationEvidence(
        int Slot,
        string PlayerName,
        ulong SteamId,
        int RetentionRank,
        ReplayScoreboardFlair? ScoreboardFlair,
        ReplayView View);
}
