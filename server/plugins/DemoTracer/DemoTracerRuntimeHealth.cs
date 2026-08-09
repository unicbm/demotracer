/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using DemoTracerBotHiderApi;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const int RuntimeHealthSchemaVersion = 1;
    private const int MinimumBotControllerAbiMinor = 34;
    private const long RuntimeHealthWriteIntervalMilliseconds = 10_000;
    private const string RuntimeHealthFileName = "demotracer-runtime.v1.json";
    private static readonly JsonSerializerOptions RuntimeHealthJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private long _nextRuntimeHealthWriteAtMilliseconds;
    private bool _runtimeHealthRunning;

    private void StartRuntimeHealthHeartbeat()
    {
        _runtimeHealthRunning = true;
        WriteRuntimeHealthHeartbeat(force: true);
    }

    private void RefreshRuntimeHealthHeartbeat()
        => WriteRuntimeHealthHeartbeat(force: true);

    private void StopRuntimeHealthHeartbeat()
    {
        _runtimeHealthRunning = false;
        WriteRuntimeHealthHeartbeat(force: true);
    }

    private void TickRuntimeHealthHeartbeat()
    {
        var now = Environment.TickCount64;
        if (now < _nextRuntimeHealthWriteAtMilliseconds)
            return;

        WriteRuntimeHealthHeartbeat(force: false, now);
    }

    private void WriteRuntimeHealthHeartbeat(bool force, long? sampledTickMilliseconds = null)
    {
        var now = sampledTickMilliseconds ?? Environment.TickCount64;
        if (!force && now < _nextRuntimeHealthWriteAtMilliseconds)
            return;

        _nextRuntimeHealthWriteAtMilliseconds = now + RuntimeHealthWriteIntervalMilliseconds;

        string? temporaryPath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(ModuleDirectory))
                return;

            var healthPath = Path.Combine(ModuleDirectory, RuntimeHealthFileName);
            temporaryPath = Path.Combine(
                ModuleDirectory,
                $".{RuntimeHealthFileName}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            var snapshot = BuildRuntimeHealthSnapshot();
            var json = JsonSerializer.Serialize(snapshot, RuntimeHealthJsonOptions);

            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.WriteLine();
                writer.Flush();
            }

            File.Move(temporaryPath, healthPath, overwrite: true);
            temporaryPath = null;
        }
        catch
        {
            // Health reporting is best-effort and must never affect replay runtime.
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // A stale temporary heartbeat is harmless and is never consumed.
                }
            }
        }
    }

    private RuntimeHealthSnapshot BuildRuntimeHealthSnapshot()
    {
        var abiInfo = BotControllerNative.AbiInfo;
        var abiMajor = abiInfo.AbiMajor >= 0
            ? abiInfo.AbiMajor
            : BotControllerNative.AbiVersion;
        var abiMinor = Math.Max(0, abiInfo.AbiMinor);
        var capabilities = BotControllerNative.Capabilities;
        var missingCapabilities = BotControllerNative.RequiredCapabilityMask & ~capabilities;
        var requiredCapabilitiesPresent = missingCapabilities == 0;
        var controllerCompatible =
            abiMajor == BotControllerNative.ExpectedAbiVersion &&
            abiMinor >= MinimumBotControllerAbiMinor &&
            requiredCapabilitiesPresent;

        var provider = _botHiderBridge.ProbeProviderInfo();
        var botHiderAvailable =
            provider != null &&
            provider.ApiVersion == DemoTracerBotHiderContract.ApiVersion &&
            provider.Connected &&
            !provider.Draining;

        return new RuntimeHealthSnapshot(
            SchemaVersion: RuntimeHealthSchemaVersion,
            WrittenAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Running: _runtimeHealthRunning,
            PluginVersion: ModuleVersion,
            DemoTracerApi: BotControllerNative.DemoTracerApiVersion,
            CounterStrikeSharpVersion: GetCounterStrikeSharpVersion(),
            BotController: new RuntimeBotControllerHealth(
                AbiMajor: abiMajor,
                AbiMinor: abiMinor,
                Capabilities: FormatCapabilityMask(capabilities),
                BuildId: SanitizeBuildId(BotControllerNative.BuildId),
                Compatible: controllerCompatible,
                RequiredCapabilities: new RuntimeRequiredCapabilitiesHealth(
                    Mask: FormatCapabilityMask(BotControllerNative.RequiredCapabilityMask),
                    Present: requiredCapabilitiesPresent,
                    Missing: FormatCapabilityMask(missingCapabilities))),
            BotHider: new RuntimeBotHiderHealth(
                ProviderApi: provider?.ApiVersion,
                Connected: provider?.Connected ?? false,
                Draining: provider?.Draining ?? false,
                Available: botHiderAvailable),
            Cosmetics: new RuntimeCosmeticAlignmentHealth(
                AlignmentEnabled: _cosmeticAlignEnabled,
                WeaponsEnabled: _cosmeticWeaponsEnabled,
                KnivesEnabled: _cosmeticKnivesEnabled,
                GlovesEnabled: _cosmeticGlovesEnabled,
                NamesEnabled: _cosmeticNamesEnabled,
                AgentsEnabled: _cosmeticAgentsEnabled,
                StickersEnabled: _stickerAlignEnabled,
                CharmsEnabled: _charmAlignEnabled,
                PreserveNativeEnabled: _preserveNativeBotCosmetics),
            ReplayWeapons: BuildRuntimeReplayWeaponHealth(),
            LoadedCssPluginDirectories: DiscoverLoadedCssPluginDirectories());
    }

    private RuntimeReplayWeaponHealth[] BuildRuntimeReplayWeaponHealth()
    {
        var snapshots = new List<RuntimeReplayWeaponHealth>();
        foreach (var slot in _session.LoadedSlots.Distinct().Order())
        {
            try
            {
                var replayState = BotControllerNative.GetReplayState(slot);
                var player = Utilities.GetPlayerFromSlot(slot);
                var pawn = player?.PlayerPawn.Value;
                var weaponServices = pawn?.WeaponServices;
                var activeHandle = weaponServices?.ActiveWeapon.Raw
                                   ?? Utilities.InvalidEHandleIndex;
                var activeWeapon = weaponServices?.ActiveWeapon.Value;
                var activeClassName = activeWeapon is { IsValid: true }
                    ? activeWeapon.DesignerName
                    : null;
                var inventory = new List<RuntimeReplayInventoryWeaponHealth>();
                if (weaponServices != null)
                {
                    foreach (var handle in weaponServices.MyWeapons)
                    {
                        var weapon = handle.Value;
                        if (weapon is not { IsValid: true })
                            continue;

                        var weaponHandle = weapon.EntityHandle.Raw;
                        inventory.Add(new RuntimeReplayInventoryWeaponHealth(
                            Handle: FormatEntityHandle(weaponHandle),
                            ClassName: weapon.DesignerName,
                            DefIndex: WeaponDefIndex(weapon.DesignerName),
                            Slot: GetReplayWeaponSlot(weapon.DesignerName).ToString(),
                            Active: weaponHandle == activeHandle));
                    }
                }

                int? cachedReplayDefIndex = _session.LastReplayWeaponDef.TryGetValue(
                    slot,
                    out var cachedDefIndex)
                    ? cachedDefIndex
                    : null;
                int? lockedTarget = _session.LastLockedWeaponTarget.TryGetValue(
                    slot,
                    out var target)
                    ? target
                    : null;
                RuntimeReplayCosmeticClaimHealth? cosmeticClaim = null;
                if (_session.LoadedReplays.TryGetValue(slot, out var replay) &&
                    _botRandomizerLease.TryGet(
                        slot,
                        replay.SteamId,
                        out var claim))
                {
                    cosmeticClaim = new RuntimeReplayCosmeticClaimHealth(
                        Agent: claim.Agent,
                        Knife: claim.Knife,
                        Gloves: claim.Gloves,
                        MusicKit: claim.MusicKit,
                        WeaponCount: claim.Weapons.Count);
                }

                snapshots.Add(new RuntimeReplayWeaponHealth(
                    Slot: slot,
                    UserId: player?.UserId,
                    Alive: player is { IsValid: true, PawnIsAlive: true },
                    PawnHandle: pawn is { IsValid: true }
                        ? FormatEntityHandle(pawn.EntityHandle.Raw)
                        : null,
                    ActiveHandle: FormatEntityHandle(activeHandle),
                    ActiveClassName: activeClassName,
                    ActiveManagedDefIndex: activeClassName != null
                        ? WeaponDefIndex(activeClassName)
                        : -1,
                    ActiveNativeDefIndex: BotControllerNative.BotActiveWeaponDef(slot),
                    ReplayPlaying: replayState.Playing,
                    ReplayCursor: replayState.Cursor,
                    ReplayDefIndex: replayState.WeaponDefIndex,
                    CachedReplayDefIndex: cachedReplayDefIndex,
                    LockedTarget: lockedTarget,
                    LoadoutSynced: _session.LoadoutSyncedSlots.Contains(slot),
                    PendingSlotReplacements: _session.PendingWeaponSlotReplacements.Keys.Count(
                        key => key.PlayerSlot == slot),
                    CosmeticClaim: cosmeticClaim,
                    Inventory: inventory.ToArray(),
                    Error: null));
            }
            catch (Exception ex)
            {
                snapshots.Add(new RuntimeReplayWeaponHealth(
                    Slot: slot,
                    UserId: null,
                    Alive: false,
                    PawnHandle: null,
                    ActiveHandle: null,
                    ActiveClassName: null,
                    ActiveManagedDefIndex: -1,
                    ActiveNativeDefIndex: -1,
                    ReplayPlaying: false,
                    ReplayCursor: -1,
                    ReplayDefIndex: -1,
                    CachedReplayDefIndex: null,
                    LockedTarget: null,
                    LoadoutSynced: false,
                    PendingSlotReplacements: 0,
                    CosmeticClaim: null,
                    Inventory: [],
                    Error: ex.GetType().Name));
            }
        }

        return snapshots.ToArray();
    }

    private static string FormatEntityHandle(uint value)
        => value == Utilities.InvalidEHandleIndex
            ? "invalid"
            : $"0x{value:X8}";

    private string[] DiscoverLoadedCssPluginDirectories()
    {
        try
        {
            var moduleDirectory = Path.GetFullPath(ModuleDirectory);
            var pluginsDirectory = Directory.GetParent(moduleDirectory)?.FullName;
            if (string.IsNullOrWhiteSpace(pluginsDirectory))
                return [];

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentPluginDirectory = Path.GetFileName(
                Path.TrimEndingDirectorySeparator(moduleDirectory));
            if (!string.IsNullOrWhiteSpace(currentPluginDirectory) &&
                currentPluginDirectory is not "." and not ".." &&
                currentPluginDirectory.Length <= 128)
            {
                names.Add(currentPluginDirectory);
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string location;
                try
                {
                    location = assembly.Location;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(location))
                    continue;

                string relativePath;
                try
                {
                    relativePath = Path.GetRelativePath(
                        pluginsDirectory,
                        Path.GetFullPath(location));
                }
                catch
                {
                    continue;
                }

                if (Path.IsPathRooted(relativePath) ||
                    relativePath.Equals("..", StringComparison.Ordinal) ||
                    relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var separatorIndex = relativePath.IndexOfAny(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
                if (separatorIndex <= 0)
                    continue;

                var directoryName = relativePath[..separatorIndex];
                if (directoryName is "." or ".." || directoryName.Length > 128)
                    continue;

                names.Add(directoryName);
            }

            return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string FormatCapabilityMask(ulong value)
        => $"0x{value:X}";

    private static string GetCounterStrikeSharpVersion()
        => typeof(BasePlugin).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static string SanitizeBuildId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var trimmed = value.Trim();
        var length = Math.Min(trimmed.Length, 128);
        var sanitized = new StringBuilder(length);
        for (var index = 0; index < length; index++)
        {
            var character = trimmed[index];
            sanitized.Append(char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or '+'
                ? character
                : '_');
        }

        return sanitized.Length > 0 ? sanitized.ToString() : "unknown";
    }

    private sealed record RuntimeHealthSnapshot(
        int SchemaVersion,
        long WrittenAtMs,
        bool Running,
        string PluginVersion,
        int DemoTracerApi,
        string CounterStrikeSharpVersion,
        RuntimeBotControllerHealth BotController,
        RuntimeBotHiderHealth BotHider,
        RuntimeCosmeticAlignmentHealth Cosmetics,
        RuntimeReplayWeaponHealth[] ReplayWeapons,
        string[] LoadedCssPluginDirectories);

    private sealed record RuntimeBotControllerHealth(
        int AbiMajor,
        int AbiMinor,
        string Capabilities,
        string BuildId,
        bool Compatible,
        RuntimeRequiredCapabilitiesHealth RequiredCapabilities);

    private sealed record RuntimeRequiredCapabilitiesHealth(
        string Mask,
        bool Present,
        string Missing);

    private sealed record RuntimeBotHiderHealth(
        int? ProviderApi,
        bool Connected,
        bool Draining,
        bool Available);

    private sealed record RuntimeCosmeticAlignmentHealth(
        bool AlignmentEnabled,
        bool WeaponsEnabled,
        bool KnivesEnabled,
        bool GlovesEnabled,
        bool NamesEnabled,
        bool AgentsEnabled,
        bool StickersEnabled,
        bool CharmsEnabled,
        bool PreserveNativeEnabled);

    private sealed record RuntimeReplayWeaponHealth(
        int Slot,
        int? UserId,
        bool Alive,
        string? PawnHandle,
        string? ActiveHandle,
        string? ActiveClassName,
        int ActiveManagedDefIndex,
        int ActiveNativeDefIndex,
        bool ReplayPlaying,
        int ReplayCursor,
        int ReplayDefIndex,
        int? CachedReplayDefIndex,
        int? LockedTarget,
        bool LoadoutSynced,
        int PendingSlotReplacements,
        RuntimeReplayCosmeticClaimHealth? CosmeticClaim,
        RuntimeReplayInventoryWeaponHealth[] Inventory,
        string? Error);

    private sealed record RuntimeReplayCosmeticClaimHealth(
        bool Agent,
        bool Knife,
        bool Gloves,
        bool MusicKit,
        int WeaponCount);

    private sealed record RuntimeReplayInventoryWeaponHealth(
        string Handle,
        string ClassName,
        int DefIndex,
        string Slot,
        bool Active);
}
