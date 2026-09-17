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
        private static readonly PluginCapability<IBotRandomizerApi> Capability = new(BotRandomizerContract.Capability);
        private IBotRandomizerApi? _api;
        private bool _resolved;

        public void Refresh()
        {
            _api = null;
            _resolved = false;
        }

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
                Refresh();
                return false;
            }
        }

        public BotRandomizerReplayPlanResult Acquire(
            string owner,
            BotRandomizerReplayCosmeticPlan[] plans, CancellationToken ownerLifetime)
        {
            if (!TryGetApi(out var api))
                return Fail("provider_unavailable");
            try
            {
                return api.AcquireReplayPlan(owner, plans, ownerLifetime);
            }
            catch (Exception ex)
            {
                Refresh();
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
                return api.ReleaseReplayPlan(leaseToken);
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
                return api.ReleaseReplayPlansByOwner(owner);
            }
            catch
            {
                Refresh();
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
                Refresh();
                return null;
            }
        }

        public BotRandomizerProviderInfo? ProbeProviderInfo() => GetProviderInfo();

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
                Refresh();
                return null;
            }
        }

        // Provider lifecycle notifications invalidate this reference. Operations
        // still validate current native ownership; no availability TTL is used.
        private bool TryGetApi(out IBotRandomizerApi api)
        {
            if (!_resolved)
            {
                _resolved = true;
                try { _api = Capability.Get(); }
                catch { _api = null; }
            }
            api = _api!;
            return api != null && api.ApiVersion == BotRandomizerContract.ApiVersion;
        }
        private static BotRandomizerReplayPlanResult Fail(string reason)
            => new()
            {
                Ok = false,
                Reason = reason
            };
    }
}
