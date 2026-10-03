using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;

using DtrControllerApi;

namespace DtrController;

public partial class BotControllerPlugin
{
    private const int ProjectileCandidateAttempts = 8;

    private sealed class PendingProjectileCandidate
    {
        public required uint EntityHandle { get; init; }
        public required nint EntityPtr { get; init; }
        public required ReplayProjectileKind Kind { get; init; }
        public required int WeaponDefIndex { get; init; }
        public uint RecordingTickIndex { get; set; } = uint.MaxValue;
        public int AttemptsRemaining { get; set; } = ProjectileCandidateAttempts;
    }

    private readonly Dictionary<int, List<ReplayProjectileEvent>> _recordedProjectiles = new();
    private readonly List<PendingProjectileCandidate> _pendingProjectileCandidates = new();

    // Starts a fresh projectile-event buffer for one recording slot
    private void BeginProjectileRecording(int slot)
    {
        _recordedProjectiles[slot] = new List<ReplayProjectileEvent>();
        DiscardProjectileCandidates(slot);
    }

    // Finishes pending captures and returns the slot's ordered projectile events
    private ReplayProjectileEvent[] FinishProjectileRecording(int slot)
    {
        ProcessPendingProjectileCandidates();
        DiscardProjectileCandidates(slot);
        if (!_recordedProjectiles.Remove(slot, out List<ReplayProjectileEvent>? events))
            return Array.Empty<ReplayProjectileEvent>();
        return events.OrderBy(projectile => projectile.TickIndex).ToArray();
    }

    private void DiscardProjectileCandidates(int slot)
    {
        _pendingProjectileCandidates.RemoveAll(candidate => TryCandidateSlot(candidate, out int owner) && owner == slot);
    }

    // Clears captured events and pending recording candidates.
    private void ClearAllProjectileState()
    {
        _recordedProjectiles.Clear();
        _pendingProjectileCandidates.Clear();
    }

    // Tracks grenade projectiles when the engine finishes spawning them
    private void OnProjectileEntitySpawned(CEntityInstance entity)
    {
        if (_recordedProjectiles.Count == 0)
            return;
        if (!ReplayProjectileMatcher.TryGetKind(entity.DesignerName, out ReplayProjectileKind kind, out int weaponDefIndex))
            return;

        var candidate = new PendingProjectileCandidate
        {
            EntityHandle = entity.EntityHandle.Raw,
            EntityPtr = entity.Handle,
            Kind = kind,
            WeaponDefIndex = weaponDefIndex,
        };
        if (!TryProcessProjectileCandidate(candidate)) _pendingProjectileCandidates.Add(candidate);
    }

    // Retries candidates whose thrower or birth vectors were not ready at spawn time
    private void ProcessPendingProjectileCandidates()
    {
        for (int i = _pendingProjectileCandidates.Count - 1; i >= 0; --i)
        {
            PendingProjectileCandidate candidate = _pendingProjectileCandidates[i];
            if (TryProcessProjectileCandidate(candidate))
            {
                _pendingProjectileCandidates.RemoveAt(i);
                continue;
            }

            --candidate.AttemptsRemaining;
            if (candidate.AttemptsRemaining <= 0) _pendingProjectileCandidates.RemoveAt(i);
        }
    }

    // Captures one projectile once its thrower and engine fields are available.
    private bool TryProcessProjectileCandidate(PendingProjectileCandidate candidate)
    {
        var projectile = ResolveCandidate(candidate);
        if (projectile == null) return true;
        if (!TryGetProjectileThrowerSlot(projectile, out int slot)) return false;

        if (_recordedProjectiles.ContainsKey(slot))
        {
            if (candidate.RecordingTickIndex == uint.MaxValue)
            {
                candidate.RecordingTickIndex = unchecked((uint)Math.Max(BotController.RecordedTickCount(slot), 0));
            }
            return TryCaptureProjectile(slot, projectile, candidate);
        }
        return true;
    }

    // Resolves a pending candidate's current thrower slot when available
    private static bool TryCandidateSlot(PendingProjectileCandidate candidate, out int slot)
    {
        slot = -1;
        var projectile = ResolveCandidate(candidate);
        return projectile != null && TryGetProjectileThrowerSlot(projectile, out slot);
    }

    private static CBaseCSGrenadeProjectile? ResolveCandidate(PendingProjectileCandidate candidate)
    {
        var projectile = new CHandle<CBaseCSGrenadeProjectile>(candidate.EntityHandle).Value;
        return projectile is { IsValid: true } && projectile.Handle == candidate.EntityPtr
            ? projectile : null;
    }

