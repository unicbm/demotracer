/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotHiderApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core.Capabilities;

namespace DemoTracer;

internal static class BotHiderAvatars
{
    private static readonly PluginCapability<IBotHiderApi> Capability = new("bothider:api");

    // Publishes a demo-backed PNG through the unified BotHider provider.
    public static bool TryPublishAvatarOverride(ulong steamId, byte[] png, out string error)
    {
        try
        {
            var api = Capability.Get();
            if (api != null) return api.TryPublishAvatarOverride(steamId, png, out error);
            error = "BotHider provider unavailable";
        }
        catch (Exception ex) { error = ex.Message; }
        return false;
    }

    // Restores the SteamID avatar previously published by DemoTracer.
    public static bool TryClearAvatarOverride(ulong steamId, out string error)
    {
        try
        {
            var api = Capability.Get();
            if (api != null) return api.TryClearAvatarOverride(steamId, out error);
            error = "BotHider provider unavailable";
        }
        catch (Exception ex) { error = ex.Message; }
        return false;
    }

    // Releases direct avatar overrides when playback stops.
    public static void ClearAvatarOverrides()
    {
        try { Capability.Get()?.ClearAvatarOverrides(); }
        catch (Exception ex) { Server.PrintToConsole($"dtr: avatar cleanup failed: {ex.Message}"); }
    }
}
