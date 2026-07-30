using System.Security.Cryptography;
using System.Text;
using BotRandomizerApi;
using CounterStrikeSharp.API;

namespace DemoTracer;

internal enum DemoTracerCosmeticWriteField
{
    Agent,
    Knife,
    Gloves,
    MusicKit,
    WeaponPaint,
    WeaponStickers,
    WeaponKeychain
}

internal sealed record DemoTracerBotRandomizerWeaponEvidence(
    int WeaponDefinitionIndex,
    bool Paint,
    bool Stickers,
    bool Keychain,
    bool? PaintUsesLegacyModel);

internal sealed record DemoTracerBotRandomizerPositiveEvidence(
    bool Agent,
    bool Knife,
    bool Gloves,
    bool MusicKit,
    IReadOnlyList<DemoTracerBotRandomizerWeaponEvidence> Weapons);

internal sealed record DemoTracerActiveWeaponWriteClaim(
    bool Paint,
    bool Stickers,
    bool Keychain,
    bool? PaintUsesLegacyModel)
{
    internal bool Allows(DemoTracerCosmeticWriteField field)
        => field switch
        {
            DemoTracerCosmeticWriteField.WeaponPaint => Paint,
            DemoTracerCosmeticWriteField.WeaponStickers => Stickers,
            DemoTracerCosmeticWriteField.WeaponKeychain => Keychain,
            _ => false
        };
}

internal sealed record DemoTracerActiveCosmeticWriteClaim(
    int Slot,
    ulong Incarnation,
    ulong SubjectSteamId,
    bool Agent,
    bool Knife,
    bool Gloves,
    bool MusicKit,
    IReadOnlyDictionary<int, DemoTracerActiveWeaponWriteClaim> Weapons)
{
    internal bool MatchesIdentity(ulong incarnation, ulong subjectSteamId)
        => Incarnation == incarnation &&
           SubjectSteamId != 0 &&
           SubjectSteamId == subjectSteamId;

    internal bool Allows(DemoTracerCosmeticWriteField field, int weaponDefinitionIndex = 0)
        => field switch
        {
            DemoTracerCosmeticWriteField.Agent => Agent,
            DemoTracerCosmeticWriteField.Knife => Knife,
            DemoTracerCosmeticWriteField.Gloves => Gloves,
            DemoTracerCosmeticWriteField.MusicKit => MusicKit,
            DemoTracerCosmeticWriteField.WeaponPaint or
            DemoTracerCosmeticWriteField.WeaponStickers or
            DemoTracerCosmeticWriteField.WeaponKeychain =>
                Weapons.TryGetValue(weaponDefinitionIndex, out var weapon) && weapon.Allows(field),
            _ => false
        };
}

internal sealed class DemoTracerBotRandomizerLeaseSnapshot
{
    private readonly Dictionary<int, DemoTracerActiveCosmeticWriteClaim> _claims = new();

    internal string Token { get; private set; } = string.Empty;
    internal string ProviderEpoch { get; private set; } = string.Empty;
    internal IReadOnlyDictionary<int, DemoTracerActiveCosmeticWriteClaim> Claims => _claims;

    internal void Activate(
        string token,
        string providerEpoch,
        IEnumerable<BotRandomizerCosmeticWriteClaim> claims)
    {
        Token = token;
        ProviderEpoch = providerEpoch;
        _claims.Clear();
        foreach (var claim in claims)
        {
            if (claim.SubjectSteamId is not { } subjectSteamId || subjectSteamId == 0)
                continue;

            var weapons = (claim.Weapons ?? [])
                .Where(weapon => weapon.Paint || weapon.Stickers || weapon.Keychain)
                .ToDictionary(
                    weapon => weapon.WeaponDefinitionIndex,
                    weapon => new DemoTracerActiveWeaponWriteClaim(
                        weapon.Paint,
                        weapon.Stickers,
                        weapon.Keychain,
                        weapon.PaintUsesLegacyModel));
            _claims[claim.Slot] = new DemoTracerActiveCosmeticWriteClaim(
                claim.Slot,
                claim.Incarnation,
                subjectSteamId,
                claim.Agent,
                claim.Knife,
                claim.Gloves,
                claim.MusicKit,
                weapons);
        }
    }

