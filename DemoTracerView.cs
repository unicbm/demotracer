/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using DemoTracerBotHiderApi;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private enum ViewmodelContinuityMode
    {
        Release,
        Round
    }

    private readonly Dictionary<int, bool> _replayLeftHandDesiredLatches = new();
    private readonly HashSet<int> _retainedReplayViewmodelSlots = new();
    private ViewmodelContinuityMode _viewmodelContinuityMode = ViewmodelContinuityMode.Round;

    private static ReplayView NormalizeReplayView(ReplayView? view)
    {
        return new ReplayView
        {
            CrosshairCode = NormalizeCrosshairCode(view?.CrosshairCode),
            Viewmodel = NormalizeReplayViewmodel(view?.Viewmodel)
        };
    }

    private static string? NormalizeCrosshairCode(string? code)
    {
        if (!DemoTracerBotHiderContract.TryNormalizeCrosshairCode(code, out var normalized) ||
            string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized;
    }

    private static bool HasCrosshairEvidence(ReplayView view)
        => !string.IsNullOrWhiteSpace(view.CrosshairCode);

    private static ReplayViewmodel? NormalizeReplayViewmodel(ReplayViewmodel? viewmodel)
    {
        if (viewmodel == null)
            return null;

        var normalized = new ReplayViewmodel
        {
            LeftHanded = viewmodel.LeftHanded,
            Fov = NormalizeViewmodelFloat(viewmodel.Fov, 0.0f, 120.0f),
            OffsetX = NormalizeViewmodelFloat(viewmodel.OffsetX, -64.0f, 64.0f),
            OffsetY = NormalizeViewmodelFloat(viewmodel.OffsetY, -64.0f, 64.0f),
            OffsetZ = NormalizeViewmodelFloat(viewmodel.OffsetZ, -64.0f, 64.0f)
        };

        return HasViewmodelEvidence(normalized) ? normalized : null;
    }

    private static float? NormalizeViewmodelFloat(float? value, float min, float max)
    {
        if (!value.HasValue || !float.IsFinite(value.Value))
            return null;

        return value.Value >= min && value.Value <= max ? value.Value : null;
    }

    private static bool HasViewmodelEvidence(ReplayView view)
        => HasViewmodelEvidence(view.Viewmodel);

    private static bool HasViewmodelEvidence(ReplayViewmodel? viewmodel)
        => viewmodel != null &&
           (viewmodel.LeftHanded.HasValue ||
            viewmodel.Fov.HasValue ||
            viewmodel.OffsetX.HasValue ||
            viewmodel.OffsetY.HasValue ||
            viewmodel.OffsetZ.HasValue);

    private void ResetCrosshairAlignState(bool resetCounters = false)
    {
        if (_session.LoadedSlots.Count == 0 &&
            _retainedBotHiderPresentation.Count == 0)
            ReleaseBotHiderPresentationLease("crosshair_reset");
        else
            _ = SyncBotHiderPresentationLease(announce: false);
    }

    private void ResetViewmodelAlignState(bool resetCounters = false)
    {
        RestoreAllReplayBotViewmodels();
    }

    private string FormatCrosshairStatusCounts()
    {
        var provider = _botHiderBridge.GetProviderInfo();
        return $"crosshair_evidence={CountLoadedCrosshairEvidence()} crosshair_server_overrides={CountActiveBotHiderCrosshairOverrides()} presentation_retained={_retainedBotHiderPresentation.Count} crosshair_lease={FormatOnOff(!string.IsNullOrWhiteSpace(_botHiderPresentationLeaseToken))} bothider={(provider is { Connected: true, Draining: false } ? "ready" : "unavailable")}";
    }

    private string FormatViewmodelStatusCounts()
        => $"viewmodel_evidence={CountLoadedViewmodelEvidence()} viewmodel_bots={_session.ReplayAppliedViewmodels.Count} viewmodel_failed={_session.ReplayFailedViewmodelSlots.Count} viewmodel_retained={_retainedReplayViewmodelSlots.Count} left_hand_latches={_replayLeftHandDesiredLatches.Count}";

    private int CountLoadedCrosshairEvidence()
        => _session.LoadedReplays.Values.Count(replay => HasCrosshairEvidence(replay.View));

    private int CountLoadedViewmodelEvidence()
        => _session.LoadedReplays.Values.Count(replay => HasViewmodelEvidence(replay.View));

    private bool RefreshReplayCrosshairPresentation()
    {
        return SyncBotHiderPresentationLease(announce: false);
    }

    private void ClearReplayCrosshairPresentationEntry(int slot)
    {
        _ = SyncBotHiderPresentationLease(announce: false);
    }

    private void ClearReplayCrosshairPresentation()
    {
        if (_session.LoadedSlots.Count == 0 && _retainedBotHiderPresentation.Count == 0)
            ReleaseBotHiderPresentationLease("crosshair_clear_all");
        else
            EnsureBotHiderPresentationLease();
    }

    private void UpdateReplayBotViewmodels(TickPlayerSnapshot playerSnapshot)
    {
        if (_session.LoadedSlots.Count == 0)
        {
            RestoreAllReplayBotViewmodels();
            return;
        }
        if (!_leftHandDesiredEnabled && _replayLeftHandDesiredLatches.Count > 0)
            ClearReplayLeftHandDesiredLatches();

        ulong activeReplaySlotMask = 0;
        foreach (var slot in _session.ReplaySlots.PlayingSlots)
        {
            if (slot is < 0 or >= MaxPlayerSlots ||
                !_session.LoadedReplays.TryGetValue(slot, out var replay) ||
                !HasViewmodelEvidence(replay.View))
            {
                ClearReplayLeftHandDesiredLatch(slot);
                continue;
            }

            if (!playerSnapshot.TryGetSlot(slot, out var replayBot) ||
                replayBot is not { IsValid: true, PawnIsAlive: true } ||
                !IsReplayTargetBot(replayBot, playerSnapshot.Controllers))
            {
                ClearReplayLeftHandDesiredLatch(slot);
                continue;
            }

            activeReplaySlotMask |= 1UL << slot;
            ApplyReplayBotViewmodel(replayBot, replay.View.Viewmodel!);
            ApplyReplayLeftHandDesiredLatch(slot, replay.View.Viewmodel!.LeftHanded);
        }

        for (var slot = 0; slot < MaxPlayerSlots; slot++)
        {
            if ((activeReplaySlotMask & (1UL << slot)) == 0 &&
                IsReplayViewmodelSlotTracked(slot) &&
                !_retainedReplayViewmodelSlots.Contains(slot))
            {
                RestoreReplayBotViewmodel(slot);
            }
        }
    }

    private bool HasTrackedReplayViewmodelState()
        => _session.ReplayOriginalViewmodels.Count > 0 ||
           _session.ReplayAppliedViewmodels.Count > 0 ||
           _session.ReplayFailedViewmodelSlots.Count > 0 ||
           _retainedReplayViewmodelSlots.Count > 0 ||
           _replayLeftHandDesiredLatches.Count > 0;

    private bool IsReplayViewmodelSlotTracked(int slot)
        => _session.ReplayOriginalViewmodels.ContainsKey(slot) ||
           _session.ReplayAppliedViewmodels.ContainsKey(slot) ||
           _session.ReplayFailedViewmodelSlots.Contains(slot) ||
           _retainedReplayViewmodelSlots.Contains(slot) ||
           _replayLeftHandDesiredLatches.ContainsKey(slot);

    private bool RetainReplayBotViewmodelForRound(int slot)
    {
        if (_viewmodelContinuityMode != ViewmodelContinuityMode.Round ||
            !IsReplayViewmodelSlotTracked(slot))
        {
            return false;
        }

        var bot = Utilities.GetPlayerFromSlot(slot);
        if (bot is not { IsValid: true, PawnIsAlive: true } || !IsReplayTargetBot(bot))
        {
            return false;
        }

        _retainedReplayViewmodelSlots.Add(slot);
        return true;
    }

    private void RestoreNonRetainedReplayBotViewmodels()
    {
        if (!HasTrackedReplayViewmodelState())
            return;

        for (var slot = 0; slot < MaxPlayerSlots; slot++)
        {
            if (IsReplayViewmodelSlotTracked(slot) && !_retainedReplayViewmodelSlots.Contains(slot))
                RestoreReplayBotViewmodel(slot);
        }
    }

    private void RestoreRetainedReplayBotViewmodels()
    {
        foreach (var slot in _retainedReplayViewmodelSlots.ToArray())
            RestoreReplayBotViewmodel(slot);
    }

    private void ApplyReplayBotViewmodel(CCSPlayerController bot, ReplayViewmodel viewmodel)
    {
        var slot = bot.Slot;
        if (slot is < 0 or >= MaxPlayerSlots)
            return;

        var pawn = bot.PlayerPawn.Value;
        if (pawn is not { IsValid: true })
            return;

        if (!_session.ReplayOriginalViewmodels.ContainsKey(slot))
            _session.ReplayOriginalViewmodels[slot] = ReadCurrentViewmodel(pawn);

        if (_session.ReplayAppliedViewmodels.TryGetValue(slot, out var current) &&
            ViewmodelsEquivalent(current, viewmodel))
        {
            return;
        }

        if (_session.ReplayFailedViewmodelSlots.Contains(slot))
            return;

        if (TryApplyViewmodelToPawn(pawn, viewmodel, $"slot={slot} replay_bot"))
        {
            _session.ReplayAppliedViewmodels[slot] = CopyViewmodel(viewmodel);
            _session.ReplayFailedViewmodelSlots.Remove(slot);
        }
        else
        {
            _session.ReplayFailedViewmodelSlots.Add(slot);
        }
    }

    private void RestoreAllReplayBotViewmodels()
    {
        if (!HasTrackedReplayViewmodelState())
            return;

        for (var slot = 0; slot < MaxPlayerSlots; slot++)
        {
            if (IsReplayViewmodelSlotTracked(slot))
                RestoreReplayBotViewmodel(slot, clearLeftHandDesiredLatch: false);
        }
        _session.ReplayOriginalViewmodels.Clear();
        _session.ReplayAppliedViewmodels.Clear();
        _session.ReplayFailedViewmodelSlots.Clear();
        _retainedReplayViewmodelSlots.Clear();
        ClearReplayLeftHandDesiredLatches();
    }

    private void RestoreReplayBotViewmodel(int slot, bool clearLeftHandDesiredLatch = true)
    {
        _retainedReplayViewmodelSlots.Remove(slot);
        _session.ReplayAppliedViewmodels.Remove(slot);
        _session.ReplayFailedViewmodelSlots.Remove(slot);
        if (clearLeftHandDesiredLatch)
            ClearReplayLeftHandDesiredLatch(slot);
        if (!_session.ReplayOriginalViewmodels.TryGetValue(slot, out var original))
            return;

        _session.ReplayOriginalViewmodels.Remove(slot);

        var bot = Utilities.GetPlayerFromSlot(slot);
        var pawn = bot?.PlayerPawn.Value;
        if (bot is not { IsValid: true } || pawn is not { IsValid: true } || !IsReplayTargetBot(bot))
            return;

        _ = TryApplyViewmodelToPawn(pawn, original, $"slot={slot} restore");
    }

    private static bool TryParseViewmodelContinuityMode(string value, out ViewmodelContinuityMode mode)
    {
        mode = value.Trim().ToLowerInvariant() switch
        {
            "release" or "off" or "none" or "immediate" => ViewmodelContinuityMode.Release,
            "round" or "retain" or "retain_round" or "retain-round" => ViewmodelContinuityMode.Round,
            _ => ViewmodelContinuityMode.Release
        };

        return value.Trim().ToLowerInvariant() is
            "release" or "off" or "none" or "immediate" or
            "round" or "retain" or "retain_round" or "retain-round";
    }

    private string ViewmodelContinuityModeName()
        => _viewmodelContinuityMode == ViewmodelContinuityMode.Round ? "round" : "release";

    private static ReplayViewmodel ReadCurrentViewmodel(CCSPlayerPawn pawn)
    {
        return new ReplayViewmodel
        {
            LeftHanded = pawn.LeftHanded,
            Fov = pawn.ViewmodelFOV,
            OffsetX = pawn.ViewmodelOffsetX,
            OffsetY = pawn.ViewmodelOffsetY,
            OffsetZ = pawn.ViewmodelOffsetZ
        };
    }

    private static ReplayViewmodel CopyViewmodel(ReplayViewmodel viewmodel)
    {
        return new ReplayViewmodel
        {
            LeftHanded = viewmodel.LeftHanded,
            Fov = viewmodel.Fov,
            OffsetX = viewmodel.OffsetX,
            OffsetY = viewmodel.OffsetY,
            OffsetZ = viewmodel.OffsetZ
        };
    }

    private static bool ViewmodelsEquivalent(ReplayViewmodel left, ReplayViewmodel right)
    {
        return left.LeftHanded == right.LeftHanded &&
               NullableFloatBitsEqual(left.Fov, right.Fov) &&
               NullableFloatBitsEqual(left.OffsetX, right.OffsetX) &&
               NullableFloatBitsEqual(left.OffsetY, right.OffsetY) &&
               NullableFloatBitsEqual(left.OffsetZ, right.OffsetZ);
    }

    private static bool NullableFloatBitsEqual(float? left, float? right)
    {
        if (!left.HasValue || !right.HasValue)
            return left.HasValue == right.HasValue;
        if (left.Value == 0.0f && right.Value == 0.0f)
            return true;
        return BitConverter.SingleToInt32Bits(left.Value) == BitConverter.SingleToInt32Bits(right.Value);
    }

    private static bool TryApplyViewmodelToPawn(CCSPlayerPawn pawn, ReplayViewmodel viewmodel, string reason)
    {
        try
        {
            if (viewmodel.LeftHanded.HasValue)
            {
                pawn.LeftHanded = viewmodel.LeftHanded.Value;
                TrySetPawnStateChanged(pawn, "m_bLeftHanded");
            }
            if (viewmodel.Fov.HasValue)
            {
                pawn.ViewmodelFOV = viewmodel.Fov.Value;
                TrySetPawnStateChanged(pawn, "m_flViewmodelFOV");
            }
            if (viewmodel.OffsetX.HasValue)
            {
                pawn.ViewmodelOffsetX = viewmodel.OffsetX.Value;
                TrySetPawnStateChanged(pawn, "m_flViewmodelOffsetX");
            }
            if (viewmodel.OffsetY.HasValue)
            {
                pawn.ViewmodelOffsetY = viewmodel.OffsetY.Value;
                TrySetPawnStateChanged(pawn, "m_flViewmodelOffsetY");
            }
            if (viewmodel.OffsetZ.HasValue)
            {
                pawn.ViewmodelOffsetZ = viewmodel.OffsetZ.Value;
                TrySetPawnStateChanged(pawn, "m_flViewmodelOffsetZ");
            }

            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: viewmodel bot apply failed reason={reason}: {ex.Message}");
            return false;
        }
    }

    private void ApplyReplayLeftHandDesiredLatch(int slot, bool? leftHanded)
    {
        if (!_leftHandDesiredEnabled || !leftHanded.HasValue)
        {
            ClearReplayLeftHandDesiredLatch(slot);
            return;
        }

        if (_replayLeftHandDesiredLatches.TryGetValue(slot, out var current) &&
            current == leftHanded.Value)
        {
            return;
        }

        var rc = BotControllerNative.SetLeftHandDesiredLatch(slot, enabled: true, leftHandDesired: leftHanded.Value);
        if (rc == 0)
            _replayLeftHandDesiredLatches[slot] = leftHanded.Value;
    }

    private void ClearReplayLeftHandDesiredLatch(int slot)
    {
        if (!_replayLeftHandDesiredLatches.Remove(slot))
            return;
        _ = BotControllerNative.SetLeftHandDesiredLatch(slot, enabled: false, leftHandDesired: false);
    }

    private void ClearReplayLeftHandDesiredLatches(bool forceNative = false)
    {
        if (!forceNative && _replayLeftHandDesiredLatches.Count == 0)
            return;

        _replayLeftHandDesiredLatches.Clear();
        _ = BotControllerNative.ClearAllLeftHandDesiredLatches();
    }

    private static void TrySetPawnStateChanged(CCSPlayerPawn pawn, string field)
    {
        try
        {
            if (!Schema.IsSchemaFieldNetworked("CCSPlayerPawn", field))
                return;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", field);
        }
        catch
        {
        }
    }

}
