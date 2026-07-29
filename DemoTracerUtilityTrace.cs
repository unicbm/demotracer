using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using System.Globalization;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private static readonly string[] UtilityTraceColumns =
    [
        "kind",
        "time",
        "slot",
        "player",
        "steam_id",
        "replay_cursor",
        "replay_total",
        "weapon_def",
        "live_weapon",
        "live_x",
        "live_y",
        "live_z",
        "live_vx",
        "live_vy",
        "live_vz",
        "live_pitch",
        "live_yaw",
        "replay_pre_x",
        "replay_pre_y",
        "replay_pre_z",
        "replay_pre_vx",
        "replay_pre_vy",
        "replay_pre_vz",
        "replay_pre_pitch",
        "replay_pre_yaw",
        "replay_buttons",
        "replay_buttons1",
        "replay_buttons2",
        "replay_post_x",
        "replay_post_y",
        "replay_post_z",
        "replay_post_vx",
        "replay_post_vy",
        "replay_post_vz",
        "stash_set",
        "stash_time",
        "stash_x",
        "stash_y",
        "stash_z",
        "stash_vx",
        "stash_vy",
        "stash_vz",
        "stash_pitch",
        "stash_yaw",
        "projectile_index",
        "projectile_name",
        "projectile_x",
        "projectile_y",
        "projectile_z",
        "projectile_abs_vx",
        "projectile_abs_vy",
        "projectile_abs_vz",
        "projectile_est_vx",
        "projectile_est_vy",
        "projectile_est_vz",
        "projectile_init_x",
        "projectile_init_y",
        "projectile_init_z",
        "projectile_init_vx",
        "projectile_init_vy",
        "projectile_init_vz",
        "projectile_smoke_det_x",
        "projectile_smoke_det_y",
        "projectile_smoke_det_z",
        "projectile_bounces",
        "projectile_is_live",
        "event_entity",
        "event_x",
        "event_y",
        "event_z",
        "message"
    ];

    [ConsoleCommand("dtr_util_trace", "dtr_util_trace <0|1> [file.csv]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void UtilityTraceCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand(
                _utilityTraceEnabled
                    ? $"dtr: utility trace on path=\"{_utilityTracePath}\""
                    : "usage: dtr_util_trace <0|1> [file.csv]");
            return;
        }

        if (command.GetArg(1) == "0")
        {
            var path = _utilityTracePath;
            StopUtilityTrace();
            command.ReplyToCommand(string.IsNullOrEmpty(path)
                ? "dtr: utility trace off"
                : $"dtr: utility trace off path=\"{path}\"");
            return;
        }

        var requestedPath = command.ArgCount >= 3 ? command.GetArg(2) : string.Empty;
        if (!StartUtilityTrace(requestedPath, out var message))
        {
            command.ReplyToCommand($"dtr: utility trace failed: {message}");
            return;
        }

        command.ReplyToCommand($"dtr: utility trace on path=\"{message}\"");
    }

    private bool StartUtilityTrace(string requestedPath, out string message)
    {
        StopUtilityTrace();
        _utilityTraceProjectiles.Clear();

        try
        {
            var path = ResolveUtilityTracePath(requestedPath);
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            try
            {
                _utilityTraceWriter = new StreamWriter(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
            _utilityTraceWriter.WriteLine(Row(UtilityTraceColumns));
            _utilityTraceWriter.Flush();
            _utilityTracePath = path;
            _utilityTraceEnabled = true;
            message = path;
            return true;
        }
        catch (Exception ex)
        {
            StopUtilityTrace();
            message = ex.Message;
            return false;
        }
    }

    private void StopUtilityTrace()
    {
        _utilityTraceEnabled = false;
        _utilityTraceProjectiles.Clear();
        _utilityTraceWriter?.Flush();
        _utilityTraceWriter?.Dispose();
        _utilityTraceWriter = null;
    }

    private string ResolveUtilityTracePath(string requestedPath)
    {
        var dir = Path.GetDirectoryName(ModulePath);
        if (string.IsNullOrWhiteSpace(dir))
            dir = AppContext.BaseDirectory;
        return ResolveUtilityTracePathUnder(dir, requestedPath);
    }

    internal static string ResolveUtilityTracePathUnder(string moduleDirectory, string requestedPath)
    {
        var fileName = string.IsNullOrWhiteSpace(requestedPath)
            ? DefaultUtilityTraceFileName()
            : requestedPath.Trim();
        if (Path.IsPathRooted(fileName) ||
            fileName.Contains('/') ||
            fileName.Contains('\\') ||
            fileName.Contains(':') ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "trace path must be a .csv file name without directories");
        }

        return Path.GetFullPath(Path.Combine(moduleDirectory, "traces", fileName));
    }

    private static string DefaultUtilityTraceFileName()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        return $"dtr_util_trace_{stamp}.csv";
    }

    private void TraceUtilityTick()
    {
        foreach (var slot in _loadedSlots.ToArray())
        {
            try
            {
                TraceReplaySlotTick(slot);
            }
            catch (Exception ex)
            {
                TraceUtilityMessage("slot_tick_failed", $"slot={slot} {ex.Message}");
            }
        }

        foreach (var tracked in _utilityTraceProjectiles.Values.ToArray())
        {
            try
            {
                var projectile = new CBaseCSGrenadeProjectile(tracked.Handle);
                if (!projectile.IsValid)
                {
                    _utilityTraceProjectiles.Remove(tracked.Index);
                    continue;
                }
                TraceProjectileEvent("projectile_tick", projectile, tracked);
            }
            catch (Exception ex)
            {
                _utilityTraceProjectiles.Remove(tracked.Index);
                TraceUtilityMessage("projectile_tick_failed", $"index={tracked.Index} {ex.Message}");
            }
        }

        _utilityTraceWriter?.Flush();
    }

    private void TraceReplaySlotTick(int slot)
    {
        var state = BotControllerNative.GetReplayState(slot);
        var hasTick = BotControllerNative.TryGetReplayTick(slot, out var tick);
        var bot = Utilities.GetPlayerFromSlot(slot);
        var pawn = bot?.PlayerPawn.Value;
        var liveOrigin = SafeVector(() => pawn?.AbsOrigin);
        var liveVelocity = SafeVector(() => pawn?.AbsVelocity);
        var liveAngles = SafeQAngle(() => pawn?.EyeAngles);
        var stashPosition = SafeVector(() => pawn?.StashedGrenadeThrowPosition);
        var stashVelocity = SafeVector(() => pawn?.StashedVelocity);
        var stashAngles = SafeQAngle(() => pawn?.StashedShootAngles);
        var stashSet = SafeObject(() => pawn?.GrenadeParametersStashed.ToString());
        var stashTime = SafeObject(() => pawn?.GrenadeParameterStashTime);

        TraceWrite(RowFields(
            ("kind", "slot_tick"),
            ("time", TimeField()),
            ("slot", slot),
            ("player", bot?.PlayerName ?? ""),
            ("steam_id", bot?.SteamID ?? 0UL),
            ("replay_cursor", state.Cursor),
            ("replay_total", state.Total),
            ("weapon_def", hasTick ? tick.WeaponDefIndex : null),
            ("live_weapon", ActiveWeaponName(pawn)),
            ("live_x", liveOrigin.X),
            ("live_y", liveOrigin.Y),
            ("live_z", liveOrigin.Z),
            ("live_vx", liveVelocity.X),
            ("live_vy", liveVelocity.Y),
            ("live_vz", liveVelocity.Z),
            ("live_pitch", liveAngles.X),
            ("live_yaw", liveAngles.Y),
            ("replay_pre_x", hasTick ? tick.Pre.OriginX : null),
            ("replay_pre_y", hasTick ? tick.Pre.OriginY : null),
            ("replay_pre_z", hasTick ? tick.Pre.OriginZ : null),
            ("replay_pre_vx", hasTick ? tick.Pre.VelX : null),
            ("replay_pre_vy", hasTick ? tick.Pre.VelY : null),
            ("replay_pre_vz", hasTick ? tick.Pre.VelZ : null),
            ("replay_pre_pitch", hasTick ? tick.Pre.Pitch : null),
            ("replay_pre_yaw", hasTick ? tick.Pre.Yaw : null),
            ("replay_buttons", hasTick ? Hex(tick.Pre.Buttons) : null),
            ("replay_buttons1", hasTick ? Hex(tick.Pre.Buttons1) : null),
            ("replay_buttons2", hasTick ? Hex(tick.Pre.Buttons2) : null),
            ("replay_post_x", hasTick ? tick.Post.OriginX : null),
            ("replay_post_y", hasTick ? tick.Post.OriginY : null),
            ("replay_post_z", hasTick ? tick.Post.OriginZ : null),
            ("replay_post_vx", hasTick ? tick.Post.VelX : null),
            ("replay_post_vy", hasTick ? tick.Post.VelY : null),
            ("replay_post_vz", hasTick ? tick.Post.VelZ : null),
            ("stash_set", stashSet),
            ("stash_time", stashTime),
            ("stash_x", stashPosition.X),
            ("stash_y", stashPosition.Y),
            ("stash_z", stashPosition.Z),
            ("stash_vx", stashVelocity.X),
            ("stash_vy", stashVelocity.Y),
            ("stash_vz", stashVelocity.Z),
            ("stash_pitch", stashAngles.X),
            ("stash_yaw", stashAngles.Y)
        ));
    }

    private void TraceGrenadeThrown(EventGrenadeThrown @event)
    {
        var slot = @event.Userid?.Slot ?? -1;
        var pawn = @event.Userid?.PlayerPawn.Value;
        var stashPosition = SafeVector(() => pawn?.StashedGrenadeThrowPosition);
        var stashVelocity = SafeVector(() => pawn?.StashedVelocity);
        var stashAngles = SafeQAngle(() => pawn?.StashedShootAngles);
        TraceWrite(RowFields(
            ("kind", "grenade_thrown"),
            ("time", TimeField()),
            ("slot", slot),
            ("player", @event.Userid?.PlayerName ?? ""),
            ("steam_id", @event.Userid?.SteamID ?? 0UL),
            ("live_weapon", @event.Weapon),
            ("stash_set", SafeObject(() => pawn?.GrenadeParametersStashed.ToString())),
            ("stash_time", SafeObject(() => pawn?.GrenadeParameterStashTime)),
            ("stash_x", stashPosition.X),
            ("stash_y", stashPosition.Y),
            ("stash_z", stashPosition.Z),
            ("stash_vx", stashVelocity.X),
            ("stash_vy", stashVelocity.Y),
            ("stash_vz", stashVelocity.Z),
            ("stash_pitch", stashAngles.X),
            ("stash_yaw", stashAngles.Y)
        ));
    }

    private void TraceSmokeDetonate(EventSmokegrenadeDetonate @event)
    {
        TraceWrite(RowFields(
            ("kind", "smoke_detonate"),
            ("time", TimeField()),
            ("slot", @event.Userid?.Slot ?? -1),
            ("player", @event.Userid?.PlayerName ?? ""),
            ("steam_id", @event.Userid?.SteamID ?? 0UL),
            ("event_entity", @event.Entityid),
            ("event_x", @event.X),
            ("event_y", @event.Y),
            ("event_z", @event.Z)
        ));
    }

    private void TraceSmokeExpired(EventSmokegrenadeExpired @event)
    {
        TraceWrite(RowFields(
            ("kind", "smoke_expired"),
            ("time", TimeField()),
            ("slot", @event.Userid?.Slot ?? -1),
            ("player", @event.Userid?.PlayerName ?? ""),
            ("steam_id", @event.Userid?.SteamID ?? 0UL),
            ("event_entity", @event.Entityid),
            ("event_x", @event.X),
            ("event_y", @event.Y),
            ("event_z", @event.Z)
        ));
    }

    private void TraceProjectileEvent(
        string kind,
        CBaseCSGrenadeProjectile projectile,
        UtilityProjectileTrace? tracked)
    {
        var time = Server.CurrentTime;
        var projectileName = SafeObject(() => projectile.DesignerName)?.ToString() ?? "";
        var origin = SafeVector(() => projectile.AbsOrigin);
        var absVelocity = SafeVector(() => projectile.AbsVelocity);
        var initialPosition = SafeVector(() => projectile.InitialPosition);
        var initialVelocity = SafeVector(() => projectile.InitialVelocity);
        var smokeDetonationPosition = IsSmokeProjectileName(projectileName)
            ? SafeVector(() => new CSmokeGrenadeProjectile(projectile.Handle).SmokeDetonationPos)
            : TraceVector.Empty;
        var estimate = tracked?.EstimateVelocity(origin, time) ?? TraceVector.Empty;
        tracked?.Update(origin, time);

        TraceWrite(RowFields(
            ("kind", kind),
            ("time", TimeField()),
            ("projectile_index", projectile.Index),
            ("projectile_name", projectileName),
            ("projectile_x", origin.X),
            ("projectile_y", origin.Y),
            ("projectile_z", origin.Z),
            ("projectile_abs_vx", absVelocity.X),
            ("projectile_abs_vy", absVelocity.Y),
            ("projectile_abs_vz", absVelocity.Z),
            ("projectile_est_vx", estimate.X),
            ("projectile_est_vy", estimate.Y),
            ("projectile_est_vz", estimate.Z),
            ("projectile_init_x", initialPosition.X),
            ("projectile_init_y", initialPosition.Y),
            ("projectile_init_z", initialPosition.Z),
            ("projectile_init_vx", initialVelocity.X),
            ("projectile_init_vy", initialVelocity.Y),
            ("projectile_init_vz", initialVelocity.Z),
            ("projectile_smoke_det_x", smokeDetonationPosition.X),
            ("projectile_smoke_det_y", smokeDetonationPosition.Y),
            ("projectile_smoke_det_z", smokeDetonationPosition.Z),
            ("projectile_bounces", SafeObject(() => projectile.Bounces)),
            ("projectile_is_live", SafeObject(() => projectile.IsLive))
        ));
    }


    private void TraceUtilityMessage(string kind, string message)
    {
        TraceWrite(RowFields(
            ("kind", kind),
            ("time", TimeField()),
            ("message", message)
        ));
    }

    private void TraceWrite(string line)
    {
        if (!_utilityTraceEnabled || _utilityTraceWriter == null)
            return;
        try
        {
            _utilityTraceWriter.WriteLine(line);
        }
        catch (Exception ex)
        {
            _utilityTraceEnabled = false;
            Server.PrintToConsole($"dtr: utility trace disabled after write failure: {ex.Message}");
        }
    }

    private static bool TryGetProjectileKind(
        CEntityInstance entity,
        out ReplayProjectileKind kind,
        out int weaponDefIndex)
    {
        kind = ReplayProjectileKind.Unknown;
        weaponDefIndex = -1;
        if (!entity.IsValid || string.IsNullOrEmpty(entity.DesignerName))
            return false;

        var name = entity.DesignerName;
        if (IsSmokeProjectileName(name))
        {
            kind = ReplayProjectileKind.Smoke;
            weaponDefIndex = 45;
            return true;
        }
        if (name.Contains("flashbang_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.Flash;
            weaponDefIndex = 43;
            return true;
        }
        if (name.Contains("hegrenade_projectile", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("he_grenade_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.He;
            weaponDefIndex = 44;
            return true;
        }
        if (name.Contains("incgrenade_projectile", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("incendiarygrenade_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.Molotov;
            weaponDefIndex = 48;
            return true;
        }
        if (name.Contains("molotov_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.Molotov;
            weaponDefIndex = 46;
            return true;
        }
        if (name.Contains("decoy_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.Decoy;
            weaponDefIndex = 47;
            return true;
        }

        return false;
    }

    private static bool IsSmokeProjectileName(string name)
        => name.Contains("smokegrenade_projectile", StringComparison.OrdinalIgnoreCase);

    private static string ActiveWeaponName(CCSPlayerPawn? pawn)
    {
        try
        {
            var weapon = pawn?.WeaponServices?.ActiveWeapon.Value;
            return weapon is { IsValid: true } ? weapon.DesignerName : "";
        }
        catch
        {
            return "";
        }
    }

    private static object? SafeObject(Func<object?> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static TraceVector SafeVector(Func<Vector?> read)
    {
        try
        {
            var value = read();
            return value == null
                ? TraceVector.Empty
                : new TraceVector(value.X, value.Y, value.Z);
        }
        catch
        {
            return TraceVector.Empty;
        }
    }

    private static TraceVector SafeQAngle(Func<QAngle?> read)
    {
        try
        {
            var value = read();
            return value == null
                ? TraceVector.Empty
                : new TraceVector(value.X, value.Y, value.Z);
        }
        catch
        {
            return TraceVector.Empty;
        }
    }

    private static string TimeField()
        => F(Server.CurrentTime);

    private static string Hex(ulong value)
        => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    private static string Row(params object?[] fields)
    {
        var output = new string[UtilityTraceColumns.Length];
        for (var i = 0; i < output.Length; i++)
            output[i] = CsvField(i < fields.Length ? fields[i] : null);
        return string.Join(",", output);
    }

    private static string RowFields(params (string Column, object? Value)[] fields)
    {
        var output = new object?[UtilityTraceColumns.Length];
        foreach (var (column, value) in fields)
        {
            var index = Array.IndexOf(UtilityTraceColumns, column);
            if (index >= 0)
                output[index] = value;
        }
        return Row(output);
    }

    private static string CsvField(object? value)
    {
        var text = value switch
        {
            null => "",
            string s => s,
            float f => F(f),
            double d => d.ToString("0.#####", CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
        if (text.Contains('"'))
            text = text.Replace("\"", "\"\"");
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{text}\""
            : text;
    }

    private static string F(float value)
        => value.ToString("0.#####", CultureInfo.InvariantCulture);
}
