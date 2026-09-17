/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotRandomizerApi;
using CounterStrikeSharp.API;
using DemoTracerBotHiderApi;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private readonly CancellationTokenSource _presentationLifetime = new();
    private bool _providerSyncPending;

    private void StartPresentationLifetime()
    {
        DemoTracerBotHiderContract.ProviderChanged += OnPresentationProviderChanged;
        BotRandomizerContract.ProviderChanged += OnPresentationProviderChanged;
    }

    private void StopPresentationLifetime()
    {
        DemoTracerBotHiderContract.ProviderChanged -= OnPresentationProviderChanged;
        BotRandomizerContract.ProviderChanged -= OnPresentationProviderChanged;
        // Cancellation releases both providers' claims even if another unload
        // cleanup failed. They never need a timer to infer that this owner left.
        try { _presentationLifetime.Cancel(); }
        finally { _presentationLifetime.Dispose(); }
    }

    private void OnPresentationProviderChanged()
    {
        if (_presentationLifetime.IsCancellationRequested) return;
        _botHiderBridge.Refresh();
        _botRandomizerBridge.Refresh();
        _botHiderPresentationSignature = string.Empty;
        _activeBotHiderReplaySteamIds.Clear();
        _botRandomizerLeaseSignature = string.Empty;
        if (_providerSyncPending) return;
        _providerSyncPending = true;
        Server.NextFrame(() =>
        {
            _providerSyncPending = false;
            if (_presentationLifetime.IsCancellationRequested) return;
            _ = SyncBotHiderPresentationLease(announce: false, forceReplace: true);
            _ = SyncBotRandomizerCosmeticLease(announce: false);
            RefreshRuntimeHealthHeartbeat();
        });
    }
}
