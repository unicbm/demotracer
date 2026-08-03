/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using DemoTracerApi;
using DemoTracerBotHiderApi;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private static bool TryGiveNamedItem(CCSPlayerController player, string itemName)
    {
        if (player is not { IsValid: true, PawnIsAlive: true })
            return false;

        try
        {
            return player.GiveNamedItem(itemName) != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: failed to give slot={player.Slot} item={itemName}: {ex.Message}");
            return false;
        }
    }

}
