/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using System.Globalization;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private static TickPlayerSnapshot BuildTickPlayerSnapshot()
    {
        var controllers = FindPlayerControllers();
        return new TickPlayerSnapshot(controllers, FindTeamPlayers(controllers));
    }

    private List<CCSPlayerController> FindReplayTargets()
    {
        var players = FindTeamPlayers();
        var targets = players
            .Where(IsReplayTargetBot)
            .OrderBy(player => player.IsBot ? 0 : 1)
            .ThenBy(player => player.Slot)
            .ToList();
        return targets;
    }

    private bool IsReplayTargetBot(CCSPlayerController player)
    {
        return IsReplayTargetBot(player, null);
    }

    private bool IsReplayTargetBot(
        CCSPlayerController player,
        IReadOnlyList<CCSPlayerController>? playerControllers)
    {
        if (!IsReplayControllerSafe(player) || IsReplayPawnTakenByController(player, playerControllers))
            return false;
        return player.IsBot || _botHiderBridge.IsManagedBot(player.Slot);
    }

    private bool IsReplaySlotStillSafe(
        int slot,
        IReadOnlyList<CCSPlayerController>? playerControllers = null)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        return player is { IsValid: true } && IsReplayTargetBot(player, playerControllers);
    }

    private bool IsReplaySlotStillSafe(int slot, TickPlayerSnapshot playerSnapshot)
    {
        return playerSnapshot.TryGetSlot(slot, out var player) &&
               player is { IsValid: true } &&
               IsReplayTargetBot(player, playerSnapshot.Controllers);
    }

    private static bool IsReplayControllerSafe(CCSPlayerController player)
    {
        return TryGetControllingBotState(player, out var controllingBot) && !controllingBot;
    }

    private static bool TryGetControllingBotState(CCSPlayerController player, out bool controllingBot)
    {
        controllingBot = false;
        if (player is not { IsValid: true })
            return false;

        try
        {
            controllingBot = player.ControllingBot;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReplayPawnTakenByController(
        CCSPlayerController replayTarget,
        IReadOnlyList<CCSPlayerController>? playerControllers = null)
    {
        if (replayTarget.PlayerPawn is not { IsValid: true, Value.IsValid: true } replayPawn)
            return true;

        var replayPawnIndex = replayPawn.Value.Index;
        var controllers = playerControllers ?? FindPlayerControllers();
        foreach (var controller in controllers)
        {
            if (controller is not { IsValid: true } || controller.Slot == replayTarget.Slot)
                continue;
            if (!TryGetControllingBotState(controller, out var controllingBot) || !controllingBot)
                continue;

            if (controller.PlayerPawn is { IsValid: true, Value.IsValid: true } controlledPawn &&
                controlledPawn.Value.Index == replayPawnIndex)
                return true;

            if (controller.OriginalControllerOfCurrentPawn is { IsValid: true, Value.IsValid: true } original &&
                original.Value.Slot == replayTarget.Slot)
                return true;
        }

        return false;
    }

    private static List<CCSPlayerController> FindPlayerControllers()
    {
        return Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(player => player.IsValid)
            .ToList();
    }

    private static List<CCSPlayerController> FindTeamPlayers(
        IReadOnlyList<CCSPlayerController>? playerControllers = null)
    {
        return (playerControllers ?? FindPlayerControllers())
            .Where(player => player.IsValid &&
                             (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist) &&
                             player.PlayerPawn is { IsValid: true, Value.IsValid: true })
            .OrderBy(player => player.Team)
            .ThenBy(player => player.Slot)
            .ToList();
    }
}
