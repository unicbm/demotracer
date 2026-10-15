/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using DtrHiderApi;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const int RuntimeHealthSchemaVersion = 1;
    private const int MinimumBotControllerAbiMinor = 1;
    private const long RuntimeHealthWriteIntervalMilliseconds = 10_000;
    private const string RuntimeHealthFileName = "demotracer-runtime.v1.json";
    private static readonly JsonSerializerOptions RuntimeHealthJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private long _nextRuntimeHealthWriteAtMilliseconds;
    private bool _runtimeHealthRunning;
    private Task _runtimeHealthWrite = Task.CompletedTask;

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
        // Finish the stopped snapshot before a hot reload starts another writer.
        _runtimeHealthWrite.GetAwaiter().GetResult();
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
        var healthPath = Path.Combine(ModuleDirectory, RuntimeHealthFileName);
        var snapshot = BuildRuntimeHealthSnapshot();
        // Sample providers on the game thread; serialize and write in submission order.
        _runtimeHealthWrite = _runtimeHealthWrite.ContinueWith(
            _ => WriteRuntimeHealthSnapshot(healthPath, snapshot), TaskScheduler.Default);
    }

    private void WriteRuntimeHealthSnapshot(string healthPath, RuntimeHealthSnapshot snapshot)
    {
        var temporaryPath = healthPath + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(snapshot, RuntimeHealthJsonOptions);
            File.WriteAllText(temporaryPath, json + Environment.NewLine);
            File.Move(temporaryPath, healthPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(ex, "Could not write runtime health heartbeat");
        }
    }

    private RuntimeHealthSnapshot BuildRuntimeHealthSnapshot()
    {
        var abiInfo = BotControllerNative.AbiInfo;
        var abiMajor = abiInfo.AbiMajor;
        var abiMinor = Math.Max(0, abiInfo.AbiMinor);
        var capabilities = abiInfo.Capabilities;
        var requiredCapabilitiesPresent =
            (BotControllerNative.RequiredCapabilityMask & ~capabilities) == 0;
        var controllerCompatible =
            abiMajor == BotControllerNative.ExpectedAbiVersion &&
            abiMinor >= MinimumBotControllerAbiMinor &&
            requiredCapabilitiesPresent;

        var provider = _botHiderBridge.ProbeProviderInfo();
        var botHiderAvailable =
            provider != null &&
            provider.ApiVersion == DtrHiderContract.ApiVersion &&
            provider.Connected &&
            !provider.Draining;
        var botRandomizerProvider = _botRandomizerBridge.ProbeProviderInfo();
        var botRandomizerAvailable =
            botRandomizerProvider != null &&
            botRandomizerProvider.ApiVersion == BotRandomizerApi.BotRandomizerContract.ApiVersion &&
            botRandomizerProvider.Ready &&
            !botRandomizerProvider.Draining &&
            botRandomizerProvider.ReplayPlanPrebuildAvailable;

        return new RuntimeHealthSnapshot(
            SchemaVersion: RuntimeHealthSchemaVersion,
            WrittenAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Running: _runtimeHealthRunning,
            PluginVersion: ModuleVersion,
            BotController: new RuntimeBotControllerHealth(controllerCompatible),
            BotHider: new RuntimeProviderHealth(botHiderAvailable),
            BotRandomizer: new RuntimeProviderHealth(botRandomizerAvailable));
    }

    private sealed record RuntimeHealthSnapshot(
        int SchemaVersion,
        long WrittenAtMs,
        bool Running,
        string PluginVersion,
        RuntimeBotControllerHealth BotController,
        RuntimeProviderHealth BotHider,
        RuntimeProviderHealth BotRandomizer);

    private sealed record RuntimeBotControllerHealth(bool Compatible);
    private sealed record RuntimeProviderHealth(bool Available);
}
