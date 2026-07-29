// funchook for CS2 movement functions (ProcessMovement / PhysicsSimulate / FinishMove / PlayerRunCommand)

#pragma once

#include <cstdint>
#include <string>

#include <nlohmann/json.hpp>
#include "sig_scan.h"

namespace BotController
{
    namespace InputInjector
    {
        // Max bots we track per-slot state for.
        static constexpr int kMaxSlots = 64;

        // Resolve sigs and install the movement hooks.
        bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                     char *errorOut, size_t errorOutLen);

        // Disable + remove the hooks.
        void Remove();

        // Optional schema offset for CCSPlayerController::m_bControllingBot.
        // When available, replay stops immediately if a real player takes over a bot.
        bool SetControllerControllingBotOffset(int offset);

        // Last observed CCSPlayerController::m_bControllingBot state for this
        // slot. Handoff actions must fail closed while a human owns the pawn.
        bool IsSlotControllingBot(int slot);

        // Short-lived, per-slot movement input lease. This is a low-level
        // usercmd/movedata primitive; policy lives in the caller. Only
        // movement button bits (WASD/duck/jump/walk) are applied.
        //
        // kUsercmdMovementIntentPreserveMoveAxes applies buttons without
        // replacing the engine-authored forward/left/up axes. This is useful
        // for final-boundary modifiers such as clearing IN_SPEED while native
        // navigation retains ownership of the route and movement magnitude.
        static constexpr int kUsercmdMovementIntentPreserveMoveAxes = 1 << 0;
        bool SetUsercmdMovementIntent(int slot, uint64_t buttonsSet, uint64_t buttonsClear,
                                      float analogForward, float analogLeft,
                                      int durationMs, int flags);
        bool ClearUsercmdMovementIntent(int slot);
        void ClearAllUsercmdMovementIntents();

        // Persistent per-slot hand-state latch. This is intentionally separate
        // from movement intent: left hand in CS2 behaves like a held usercmd
        // desire, so callers set policy once and the native command hook keeps
        // it continuous without a C# timer gap.
        bool SetLeftHandDesiredLatch(int slot, bool enabled, bool leftHandDesired);
        bool ClearLeftHandDesiredLatch(int slot);
        void ClearAllLeftHandDesiredLatches();
        bool GetLeftHandDesiredLatch(int slot, bool *enabled, bool *leftHandDesired);

        const char *Status();

        // Whether replay should inject subtick pitch_delta/yaw_delta into
        // usercmd. Disabled by default because offline demo pawn snapshots do
        // not prove they are aligned to CBaseUserCmdPB base viewangles.
        void SetReplaySubtickViewDeltas(bool enabled);
        bool ReplaySubtickViewDeltas();

        // Last CCSPlayer_MovementServices* seen for this player slot.
        void *LiveMovementServices(int slot);

        // Replay slots can register the pawn pointer known by CounterStrikeSharp.
        // This is a fallback for builds where CPlayerPawnComponent's helper
        // pointer is missing or stale inside movement services.
        bool SetReplayPawn(int slot, void *pawn);
        void ClearReplayPawn(int slot);
        void *ResolveReplayPawn(int slot, void *services);

        // Resolved address of the hooked function.
        void *ProcessUsercmdAddress();

        // Diagnostics
        uint64_t HookCallCount();
        int LastResolvedSlot();
    }
}
