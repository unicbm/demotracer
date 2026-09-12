// CounterStrikeSharp plugin: record a player's per-tick input and replay it on
// Chat commands:
//   !record [fileName] / !stoprecord  capture your own input, save to disk
//   !replay <botSlot> [fileName]      play a recording back on a bot
//   !stopreplay <botSlot>        stop a bot's replay

using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

using BotControllerApi;
using DemoTracerApi;

namespace BotControllerImpl;

public partial class BotControllerPlugin : BasePlugin
{
    public override string ModuleName => "BotControllerImpl";
    public override string ModuleVersion => "0.6.3-dtr.1";
    public override string ModuleAuthor => "XBribo & unicbm";
    public override string ModuleDescription =>
        "Record & Replay and Control CS2 bots.";

    // Record and replay must share a tickrate; adjust if your server differs.
    private const int Tickrate = 64;

    private readonly BotControllerApiImpl _api = new(IsDemoTracerBusy);
    private readonly Dictionary<int, string> _recordingFiles = new();
    private static readonly PluginCapability<IDemoTracerApi> DemoTracerCapability = new("demotracer:api");

    private static bool IsDemoTracerBusy(int slot)
    {
        try
        {
            var api = DemoTracerCapability.Get();
            return api == null || (api.IsDemoTracerBot(slot) && api.IsSlotBusy(slot));
        }
        catch (KeyNotFoundException) { return true; }
    }

    private static bool IsDemoTracerOwner(int slot)
    {
        try
        {
            var api = DemoTracerCapability.Get();
            return api != null && api.IsDemoTracerBot(slot) && api.IsSlotBusy(slot);
        }
        catch (KeyNotFoundException) { return false; }
    }

    // Loads the managed plugin and publishes its shared API
    public override void Load(bool hotReload)
    {
        if (!BotController.IsCompatible())
        {
            Server.PrintToConsole("[BotController] BotController ABI mismatch; disabled.");
            return;
        }

        // Publish the cross-plugin API
        // Consumers: BotControllerCapability.Cap.Get().
        Capabilities.RegisterPluginCapability(
            BotControllerCapability.Cap, () => _api);

        Directory.CreateDirectory(RecordingsDir);
        RegisterListener<Listeners.OnTick>(() => _api.ObserveDemoTracerOwnership(IsDemoTracerOwner, ClearProjectileReplay));
        RegisterListener<Listeners.OnTick>(ProcessPendingProjectileCandidates);
        RegisterListener<Listeners.OnEntitySpawned>(OnProjectileEntitySpawned);
        RegisterListener<Listeners.OnClientDisconnect>(ReleaseOwnedSlot);
        RegisterListener<Listeners.OnMapEnd>(ReleaseAllOwnedState);
        RegisterListener<Listeners.OnMapStart>(_ => ReleaseAllOwnedState());
    }

    // Clears projectile alignment state during managed plugin unload
    public override void Unload(bool hotReload)
    {
        ReleaseAllOwnedState();
    }

    private void ReleaseOwnedSlot(int slot)
    {
        try { _api.ReleaseOwnedSlot(slot, IsDemoTracerOwner(slot)); }
        catch (Exception ex) { Server.PrintToConsole($"[BotController] Slot {slot} cleanup failed: {ex.Message}"); }
        _recordingFiles.Remove(slot);
        _recordedProjectiles.Remove(slot);
        ClearProjectileReplay(slot);
    }

    private void ReleaseAllOwnedState()
    {
        try { _api.ReleaseAllOwnedSlots(IsDemoTracerOwner); }
        catch (Exception ex) { Server.PrintToConsole($"[BotController] Cleanup failed: {ex.Message}"); }
        _recordingFiles.Clear();
        ClearAllProjectileState();
    }

    private string RecordingsDir => Path.Combine(ModuleDirectory, "recordings");
    // Resolves an optional recording name to a safe plugin-local JSON path
    private bool TryGetRecordingFile(string? fileName, ulong steamId, out string file)
    {
        string name = string.IsNullOrWhiteSpace(fileName)
            ? steamId.ToString()
            : fileName;

        if (name != Path.GetFileName(name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            file = string.Empty;
            return false;
        }

        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];

        if (string.IsNullOrWhiteSpace(name))
        {
            file = string.Empty;
            return false;
        }

        file = Path.Combine(RecordingsDir, $"{name}.json");
        return true;
    }

    // Find a connected player/bot by its slot, or null.
    private static CCSPlayerController? ControllerForSlot(int slot)
    {
        foreach (var p in Utilities.GetPlayers())
            if (p.Slot == slot && p.IsValid) return p;
        return null;
    }

