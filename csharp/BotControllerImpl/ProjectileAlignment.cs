using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;

using BotControllerApi;

namespace BotControllerImpl;

public partial class BotControllerPlugin
{
    private const int ProjectileCandidateAttempts = 8;

    private sealed class PendingProjectileCandidate
    {
        public required int EntityIndex { get; init; }
        public required nint EntityPtr { get; init; }
        public required ReplayProjectileKind Kind { get; init; }
        public required int WeaponDefIndex { get; init; }
        public uint RecordingTickIndex { get; set; } = uint.MaxValue;
        public int AttemptsRemaining { get; set; } = ProjectileCandidateAttempts;
    }

    private readonly Dictionary<int, List<ReplayProjectileEvent>> _recordedProjectiles = new();
    private readonly Dictionary<int, ReplayProjectileEvent[]> _replayProjectiles = new();
    private readonly Dictionary<int, int> _nextReplayProjectile = new();
    private readonly List<PendingProjectileCandidate> _pendingProjectileCandidates = new();

    // Starts a fresh projectile-event buffer for one recording slot
    private void BeginProjectileRecording(int slot)
    {
        _recordedProjectiles[slot] = new List<ReplayProjectileEvent>();
        _pendingProjectileCandidates.RemoveAll(candidate => TryCandidateSlot(candidate, out int owner) && owner == slot);
    }

    // Finishes pending captures and returns the slot's ordered projectile events
    private ReplayProjectileEvent[] FinishProjectileRecording(int slot)
    {
        ProcessPendingProjectileCandidates();
        _pendingProjectileCandidates.RemoveAll(candidate => TryCandidateSlot(candidate, out int owner) && owner == slot);
        if (!_recordedProjectiles.Remove(slot, out List<ReplayProjectileEvent>? events))
            return Array.Empty<ReplayProjectileEvent>();
        return events.OrderBy(projectile => projectile.TickIndex).ToArray();
    }

    // Installs one loaded recording's projectile sequence for a replay slot
    private void PrepareProjectileReplay(int slot, MotionRecording recording)
    {
        _replayProjectiles[slot] = recording.Projectiles ?? Array.Empty<ReplayProjectileEvent>();
        _nextReplayProjectile[slot] = 0;
    }

    // Releases one replay slot's projectile matching state
    private void ClearProjectileReplay(int slot)
    {
        _replayProjectiles.Remove(slot);
        _nextReplayProjectile.Remove(slot);
        _pendingProjectileCandidates.RemoveAll(candidate => TryCandidateSlot(candidate, out int owner) && owner == slot);
    }

    // Clears every managed and native projectile alignment buffer
    private void ClearAllProjectileState()
    {
        _recordedProjectiles.Clear();
        _replayProjectiles.Clear();
        _nextReplayProjectile.Clear();
        _pendingProjectileCandidates.Clear();
        // The native queue is shared with DemoTracer. Its owner clears it on unload/map end.
    }

    // Tracks grenade projectiles when the engine finishes spawning them
    private void OnProjectileEntitySpawned(CEntityInstance entity)
    {
        if (!ReplayProjectileMatcher.TryGetKind(entity.DesignerName, out ReplayProjectileKind kind, out int weaponDefIndex))
            return;

        var candidate = new PendingProjectileCandidate
        {
            EntityIndex = unchecked((int)entity.Index),
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

        foreach (int slot in _replayProjectiles.Keys.Where(slot => !BotController.IsReplaying(slot)).ToArray())
            ClearProjectileReplay(slot);
    }

    // Captures or aligns one projectile once its thrower and engine fields are available
    private bool TryProcessProjectileCandidate(PendingProjectileCandidate candidate)
    {
        var projectile = new CBaseCSGrenadeProjectile(candidate.EntityPtr);
        if (!projectile.IsValid) return true;
        if (!TryGetProjectileThrowerSlot(projectile, out int slot)) return false;

        if (_recordedProjectiles.ContainsKey(slot))
        {
            if (candidate.RecordingTickIndex == uint.MaxValue)
            {
                candidate.RecordingTickIndex = unchecked((uint)Math.Max(BotController.RecordedTickCount(slot), 0));
            }
            return TryCaptureProjectile(slot, projectile, candidate);
        }
        if (IsDemoTracerOwner(slot))
        {
            ClearProjectileReplay(slot);
            return true;
        }
        if (BotController.IsReplaying(slot)) return TryAlignProjectile(slot, projectile, candidate);
        return true;
    }

    // Resolves a pending candidate's current thrower slot when available
    private static bool TryCandidateSlot(PendingProjectileCandidate candidate, out int slot)
    {
        slot = -1;
        var projectile = new CBaseCSGrenadeProjectile(candidate.EntityPtr);
        return projectile.IsValid && TryGetProjectileThrowerSlot(projectile, out slot);
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

        if (!_recordedProjectiles.TryGetValue(slot, out List<ReplayProjectileEvent>? events))
        {
            events = new List<ReplayProjectileEvent>();
            _recordedProjectiles[slot] = events;
        }
        events.Add(new ReplayProjectileEvent
        {
            TickIndex = candidate.RecordingTickIndex,
            WeaponDefIndex = candidate.WeaponDefIndex,
            Kind = candidate.Kind,
            InitialPosition = initialPosition,
            InitialVelocity = initialVelocity,
        });
        return true;
    }

    // Matches a spawned replay projectile and queues its native birth correction
    private bool TryAlignProjectile(
        int slot,
        CBaseCSGrenadeProjectile projectile,
        PendingProjectileCandidate candidate)
    {
        if (!_replayProjectiles.TryGetValue(slot, out ReplayProjectileEvent[]? events) || events.Length == 0)
            return true;

        int start = _nextReplayProjectile.TryGetValue(slot, out int next) ? next : 0;
        int eventIndex = ReplayProjectileMatcher.FindNext(
            events,
            start,
            BotController.ReplayCursor(slot),
            candidate.Kind,
            candidate.WeaponDefIndex);
        if (eventIndex < 0) return false;

        ReplayProjectileEvent expected = events[eventIndex];
        if (!ReplayProjectileMatcher.IsFinite(expected.InitialPosition) ||
            !ReplayProjectileMatcher.IsMeaningful(expected.InitialVelocity))
        {
            _nextReplayProjectile[slot] = eventIndex + 1;
            return true;
        }

        if (!BotController.QueueProjectileBirthAlign(
                projectile.Handle,
                expected.InitialPosition,
                expected.InitialVelocity))
            return false;

        _nextReplayProjectile[slot] = eventIndex + 1;
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