    internal void Invalidate()
    {
        Token = string.Empty;
        ProviderEpoch = string.Empty;
        _claims.Clear();
    }

    internal bool TryGet(
        int slot,
        ulong subjectSteamId,
        out DemoTracerActiveCosmeticWriteClaim claim)
        => _claims.TryGetValue(slot, out claim!) &&
           subjectSteamId != 0 &&
           claim.SubjectSteamId == subjectSteamId;
}

public sealed partial class DemoTracerPlugin
{
    private const float BotRandomizerLeaseHeartbeatSeconds = 1.0f;
    private const float BotRandomizerLeaseRetrySeconds = 1.0f;
    private readonly DemoTracerBotRandomizerBridge _botRandomizerBridge = new();
    private readonly DemoTracerBotRandomizerLeaseSnapshot _botRandomizerLease = new();
    private string _botRandomizerLeaseSignature = string.Empty;
    private string _lastBotRandomizerLeaseError = string.Empty;
    private float _nextBotRandomizerLeaseHeartbeatAt;
    private float _nextBotRandomizerLeaseRetryAt;

    private void EnsureBotRandomizerCosmeticLease()
    {
        if (!string.IsNullOrWhiteSpace(_botRandomizerLease.Token) &&
            Server.CurrentTime >= _nextBotRandomizerLeaseHeartbeatAt)
        {
            _nextBotRandomizerLeaseHeartbeatAt = Server.CurrentTime + BotRandomizerLeaseHeartbeatSeconds;
            if (!ProviderEpochMatchesActiveBotRandomizerLease() ||
                !_botRandomizerBridge.Heartbeat(_botRandomizerLease.Token))
            {
                InvalidateBotRandomizerCosmeticLease("heartbeat_failed");
            }
        }

        if (Server.CurrentTime >= _nextBotRandomizerLeaseRetryAt &&
            (string.IsNullOrWhiteSpace(_botRandomizerLease.Token) ||
             !string.IsNullOrWhiteSpace(_lastBotRandomizerLeaseError)))
        {
            _ = SyncBotRandomizerCosmeticLease(announce: false);
        }
    }

    private bool SyncBotRandomizerCosmeticLease(bool announce)
    {
        if (_loadedReplays.Count == 0 || !AnyCosmeticFeatureEnabled())
        {
            ReleaseBotRandomizerCosmeticLease("no_positive_claims");
            return true;
        }

        var provider = _botRandomizerBridge.GetProviderInfo();
        if (provider == null ||
            provider.ApiVersion != BotRandomizerContract.ApiVersion ||
            !provider.Ready ||
            provider.Draining)
        {
            InvalidateBotRandomizerCosmeticLease("provider_unavailable");
            _nextBotRandomizerLeaseRetryAt = Server.CurrentTime + BotRandomizerLeaseRetrySeconds;
            ReportBotRandomizerLeaseError("provider_unavailable", announce);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_botRandomizerLease.ProviderEpoch) &&
            !_botRandomizerLease.ProviderEpoch.Equals(provider.ProviderEpoch, StringComparison.Ordinal))
        {
            InvalidateBotRandomizerCosmeticLease("provider_epoch_changed");
        }

        var requests = BuildBotRandomizerCosmeticWriteClaims();
        if (requests.Length == 0)
        {
            ReleaseBotRandomizerCosmeticLease("no_positive_claims");
            return true;
        }

        var signature = BuildBotRandomizerClaimSignature(provider.ProviderEpoch, requests);
        if (!string.IsNullOrWhiteSpace(_botRandomizerLease.Token) &&
            signature.Equals(_botRandomizerLeaseSignature, StringComparison.Ordinal))
        {
            _lastBotRandomizerLeaseError = string.Empty;
            return true;
        }

        BotRandomizerWriteLeaseResult result;
        if (string.IsNullOrWhiteSpace(_botRandomizerLease.Token))
        {
            result = AcquireBotRandomizerCosmeticLease(requests);
        }
        else
        {
            var token = _botRandomizerLease.Token;
            result = _botRandomizerBridge.Replace(token, requests);
            if (!result.Ok && result.Reason.Equals("lease_not_found", StringComparison.Ordinal))
            {
                InvalidateBotRandomizerCosmeticLease("lease_not_found");
                result = AcquireBotRandomizerCosmeticLease(requests);
            }
        }

