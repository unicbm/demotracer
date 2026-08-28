/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotRandomizerApi;
using CounterStrikeSharp.API.Core.Capabilities;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private sealed class DemoTracerBotRandomizerBridge
    {
        private const long CapabilityRetryDelayMilliseconds = 1_000;
        private const long ProviderValidationIntervalMilliseconds = 500;
        private static readonly PluginCapability<IBotRandomizerApi> Capability =
            new(BotRandomizerContract.Capability);

        private IBotRandomizerApi? _api;
        private long _nextCapabilityLookupAtMilliseconds;
        private long _providerValidationExpiresAtMilliseconds;

        public void Refresh()
            => InvalidateApi(throttleCapabilityLookup: false);

        public bool IsAvailable()
            => TryGetApi(out _);

        public bool TryGetManagedBot(int slot, out BotRandomizerManagedBot state)
        {
            state = new BotRandomizerManagedBot { Slot = slot };
            if (!TryGetApi(out var api))
                return false;
            try
            {
                return api.TryGetManagedBot(slot, out state);
            }
            catch
            {
                InvalidateApi(throttleCapabilityLookup: true);
                return false;
            }
        }

        public BotRandomizerReplayPlanResult Acquire(
            string owner,
            BotRandomizerReplayCosmeticPlan[] plans)
        {
            if (!TryGetApi(out var api))
                return Fail("provider_unavailable");
            try
            {
                return api.AcquireReplayPlan(owner, plans);
            }
            catch (Exception ex)
            {
                InvalidateApi(throttleCapabilityLookup: true);
                return Fail($"provider_error:{ex.Message}");
            }
        }

        public BotRandomizerReplayPlanResult Replace(
            string planToken,
            BotRandomizerReplayCosmeticPlan[] plans)
        {
            if (!TryGetApi(out var api))
                return Fail("provider_unavailable");
            try
            {
                return api.ReplaceReplayPlan(planToken, plans);
            }
            catch (Exception ex)
            {
                InvalidateApi(throttleCapabilityLookup: true);
                return Fail($"provider_error:{ex.Message}");
            }
        }

        public bool Heartbeat(string leaseToken)
        {
            if (string.IsNullOrWhiteSpace(leaseToken) || !TryGetApi(out var api))
                return false;
            try
            {
                return api.HeartbeatReplayPlan(leaseToken);
            }
            catch
            {
                InvalidateApi(throttleCapabilityLookup: true);
                return false;
            }
        }

        public bool Release(string leaseToken)
        {
            if (string.IsNullOrWhiteSpace(leaseToken))
                return true;
            if (!TryGetApi(out var api))
                return false;
            try
            {
                return api.ReleaseReplayPlan(leaseToken);
            }
            catch
            {
                InvalidateApi(throttleCapabilityLookup: true);
                return false;
            }
        }

        public int ReleaseOwner(string owner)
        {
            if (!TryGetApi(out var api))
                return 0;
            try
            {
                return api.ReleaseReplayPlansByOwner(owner);
            }
            catch
            {
                InvalidateApi(throttleCapabilityLookup: true);
                return 0;
            }
        }

        public BotRandomizerProviderInfo? GetProviderInfo()
        {
            if (!TryGetApi(out var api))
                return null;
            try
            {
                return api.GetProviderInfo();
            }
            catch
            {
                InvalidateApi(throttleCapabilityLookup: true);
                return null;
            }
        }

        public BotRandomizerProviderInfo? ProbeProviderInfo()
        {
            try
            {
                var api = _api ?? Capability.Get();
                return api?.GetProviderInfo();
            }
            catch
            {
                return null;
            }
        }

        public BotRandomizerDiagnostics? GetDiagnostics()
        {
            if (!TryGetApi(out var api))
                return null;
            try
            {
                return api.GetDiagnostics();
            }
            catch
            {
                InvalidateApi(throttleCapabilityLookup: true);
                return null;
            }
        }

        private bool TryGetApi(out IBotRandomizerApi api)
        {
            var now = Environment.TickCount64;
            if (_api != null &&
                (now < _providerValidationExpiresAtMilliseconds || ValidateCachedApi(_api, now)))
            {
                api = _api;
                return true;
            }

            if (_api != null)
                InvalidateApi(throttleCapabilityLookup: true);

            if (now < _nextCapabilityLookupAtMilliseconds)
            {
                api = null!;
                return false;
            }

            _nextCapabilityLookupAtMilliseconds = now + CapabilityRetryDelayMilliseconds;
            try
            {
                var candidate = Capability.Get();
                if (candidate != null && ProviderIsUsable(candidate))
                {
                    _api = candidate;
                    _nextCapabilityLookupAtMilliseconds = 0;
                    _providerValidationExpiresAtMilliseconds = now + ProviderValidationIntervalMilliseconds;
                }
            }
            catch
            {
                InvalidateApi(throttleCapabilityLookup: true);
            }

            api = _api!;
            return api != null;
        }

        private bool ValidateCachedApi(IBotRandomizerApi api, long now)
        {
            if (!ProviderIsUsable(api))
                return false;

            _providerValidationExpiresAtMilliseconds = now + ProviderValidationIntervalMilliseconds;
            return true;
        }

        private void InvalidateApi(bool throttleCapabilityLookup)
        {
            _api = null;
            _providerValidationExpiresAtMilliseconds = 0;
            _nextCapabilityLookupAtMilliseconds = throttleCapabilityLookup
                ? Environment.TickCount64 + CapabilityRetryDelayMilliseconds
                : 0;
        }

        private static bool ProviderIsUsable(IBotRandomizerApi api)
        {
            try
            {
                var provider = api.GetProviderInfo();
                return api.ApiVersion == BotRandomizerContract.ApiVersion &&
                       provider.ApiVersion == BotRandomizerContract.ApiVersion &&
                       provider.Ready &&
                       !provider.Draining;
            }
            catch
            {
                return false;
            }
        }

        private static BotRandomizerReplayPlanResult Fail(string reason)
            => new()
            {
                Ok = false,
                Reason = reason
            };
    }
}
