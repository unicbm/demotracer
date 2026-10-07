/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Plugin;
using CounterStrikeSharp.API.Core.Plugin.Host;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const string OriginalHiderPath = "addons/BotHider/bin/win64/BotHider";
    private const string ReplayHiderPath = "addons/dtr-hider/bin/win64/dtr-hider";
    private CounterStrikeSharp.API.Modules.Timers.Timer? _hiderSwitchTimer;
    private bool _restoreOriginalHider;
    private IPluginContext? _restoreOriginalHiderManaged;
    private bool _hiderUsedThisMap;

    private static IPluginContext? OriginalHiderManaged()
    {
        // CSS keeps its plugin query service private; use the actual load state,
        // not a capability delegate that can survive provider unload.
        var plugins = (IPluginContextQueryHandler)typeof(Application)
            .GetField("_pluginContextQueryHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Application.Instance)!;
        return plugins.FindPluginByModuleName("BotHiderImpl");
    }

    private bool EnsureHiderForCommand(CommandInfo command)
    {
        if (BotControllerNative.AbiInfo.AbiMinor < MinimumBotControllerAbiMinor)
        {
            command.ReplyToCommand("[DTR ERR] Hider switching requires a matching dtr-controller with native ABI 22.1 or newer.");
            return false;
        }
        if (_hiderSwitchTimer != null)
        {
            command.ReplyToCommand("[DTR] Hider switching is in progress. Please wait.");
            return false;
        }
        var nativeId = BotControllerNative.FindMetamodPlugin("BotHider");
        var managed = OriginalHiderManaged();
        var managedLoaded = managed?.State == PluginState.Loaded;
        if (nativeId == 0 && !managedLoaded && _botHiderBridge.GetProviderInfo()?.Connected == true)
        {
            _hiderUsedThisMap = true;
            return true;
        }

        _restoreOriginalHider |= nativeId != 0;
        _hiderUsedThisMap = true;
        var pendingCommand = command.GetCommandString;
        command.ReplyToCommand("[DTR] Unloading BotHider and loading dtr-hider; playback will continue once ready.");
        if (managedLoaded)
        {
            _restoreOriginalHiderManaged = managed;
            managed!.Unload(hotReload: false);
        }
        if (nativeId != 0) Server.ExecuteCommand($"meta unload {nativeId}");

        var loadingReplayHider = false;
        var attempts = 0;
        _hiderSwitchTimer = AddTimer(0.05f, () =>
        {
            if (++attempts > 100)
            {
                CancelHiderSwitch();
                Server.PrintToConsole(loadingReplayHider
                    ? "[DTR ERR] dtr-hider did not finish loading or taking control of bots; playback was not started."
                    : "[DTR ERR] BotHider did not finish unloading; playback was not started.");
                return;
            }
            if (BotControllerNative.FindMetamodPlugin("BotHider") != 0 ||
                OriginalHiderManaged()?.State == PluginState.Loaded) return;
            if (!loadingReplayHider)
            {
                loadingReplayHider = true;
                Server.ExecuteCommand($"meta load {ReplayHiderPath}");
                return;
            }
            _botHiderBridge.Refresh();
            if (BotControllerNative.FindMetamodPlugin("dtr-hider") == 0 ||
                _botHiderBridge.GetProviderInfo()?.Connected != true ||
                Utilities.GetPlayers().Any(p => p.IsBot && !p.IsHLTV && !_botHiderBridge.IsManagedBot(p.Slot))) return;
            CancelHiderSwitch();
            Server.PrintToConsole("[DTR] dtr-hider has taken control and will remain active for subsequent rounds on this map.");
            Server.ExecuteCommand(pendingCommand);
        }, TimerFlags.REPEAT);
        return false;
    }

    private void CancelHiderSwitch()
    {
        _hiderSwitchTimer?.Kill();
        _hiderSwitchTimer = null;
    }

    private void RestoreHiderAfterMap()
    {
        if (!_hiderUsedThisMap) return;
        _hiderUsedThisMap = false;
        // Native DH retires its hooks in OnLevelShutdown. Do not start BH
        // beside it if that cleanup failed.
        _botHiderBridge.Refresh();
        if (_botHiderBridge.GetProviderInfo()?.Connected == true)
        {
            Server.PrintToConsole("[DTR ERR] dtr-hider did not finish map-change cleanup; BotHider cannot be restored.");
            return;
        }
        var nativeId = BotControllerNative.FindMetamodPlugin("dtr-hider");
        if (nativeId != 0) Server.ExecuteCommand($"meta unload {nativeId}");
        if (_restoreOriginalHider && BotControllerNative.FindMetamodPlugin("BotHider") == 0)
            Server.ExecuteCommand($"meta load {OriginalHiderPath}");
        if (_restoreOriginalHiderManaged is { } managed)
        {
            managed.Load(hotReload: false);
            managed.Plugin.OnAllPluginsLoaded(hotReload: false);
        }
        _restoreOriginalHider = false;
        _restoreOriginalHiderManaged = null;
    }
}