    // Registers the live bot pawn pointer required by the current native replay path.
    private bool RegisterReplayPawnForSlot(int slot)
    {
        var player = ControllerForSlot(slot);
        if (IsDemoTracerBusy(slot) || player is not { IsValid: true, IsBot: true, IsHLTV: false, ControllingBot: false } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;

        return _api.SetReplayPawn(slot, player.PlayerPawn.Value.Handle);
    }

    // Starts recording under the optional file name
    [ConsoleCommand("css_record", "Start recording: !record [fileName]")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnRecord(CCSPlayerController? player, CommandInfo cmd)
    {
        if (player == null || !player.IsValid) return;
        string? fileName = cmd.ArgCount >= 2 ? cmd.GetArg(1) : null;
        if (cmd.ArgCount > 2 ||
            !TryGetRecordingFile(fileName, player.SteamID, out string file))
        {
            cmd.ReplyToCommand("[BotController] Usage: !record [fileName]");
            return;
        }
        if (!_api.StartRecord(player.Slot))
        {
            cmd.ReplyToCommand("[BotController] Failed to start recording.");
            return;
        }
        BeginProjectileRecording(player.Slot);
        _recordingFiles[player.Slot] = file;
        cmd.ReplyToCommand("[BotController] Recording. Use !stoprecord to finish.");
    }

    // Stops recording and saves it under the selected file name
    [ConsoleCommand("css_stoprecord", "Stop recording and save to disk")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnStopRecord(CCSPlayerController? player, CommandInfo cmd)
    {
        if (player == null || !player.IsValid) return;
        ReplayProjectileEvent[] projectiles = FinishProjectileRecording(player.Slot);
        _api.StopRecord(player.Slot);

        if (!_recordingFiles.Remove(player.Slot, out string? file) &&
            !TryGetRecordingFile(null, player.SteamID, out file))
            return;

        int saved = MotionStore.SaveToFile(player.Slot, file, Tickrate, projectiles);
        cmd.ReplyToCommand(saved > 0
            ? $"[BotController] Saved {saved} ticks."
            : "[BotController] Nothing recorded.");
    }

    // Loads the optional recording file and replays it on a bot
    [ConsoleCommand("css_replay", "Replay a recording: !replay <botSlot> [fileName]")]
    [CommandHelper(minArgs: 1, usage: "<botSlot> [fileName]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnReplay(CCSPlayerController? player, CommandInfo cmd)
    {
        if (player == null || !player.IsValid) return;
        string? fileName = cmd.ArgCount >= 3 ? cmd.GetArg(2) : null;
        if (cmd.ArgCount > 3 ||
            !int.TryParse(cmd.GetArg(1), out int botSlot) ||
            !TryGetRecordingFile(fileName, player.SteamID, out string file))
        {
            cmd.ReplyToCommand("[BotController] Usage: !replay <botSlot> [fileName]");
            return;
        }
        if (!File.Exists(file))
        {
            cmd.ReplyToCommand("[BotController] No recording found. Use !record first.");
            return;
        }

        if (IsDemoTracerBusy(botSlot) || ControllerForSlot(botSlot) is not { IsBot: true, IsHLTV: false, ControllingBot: false })
        {
            cmd.ReplyToCommand("[BotController] Target must be a free bot outside DemoTracer playback.");
            return;
        }

        MotionRecording rec;
        try
        {
            rec = MotionStore.LoadFromFile(file);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            System.Text.Json.JsonException or NotSupportedException)
        {
            cmd.ReplyToCommand($"[BotController] Cannot load recording: {ex.Message}");
            return;
        }
        if (rec.Ticks.Length == 0)
        {
            cmd.ReplyToCommand("[BotController] Recording is empty.");
            return;
        }
        if (rec.Tickrate != Tickrate)
            cmd.ReplyToCommand($"[BotController] WARN tickrate mismatch: recorded {rec.Tickrate}, server {Tickrate}.");

        var replayLoaded = _api.LoadReplayExtended(
                botSlot,
                rec.Ticks,
                rec.Subticks,
                rec.Commands ?? Array.Empty<ReplayCommandFrame>());
        if (replayLoaded) PrepareProjectileReplay(botSlot, rec);
        if (replayLoaded &&
            RegisterReplayPawnForSlot(botSlot) &&
            _api.StartReplay(botSlot))
        {
            cmd.ReplyToCommand($"[BotController] Replaying on bot slot {botSlot}.");
        }
        else
        {
            if (replayLoaded)
            {
                _api.ReleaseOwnedReplay(botSlot, IsDemoTracerOwner(botSlot));
                ClearProjectileReplay(botSlot);
            }
            cmd.ReplyToCommand("[BotController] Failed to start replay.");
        }
    }

    // Stops replay on the selected bot slot
    [ConsoleCommand("css_stopreplay", "Stop a bot's replay: !stopreplay <botSlot>")]
    [CommandHelper(minArgs: 1, usage: "<botSlot>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnStopReplay(CCSPlayerController? player, CommandInfo cmd)
    {
        if (player == null || !player.IsValid) return;
        if (!int.TryParse(cmd.GetArg(1), out int botSlot)) return;
        if (IsDemoTracerBusy(botSlot) || ControllerForSlot(botSlot) is not { IsBot: true, ControllingBot: false }) return;
        _api.StopReplay(botSlot);
        ClearProjectileReplay(botSlot);
        cmd.ReplyToCommand($"[BotController] Stopped replay on bot slot {botSlot}.");
    }
}
