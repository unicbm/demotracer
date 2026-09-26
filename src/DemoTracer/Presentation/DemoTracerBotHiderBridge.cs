/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core.Capabilities;
using DemoTracerBotHiderApi;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private sealed class DemoTracerBotHiderBridge
    {
        private static readonly PluginCapability<IBotHiderApi> Capability = new(DemoTracerBotHiderContract.Capability);
        private IBotHiderApi? _api;
        private bool _resolved;

        public void Refresh()
        {
            _api = null;
            _resolved = false;
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
                Refresh();
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
                Refresh();
                return false;
            }
        }

        public BotHiderPresentationLeaseResult Acquire(
            string owner,
            BotHiderPresentationOverride[] overrides, CancellationToken ownerLifetime)
        {
            if (!TryGetApi(out var api))
                return Fail("provider_unavailable");
            try
            {
                return api.AcquirePresentationLease(owner, overrides, ownerLifetime);
            }
            catch (Exception ex)
            {
                Refresh();
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
                Refresh();
                return Fail($"provider_error:{ex.Message}");
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
                Refresh();
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
                Refresh();
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
                Refresh();
                return null;
            }
        }

        public BotHiderProviderInfo? ProbeProviderInfo() => GetProviderInfo();

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
                Refresh();
                return null;
            }
        }

        // Provider lifecycle notifications invalidate this reference. Operations
        // still validate current native ownership; no availability TTL is used.
        private bool TryGetApi(out IBotHiderApi api)
        {
            if (!_resolved)
            {
                _resolved = true;
                try { _api = Capability.Get(); }
                catch { _api = null; }
            }
            api = _api!;
            return api != null && api.ApiVersion == DemoTracerBotHiderContract.ApiVersion;
        }
        private static BotHiderPresentationLeaseResult Fail(string reason)
            => new()
            {
                Ok = false,
                Reason = reason
            };
    }
}