    // Captures the native projectile birth vectors for the active recording tick
    private bool TryCaptureProjectile(
        int slot,
        CBaseCSGrenadeProjectile projectile,
        PendingProjectileCandidate candidate)
    {
        ReplayVector3 initialPosition = ToReplayVector(projectile.InitialPosition);
        ReplayVector3 initialVelocity = ToReplayVector(projectile.InitialVelocity);
        if (!ReplayProjectileMatcher.IsFinite(initialPosition) ||
            !ReplayProjectileMatcher.IsMeaningful(initialVelocity))
        {
            return false;
        }

        _recordedProjectiles[slot].Add(new ReplayProjectileEvent
        {
            TickIndex = candidate.RecordingTickIndex,
            WeaponDefIndex = candidate.WeaponDefIndex,
            Kind = candidate.Kind,
            InitialPosition = initialPosition,
            InitialVelocity = initialVelocity,
        });
        return true;
    }

    // Resolves the player slot that owns a grenade projectile
    private static bool TryGetProjectileThrowerSlot(CBaseCSGrenadeProjectile projectile, out int slot)
    {
        slot = -1;
        CCSPlayerPawn? thrower = projectile.Thrower.Value;
        if (thrower is not { IsValid: true }) return false;

        foreach (CCSPlayerController player in Utilities.GetPlayers())
        {
            CCSPlayerPawn? pawn = player.PlayerPawn.Value;
            if (pawn is { IsValid: true } && pawn.Handle == thrower.Handle)
            {
                slot = player.Slot;
                return true;
            }
        }
        return false;
    }

    // Converts a CounterStrikeSharp vector into stable recording storage
    private static ReplayVector3 ToReplayVector(Vector vector)
        => new() { X = vector.X, Y = vector.Y, Z = vector.Z };

    // Stores a recorded projectile's final effect position when an event exposes it
    private void CaptureProjectileDetonation(
        CCSPlayerController? player,
        ReplayProjectileKind kind,
        float x,
        float y,
        float z)
    {
        if (player is not { IsValid: true } ||
            !_recordedProjectiles.TryGetValue(player.Slot, out List<ReplayProjectileEvent>? events))
        {
            return;
        }

        ReplayProjectileEvent? projectile = events.LastOrDefault(candidate => candidate.Kind == kind);
        if (projectile != null) projectile.DetonationPosition = new ReplayVector3 { X = x, Y = y, Z = z };
    }

    // Captures smoke detonation position for the latest recorded smoke
    [GameEventHandler]
    public HookResult OnSmokegrenadeDetonate(EventSmokegrenadeDetonate @event, GameEventInfo info)
    {
        CaptureProjectileDetonation(@event.Userid, ReplayProjectileKind.Smoke, @event.X, @event.Y, @event.Z);
        return HookResult.Continue;
    }

    // Captures flash detonation position for the latest recorded flash
    [GameEventHandler]
    public HookResult OnFlashbangDetonate(EventFlashbangDetonate @event, GameEventInfo info)
    {
        CaptureProjectileDetonation(@event.Userid, ReplayProjectileKind.Flash, @event.X, @event.Y, @event.Z);
        return HookResult.Continue;
    }

    // Captures HE detonation position for the latest recorded grenade
    [GameEventHandler]
    public HookResult OnHegrenadeDetonate(EventHegrenadeDetonate @event, GameEventInfo info)
    {
        CaptureProjectileDetonation(@event.Userid, ReplayProjectileKind.He, @event.X, @event.Y, @event.Z);
        return HookResult.Continue;
    }

    // Captures fire detonation position for the latest recorded fire grenade
    [GameEventHandler]
    public HookResult OnMolotovDetonate(EventMolotovDetonate @event, GameEventInfo info)
    {
        CaptureProjectileDetonation(@event.Userid, ReplayProjectileKind.Molotov, @event.X, @event.Y, @event.Z);
        return HookResult.Continue;
    }

    // Captures decoy detonation position for the latest recorded decoy
    [GameEventHandler]
    public HookResult OnDecoyDetonate(EventDecoyDetonate @event, GameEventInfo info)
    {
        CaptureProjectileDetonation(@event.Userid, ReplayProjectileKind.Decoy, @event.X, @event.Y, @event.Z);
        return HookResult.Continue;
    }
}
