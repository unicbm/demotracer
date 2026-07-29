using CounterStrikeSharp.API.Core.Capabilities;
using DemoTracerBotHiderApi;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private sealed class DemoTracerBotHiderBridge
    {
        private const long CapabilityRetryDelayMilliseconds = 1_000;
        private const long ProviderValidationIntervalMilliseconds = 500;
        private static readonly PluginCapability<IBotHiderApi> Capability =
            new(DemoTracerBotHiderContract.Capability);

        private IBotHiderApi? _api;
        private long _nextCapabilityLookupAtMilliseconds;
        private long _providerValidationExpiresAtMilliseconds;
        private bool _tickQueryScopeActive;
        private bool _providerValidatedInTickQueryScope;

        public void Refresh()
            => InvalidateApi(throttleCapabilityLookup: false);

        public void BeginTickQueryScope()
        {
            _tickQueryScopeActive = true;
            _providerValidatedInTickQueryScope = false;
        }

        public void EndTickQueryScope()
        {
            _tickQueryScopeActive = false;
            _providerValidatedInTickQueryScope = false;
        }

        public bool IsAvailable()
            => TryGetApi(out _);

        public bool IsManagedBot(int slot)
        {
            if (!TryGetApi(out var api))
                return false;
            try
            {
                return api.IsManagedBot(slot);
            }
            catch
            {
                InvalidateApi(throttleCapabilityLookup: true);
                return false;
            }
        }

        public bool TryGetManagedSlot(int slot, out BotHiderManagedSlot state)
        {
            state = new BotHiderManagedSlot { Slot = slot };
            if (!TryGetApi(out var api))
                return false;
            try
            {
                return api.TryGetManagedSlot(slot, out state);
            }
            catch
            {
                InvalidateApi(throttleCapabilityLookup: true);
                return false;
            }
        }

        public BotHiderPresentationLeaseResult Acquire(
            string owner,
            BotHiderPresentationOverride[] overrides)
        {
            if (!TryGetApi(out var api))
                return Fail("provider_unavailable");
            try
            {
                return api.AcquirePresentationLease(owner, overrides);
            }
            catch (Exception ex)
            {
                InvalidateApi(throttleCapabilityLookup: true);
                return Fail($"provider_error:{ex.Message}");
            }
        }

        public BotHiderPresentationLeaseResult Replace(
            string leaseToken,
            BotHiderPresentationOverride[] overrides)
        {
            if (!TryGetApi(out var api))
                return Fail("provider_unavailable");
            try
            {
                return api.ReplacePresentationLease(leaseToken, overrides);
            }
            catch (Exception ex)
            {
                InvalidateApi(throttleCapabilityLookup: true);
                return Fail($"provider_error:{ex.Message}");
            }
        }

        public bool Heartbeat(string leaseToken)
        {
            if (!TryGetApi(out var api))
                return false;
            try
            {
                return api.HeartbeatPresentationLease(leaseToken);
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
                return api.ReleasePresentationLease(leaseToken);
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
                return api.ReleasePresentationLeasesByOwner(owner);
            }
            catch
            {
                InvalidateApi(throttleCapabilityLookup: true);
                return 0;
            }
        }

        public BotHiderProviderInfo? GetProviderInfo()
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

        public BotHiderProviderInfo? ProbeProviderInfo()
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

        public BotHiderDiagnostics? GetDiagnostics()
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

        private bool TryGetApi(out IBotHiderApi api)
        {
            var now = Environment.TickCount64;
            // Cache only provider availability. Every safety decision still calls
            // IsManagedBot/TryGetManagedSlot on the provider for the current slot.
            if (_api != null &&
                (_tickQueryScopeActive && _providerValidatedInTickQueryScope ||
                 now < _providerValidationExpiresAtMilliseconds ||
                 ValidateCachedApi(_api, now)))
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
                    if (_tickQueryScopeActive)
                        _providerValidatedInTickQueryScope = true;
                }
            }
            catch
            {
                InvalidateApi(throttleCapabilityLookup: true);
            }

            api = _api!;
            return api != null;
        }

        private bool ValidateCachedApi(IBotHiderApi api, long now)
        {
            if (!ProviderIsUsable(api))
                return false;

            _providerValidationExpiresAtMilliseconds = now + ProviderValidationIntervalMilliseconds;
            if (_tickQueryScopeActive)
                _providerValidatedInTickQueryScope = true;
            return true;
        }

        private void InvalidateApi(bool throttleCapabilityLookup)
        {
            _api = null;
            _providerValidatedInTickQueryScope = false;
            _providerValidationExpiresAtMilliseconds = 0;
            _nextCapabilityLookupAtMilliseconds = throttleCapabilityLookup
                ? Environment.TickCount64 + CapabilityRetryDelayMilliseconds
                : 0;
        }

        private static bool ProviderIsUsable(IBotHiderApi api)
        {
            try
            {
                var provider = api.GetProviderInfo();
                return api.ApiVersion == DemoTracerBotHiderContract.ApiVersion &&
                       provider.ApiVersion == DemoTracerBotHiderContract.ApiVersion &&
                       provider.Connected &&
                       !provider.Draining;
            }
            catch
            {
                return false;
            }
        }

        private static BotHiderPresentationLeaseResult Fail(string reason)
            => new()
            {
                Ok = false,
                Reason = reason
            };
    }
}