        if (!result.Ok || string.IsNullOrWhiteSpace(result.LeaseToken))
        {
            // A failed replacement leaves the provider's old lease intact, but
            // its fields no longer match current evidence. Keep DemoTracer fail
            // closed until an atomic replacement succeeds.
            _botRandomizerLease.Invalidate();
            _nextBotRandomizerLeaseRetryAt = Server.CurrentTime + BotRandomizerLeaseRetrySeconds;
            ReportBotRandomizerLeaseError(result.Reason, announce);
            return false;
        }

        _botRandomizerLease.Activate(result.LeaseToken, result.ProviderEpoch, requests);
        _botRandomizerLeaseSignature = signature;
        _lastBotRandomizerLeaseError = string.Empty;
        _nextBotRandomizerLeaseHeartbeatAt = Server.CurrentTime + BotRandomizerLeaseHeartbeatSeconds;
        _nextBotRandomizerLeaseRetryAt = 0.0f;
        ScheduleBotRandomizerLeaseInventoryRebuild(result.Slots);
        if (announce)
        {
            Server.PrintToConsole(
                $"dtr: BotRandomizer cosmetic lease active slots={string.Join(',', result.Slots)} " +
                $"provider_epoch={result.ProviderEpoch}");
        }
        return true;
    }

    private BotRandomizerWriteLeaseResult AcquireBotRandomizerCosmeticLease(
        BotRandomizerCosmeticWriteClaim[] requests)
    {
        var result = _botRandomizerBridge.Acquire(BotRandomizerContract.DemoTracerOwner, requests);
        if (!result.Ok && result.Reason.StartsWith("slot_leased:", StringComparison.Ordinal))
        {
            _ = _botRandomizerBridge.ReleaseOwner(BotRandomizerContract.DemoTracerOwner);
            result = _botRandomizerBridge.Acquire(BotRandomizerContract.DemoTracerOwner, requests);
        }
        return result;
    }

    private BotRandomizerCosmeticWriteClaim[] BuildBotRandomizerCosmeticWriteClaims()
    {
        var claims = new List<BotRandomizerCosmeticWriteClaim>();
        foreach (var pair in _loadedReplays.OrderBy(pair => pair.Key))
        {
            var slot = pair.Key;
            var replay = pair.Value;
            if (!IsReplaySlotStillSafe(slot) ||
                !HasActiveBotHiderReplayIdentity(slot, replay.SteamId) ||
                !_botRandomizerBridge.TryGetManagedBot(slot, out var managed))
            {
                continue;
            }

            var evidence = BuildBotRandomizerPositiveEvidence(replay);
            var claim = BuildBotRandomizerWriteClaim(
                managed.Slot,
                managed.Incarnation,
                replay.SteamId,
                evidence);
            if (claim != null)
                claims.Add(claim);
        }
        return claims.ToArray();
    }

    private DemoTracerBotRandomizerPositiveEvidence BuildBotRandomizerPositiveEvidence(LoadedReplay replay)
    {
        var weapons = new List<DemoTracerBotRandomizerWeaponEvidence>();
        if (_cosmeticAlignEnabled && _weaponAlignEnabled)
        {
            foreach (var weapon in replay.Cosmetics.Weapons)
            {
                var paint = _cosmeticWeaponsEnabled;
                var stickers = _stickerAlignEnabled && weapon.Stickers.Count > 0;
                var keychain = _charmAlignEnabled && weapon.Charms.Count > 0;
                if (!paint && !stickers && !keychain)
                    continue;
                weapons.Add(new DemoTracerBotRandomizerWeaponEvidence(
                    weapon.WeaponDefIndex,
                    paint,
                    stickers,
                    keychain,
                    paint
                        ? IsLegacyCosmeticPaint(weapon.WeaponDefIndex, (int)weapon.PaintKit)
                        : null));
            }
        }

        return new DemoTracerBotRandomizerPositiveEvidence(
            Agent: _cosmeticAlignEnabled && _cosmeticAgentsEnabled && replay.Cosmetics.Agent != null,
            Knife: _cosmeticAlignEnabled && _weaponAlignEnabled && _cosmeticKnivesEnabled && replay.Cosmetics.Knife != null,
            Gloves: _cosmeticAlignEnabled && _weaponAlignEnabled && _cosmeticGlovesEnabled && replay.Cosmetics.Glove != null,
            MusicKit: ReplayMusicKitAlignmentAllowed(replay.MusicKitId),
            Weapons: weapons);
    }

    internal static BotRandomizerCosmeticWriteClaim? BuildBotRandomizerWriteClaim(
        int slot,
        ulong incarnation,
        ulong subjectSteamId,
        DemoTracerBotRandomizerPositiveEvidence evidence)
    {
        if (slot < 0 || incarnation == 0 || subjectSteamId == 0)
            return null;

        var weapons = evidence.Weapons
            .Where(weapon =>
                weapon.WeaponDefinitionIndex > 0 &&
                (weapon.Paint || weapon.Stickers || weapon.Keychain))
            .OrderBy(weapon => weapon.WeaponDefinitionIndex)
            .GroupBy(weapon => weapon.WeaponDefinitionIndex)
            .Where(group => group.Count() == 1)
            .Select(group => group.First())
            .Select(weapon => new BotRandomizerWeaponWriteClaim
            {
                WeaponDefinitionIndex = weapon.WeaponDefinitionIndex,
                Paint = weapon.Paint,
                Stickers = weapon.Stickers,
                Keychain = weapon.Keychain,
                PaintUsesLegacyModel = weapon.Paint ? weapon.PaintUsesLegacyModel : null
            })
            .ToArray();
        if (!evidence.Agent && !evidence.Knife && !evidence.Gloves && !evidence.MusicKit && weapons.Length == 0)
            return null;

        return new BotRandomizerCosmeticWriteClaim
        {
            Slot = slot,
            Incarnation = incarnation,
            SubjectSteamId = subjectSteamId,
            Agent = evidence.Agent,
            Knife = evidence.Knife,
            Gloves = evidence.Gloves,
            MusicKit = evidence.MusicKit,
            Weapons = weapons
        };
    }

    private static string BuildBotRandomizerClaimSignature(
        string providerEpoch,
        IEnumerable<BotRandomizerCosmeticWriteClaim> claims)
    {
        var builder = new StringBuilder(providerEpoch).Append('|');
        foreach (var claim in claims.OrderBy(claim => claim.Slot))
        {
            builder
                .Append(claim.Slot).Append(':')
                .Append(claim.Incarnation).Append(':')
                .Append(claim.SubjectSteamId).Append(':')
                .Append(claim.Agent ? '1' : '0')
                .Append(claim.Knife ? '1' : '0')
                .Append(claim.Gloves ? '1' : '0')
                .Append(claim.MusicKit ? '1' : '0').Append('|');
            foreach (var weapon in claim.Weapons.OrderBy(weapon => weapon.WeaponDefinitionIndex))
            {
                builder
                    .Append(weapon.WeaponDefinitionIndex).Append(':')
                    .Append(weapon.Paint ? '1' : '0')
                    .Append(weapon.Stickers ? '1' : '0')
                    .Append(weapon.Keychain ? '1' : '0').Append(':')
                    .Append(weapon.PaintUsesLegacyModel?.ToString() ?? "null").Append('|');
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private bool TryValidateBotRandomizerClaim(
        int slot,
        ulong subjectSteamId,
        DemoTracerCosmeticWriteField field,
        int weaponDefinitionIndex = 0)
    {
        if (!_botRandomizerLease.TryGet(slot, subjectSteamId, out var claim) ||
            !claim.Allows(field, weaponDefinitionIndex))
        {
            return false;
        }

        var provider = _botRandomizerBridge.GetProviderInfo();
        if (provider == null ||
            !provider.Ready ||
            provider.Draining ||
            !provider.ProviderEpoch.Equals(_botRandomizerLease.ProviderEpoch, StringComparison.Ordinal) ||
            !_botRandomizerBridge.Heartbeat(_botRandomizerLease.Token) ||
            !_botRandomizerBridge.TryGetManagedBot(slot, out var managed) ||
            !claim.MatchesIdentity(managed.Incarnation, subjectSteamId) ||
            !HasActiveBotHiderReplayIdentity(slot, subjectSteamId))
        {
            InvalidateBotRandomizerCosmeticLease("active_claim_validation_failed");
            return false;
        }

        _nextBotRandomizerLeaseHeartbeatAt = Server.CurrentTime + BotRandomizerLeaseHeartbeatSeconds;
        return true;
    }

    private bool HasActiveBotRandomizerClaim(
        int slot,
        ulong subjectSteamId,
        DemoTracerCosmeticWriteField field,
        int weaponDefinitionIndex = 0)
        => _botRandomizerLease.TryGet(slot, subjectSteamId, out var claim) &&
           claim.Allows(field, weaponDefinitionIndex);

    private bool ProviderEpochMatchesActiveBotRandomizerLease()
    {
        var provider = _botRandomizerBridge.GetProviderInfo();
        return provider != null &&
               provider.Ready &&
               !provider.Draining &&
               provider.ProviderEpoch.Equals(_botRandomizerLease.ProviderEpoch, StringComparison.Ordinal);
    }

    private void InvalidateBotRandomizerCosmeticLease(string reason)
    {
        var hadActiveLease = !string.IsNullOrWhiteSpace(_botRandomizerLease.Token);
        _botRandomizerLease.Invalidate();
        _botRandomizerLeaseSignature = string.Empty;
        _nextBotRandomizerLeaseHeartbeatAt = 0.0f;
        _nextBotRandomizerLeaseRetryAt = Server.CurrentTime;
        if (hadActiveLease)
            Server.PrintToConsole($"dtr: BotRandomizer cosmetic lease invalidated reason={reason}");
    }

    private void ReleaseBotRandomizerCosmeticLease(string reason)
    {
        var token = _botRandomizerLease.Token;
        _botRandomizerLease.Invalidate();
        _botRandomizerLeaseSignature = string.Empty;
        _lastBotRandomizerLeaseError = string.Empty;
        _nextBotRandomizerLeaseHeartbeatAt = 0.0f;
        _nextBotRandomizerLeaseRetryAt = 0.0f;
        if (string.IsNullOrWhiteSpace(token))
            return;

        if (!_botRandomizerBridge.Release(token))
            _ = _botRandomizerBridge.ReleaseOwner(BotRandomizerContract.DemoTracerOwner);
        Server.PrintToConsole($"dtr: BotRandomizer cosmetic lease released reason={reason}");
    }

    private void ReportBotRandomizerLeaseError(string reason, bool announce)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
        if (announce || !_lastBotRandomizerLeaseError.Equals(reason, StringComparison.Ordinal))
            Server.PrintToConsole($"dtr: BotRandomizer cosmetic lease unavailable: {reason}");
        _lastBotRandomizerLeaseError = reason;
    }

    private void ScheduleBotRandomizerLeaseInventoryRebuild(IEnumerable<int> slots)
    {
        foreach (var slot in slots.Distinct())
        {
            _rebuiltInventorySlots.Remove(slot);
            _loadoutSyncedSlots.Remove(slot);
            _cosmeticSyncedSlots.Remove(slot);
            ScheduleCosmeticNextFrame(() =>
            {
                if (!_loadedReplays.TryGetValue(slot, out var replay) ||
                    !_botRandomizerLease.TryGet(slot, replay.SteamId, out _))
                {
                    return;
                }

                ApplyReplayLoadoutForSlot(slot, replay);
                PreloadReplayWeaponsForSlot(slot, replay);
                if (ReplayMusicKitAlignmentAllowed(replay.MusicKitId))
                    _ = ApplyReplayMusicKitForSlot(slot, replay.MusicKitId);
                if (_cosmeticAlignEnabled && (_weaponAlignEnabled || _cosmeticAgentsEnabled))
                    ApplyLoadedReplayCosmeticsForSlot(slot, replay);
            });
        }
    }

    internal static bool ShouldClearCompleteAttributeLists(DemoTracerCosmeticWriteField field)
        => field is DemoTracerCosmeticWriteField.Knife or DemoTracerCosmeticWriteField.Gloves;

    private string FormatBotRandomizerLeaseStatus()
    {
        var diagnostics = _botRandomizerBridge.GetDiagnostics();
        return
            $"randomizer_lease={(!string.IsNullOrWhiteSpace(_botRandomizerLease.Token) ? "active" : "inactive")}" +
            $" randomizer_claim_slots={_botRandomizerLease.Claims.Count}" +
            $" randomizer_provider_ready={(diagnostics?.Ready == true ? "on" : "off")}";
    }
}
