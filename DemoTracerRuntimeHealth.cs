using CounterStrikeSharp.API.Core;
using DemoTracerBotHiderApi;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const int RuntimeHealthSchemaVersion = 1;
    private const int MinimumBotControllerAbiMinor = 33;
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
            LoadedCssPluginDirectories: DiscoverLoadedCssPluginDirectories());
    }

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
}
