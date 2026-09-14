// Recording model and JSON load/save

using System.Text.Json;

using BotControllerApi;

namespace BotControllerImpl;

// Recorded motion plus the tickrate it was captured at
public sealed class MotionRecording
{
    public int Tickrate { get; set; } = 64;
    public ReplayTick[] Ticks { get; set; } = Array.Empty<ReplayTick>();
    public SubtickMove[] Subticks { get; set; } = Array.Empty<SubtickMove>();
    public ReplayCommandFrame[] Commands { get; set; } = Array.Empty<ReplayCommandFrame>();
    public ReplayProjectileEvent[] Projectiles { get; set; } = Array.Empty<ReplayProjectileEvent>();
}

// File + capture-buffer on top of the native calls
public static class MotionStore
{
    // IncludeFields
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        IncludeFields = true,
    };

    // Save a slot's recorded motion to a JSON file. Returns tick count, or -1
    // if nothing was recorded
    public static int SaveToFile(
        int slot,
        string path,
        int tickrate = 64,
        ReplayProjectileEvent[]? projectiles = null)
    {
        var (ticks, subs, commands) = BotController.GetRecordedMotionExtended(slot);
        if (ticks.Length == 0) return -1;
        var rec = new MotionRecording
        {
            Tickrate = tickrate,
            Ticks = ticks,
            Subticks = subs,
            Commands = commands,
            Projectiles = projectiles ?? Array.Empty<ReplayProjectileEvent>(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(rec, JsonOpts));
        return ticks.Length;
    }

    // Load a JSON recording from disk
    public static MotionRecording LoadFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        var recording = JsonSerializer.Deserialize<MotionRecording>(stream, JsonOpts)
            ?? throw new InvalidDataException("Recording is empty.");
        if (recording.Ticks is null || recording.Subticks is null)
            throw new InvalidDataException("Recording must contain tick and subtick arrays.");
        for (int i = 0; i < recording.Ticks.Length; i++)
        {
            var tick = recording.Ticks[i];
            if (tick.EventFlags != 0 || tick.EventWeaponDefIndex != 0 ||
                tick.EventDropVectorFlags != 0 ||
                tick.EventDropTargetX != 0 || tick.EventDropTargetY != 0 || tick.EventDropTargetZ != 0 ||
                tick.EventDropVelocityX != 0 || tick.EventDropVelocityY != 0 || tick.EventDropVelocityZ != 0)
            {
                throw new NotSupportedException(
                    $"Native weapon-drop events are unsupported (tick {i}); the reserved event tail must be zero.");
            }
        }
        return recording;
    }
}
