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

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private readonly BotBuySuspension _botBuySuspension = new();

    private bool BotBuyMustRemainSuspended()
        => _session.ReplaySlots.OwnedCount > 0 || _session.Plan.Armed ||
           _session.Plan.SequenceActive || HasPlayoffSchedulingState();

    private bool EnsureReplayPluginsForCommand(CommandInfo command)
        => EnsureHiderForCommand(command) && SuspendBotBuy(command.ReplyToCommand);

    private bool SuspendBotBuy(Action<string> reply)
    {
        try
        {
            var plugins = (IPluginContextQueryHandler)typeof(Application)
                .GetField("_pluginContextQueryHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Application.Instance)!;
            if (_botBuySuspension.Suspend(plugins.FindPluginByModuleName("BotBuyPatch")))
                reply("[DTR] BotBuy unloaded for replay control.");
            return true;
        }
        catch (Exception ex)
        {
            reply($"[DTR ERR] Could not unload BotBuy; replay control refused: {ex.Message}");
            return false;
        }
    }

    private void RestoreBotBuyIfIdle(bool force = false)
    {
        try
        {
            if (_botBuySuspension.Restore(force || !BotBuyMustRemainSuspended(), retry: force))
                Server.PrintToConsole("[DTR] BotBuy restored after replay control.");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[DTR ERR] Could not restore BotBuy; load BotBuy manually or retry after map change: {ex.Message}");
        }
    }
}

// Retain only the instance DTR unloaded; never load a plugin that was already disabled.
internal sealed class BotBuySuspension
{
    private IPluginContext? _restore;
    private bool _restoreFailed;

    public bool Suspend(IPluginContext? plugin)
    {
        if (plugin?.State != PluginState.Loaded) return false;
        if (_restore != null && !ReferenceEquals(_restore, plugin))
            throw new InvalidOperationException("BotBuy was replaced while suspended.");
        _restore = plugin;
        _restoreFailed = false;
        plugin.Unload(hotReload: false);
        if (plugin.State != PluginState.Unloaded)
            throw new InvalidOperationException("BotBuy did not finish unloading.");
        return true;
    }

    public bool Restore(bool idle, bool retry = false)
    {
        if (!idle || _restore == null || (_restoreFailed && !retry)) return false;
        try
        {
            if (_restore.State != PluginState.Loaded)
            {
                _restore.Load(hotReload: false);
                if (_restore.State != PluginState.Loaded)
                    throw new InvalidOperationException("BotBuy did not finish loading.");
                _restore.Plugin.OnAllPluginsLoaded(hotReload: false);
            }
            _restore = null;
            _restoreFailed = false;
            return true;
        }
        catch
        {
            _restoreFailed = true;
            throw;
        }
    }
}
