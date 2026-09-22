// CS2 movement hooks
// SetupMove (single replay kinematic input boundary)
// ProcessMovement (engine simulation + recording)
// FinishMove (engine output + final replay view)
// PlayerRunCommand(subtick record + re-inject)

#include "playercommand.h"

#include "InputInjector.h"
#include "ButtonState.h"
#include "LeftHandDesiredLatch.h"
#include "UsercmdRequests.h"
#include "BotController.h"
#include "BotControllerState.h"
#include "ccsbot_slot.h"
#include "sig_scan.h"
#include "schema_resolver.h"
#include "MotionRecorder.h"
#include "ReplayPawnEquipment.h"
#include "ReplaySubtickLayout.h"
#include "projectile_birth_align.h"
#include "version_targets.h"
#include "hook.h"
#include "platform.h"
#include <entity2/entityinstance.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cmath>
#include <cstdio>
#include <vector>

namespace tg = BotController::targets;

using ProcessMovement_t = void(BC_FASTCALL *)(void *services, void *moveData);
using FinishMove_t = void(BC_FASTCALL *)(void *services, void *cmd, void *moveData);
using SetupMove_t = void(BC_FASTCALL *)(void *services, void *cmd, void *moveData);
using SetEntityVector_t = void(BC_FASTCALL *)(void *pawn, const float *value);
using PlayerRunCommand_t = void(BC_FASTCALL *)(void *services, void *cmd);
using PhysicsSimulate_t = void(BC_FASTCALL *)(void *controller);

namespace BotController
{
    namespace InputInjector
    {
        static ProcessMovement_t g_origProcessMovement = nullptr;
        static FinishMove_t g_origFinishMove = nullptr;
        static SetupMove_t g_origSetupMove = nullptr;
        static SetEntityVector_t g_setAbsOrigin = nullptr;
        static SetEntityVector_t g_setAbsVelocity = nullptr;
        using SetMoveType_t = void(BC_FASTCALL *)(void *, uint8_t, uint8_t);
        static SetMoveType_t g_setMoveType = nullptr;
        static int g_moveCollideOffset = -1, g_tickBaseOffset = -1;
        static std::array<std::atomic<void *>, kMaxSlots> g_slotControllers{};
        static PlayerRunCommand_t g_origPlayerRunCommand = nullptr;
        static PhysicsSimulate_t g_origPhysicsSimulate = nullptr;

        static void *g_addrProcessMovement = nullptr;
        static void *g_addrFinishMove = nullptr;
        static void *g_addrSetupMove = nullptr;
        static void *g_addrPlayerRunCommand = nullptr;
        static void *g_addrPhysicsSimulate = nullptr;

        static Hook<ProcessMovement_t> g_hookProcessMovement;
        static Hook<FinishMove_t> g_hookFinishMove;
        static Hook<SetupMove_t> g_hookSetupMove;
        static Hook<PlayerRunCommand_t> g_hookPlayerRunCommand;
        static Hook<PhysicsSimulate_t> g_hookPhysicsSimulate;
        static bool g_installed = false;
        // True once PhysicsSimulate is hooked
        static bool g_physicsActive = false;
        static bool g_finishMoveActive = false;
        static bool g_setupMoveActive = false;
        // True once PlayerRunCommand is hooked
        static bool g_subtickActive = false;
        static UsercmdRequests g_requests;
        static std::string g_status = "not_attempted";

        // slot -> live CCSPlayer_MovementServices*
        static std::array<std::atomic<void *>, kMaxSlots> g_slotServices{};
        static std::array<std::atomic<void *>, kMaxSlots> g_slotPawns{};
        static std::array<std::atomic<bool>, kMaxSlots> g_slotControllingBot{};
        static std::atomic<int> g_controllerControllingBotOffset{-1};

        static std::atomic<uint64_t> g_hookCalls{0};
        static std::atomic<int> g_lastSlot{-1};
        static std::atomic<bool> g_replaySubtickViewDeltas{false};

        constexpr uint64_t kInAttack = 1ULL << 0;
        constexpr uint64_t kInJump = 1ULL << 1;
        constexpr uint64_t kInDuck = 1ULL << 2;
        constexpr uint64_t kInForward = 1ULL << 3;
        constexpr uint64_t kInBack = 1ULL << 4;
        constexpr uint64_t kInMoveLeft = 1ULL << 9;
        constexpr uint64_t kInMoveRight = 1ULL << 10;
        constexpr uint64_t kInSpeed = 1ULL << 16;
        constexpr uint64_t kMovementIntentButtons =
            kInAttack | kInJump | kInDuck | kInForward | kInBack | kInMoveLeft |
            kInMoveRight | kInSpeed;
        constexpr int kMovementIntentFlags =
            kUsercmdMovementIntentPreserveMoveAxes;
        constexpr float kCommandMoveSpeed = 450.0f;
        constexpr float kAnalogDeadzone = 0.05f;
        constexpr int kMaxIntentDurationMs = 60000;

        struct UsercmdMovementIntentFrame
        {
            uint64_t buttonsSet;
            uint64_t buttonsClear;
            float analogForward;
            float analogLeft;
            int flags;
        };

        static std::array<std::atomic<uint64_t>, kMaxSlots> g_intentButtonsSet{};
        static std::array<std::atomic<uint64_t>, kMaxSlots> g_intentButtonsClear{};
        static std::array<std::atomic<float>, kMaxSlots> g_intentAnalogForward{};
        static std::array<std::atomic<float>, kMaxSlots> g_intentAnalogLeft{};
        static std::array<std::atomic<int>, kMaxSlots> g_intentFlags{};
        static std::array<std::atomic<int64_t>, kMaxSlots> g_intentExpireMs{};
        static std::array<LeftHandDesiredLatch, kMaxSlots> g_leftHandLatches{};

        static int64_t NowMs()
        {
            using namespace std::chrono;
            return duration_cast<milliseconds>(
                       steady_clock::now().time_since_epoch())
                .count();
        }

        static float ClampAxis(float value)
        {
            if (!std::isfinite(value))
                return 0.0f;
            return std::clamp(value, -1.0f, 1.0f);
        }

        static bool CanUsePublicControl(int slot)
        {
            return g_subtickActive && slot >= 0 && slot < kMaxSlots &&
                BotControllerHooks::BotForSlot(slot) && !IsSlotControllingBot(slot) &&
                !MotionRecorder::IsReplaying(slot) && !BotControllerState::GetAll(slot);
        }

        int64_t InjectUsercmd(int slot, uint64_t buttons, int durationMs)
        {
            return CanUsePublicControl(slot)
                ? g_requests.Add(slot, UsercmdRequests::Kind::Injection, buttons, durationMs, NowMs()) : -1;
        }
        bool CancelUsercmdInjection(int slot, int64_t id)
        { return g_requests.Cancel(slot, UsercmdRequests::Kind::Injection, id); }
        int64_t StartUsercmdMovement(int slot, float forward, float left)
        {
            return CanUsePublicControl(slot)
                ? g_requests.Add(slot, UsercmdRequests::Kind::Movement, 0, 0, NowMs(), forward, left) : -1;
        }
        bool UpdateUsercmdMovement(int slot, int64_t id, float forward, float left)
        { return CanUsePublicControl(slot) && g_requests.UpdateMovement(slot, id, forward, left); }
        bool CancelUsercmdMovement(int slot, int64_t id)
        { return g_requests.Cancel(slot, UsercmdRequests::Kind::Movement, id); }
        bool SuppressUsercmd(int slot, uint64_t buttons, int durationMs)
        {
            return durationMs > 0 && CanUsePublicControl(slot) &&
                g_requests.Add(slot, UsercmdRequests::Kind::Suppression, buttons, durationMs, NowMs()) > 0;
        }
        int64_t StartUsercmdSuppression(int slot, uint64_t buttons)
        {
            return CanUsePublicControl(slot)
                ? g_requests.Add(slot, UsercmdRequests::Kind::Suppression, buttons, 0, NowMs()) : -1;
        }
        bool CancelUsercmdSuppression(int slot, int64_t id)
        { return g_requests.Cancel(slot, UsercmdRequests::Kind::Suppression, id); }
        void ClearUsercmdInjections(int slot) { g_requests.Clear(slot); }

        static void ApplyPublicControl(int slot, PlayerCommand *pc, CBaseUserCmdPB *base)
        {
            const auto frame = g_requests.Advance(slot, NowMs(), {
                pc->buttonstates.m_pButtonStates[0], pc->buttonstates.m_pButtonStates[1],
                pc->buttonstates.m_pButtonStates[2]});
            auto *buttons = base->mutable_buttons_pb();
            buttons->set_buttonstate1(frame.buttons.state1);
            buttons->set_buttonstate2(frame.buttons.state2);
            buttons->set_buttonstate3(frame.buttons.state3);
            pc->buttonstates.m_pButtonStates[0] = frame.buttons.state1;
            pc->buttonstates.m_pButtonStates[1] = frame.buttons.state2;
            pc->buttonstates.m_pButtonStates[2] = frame.buttons.state3;
            if (frame.movement)
            {
                base->set_forwardmove(frame.forward * kCommandMoveSpeed);
                base->set_leftmove(frame.left * kCommandMoveSpeed);
            }
            for (int i = 0; i < base->subtick_moves_size(); ++i)
            {
                auto *step = base->mutable_subtick_moves(i);
                step->set_button(step->button() & ~frame.controlledMask);
                if (step->button() == 0) step->set_pressed(false);
                if (frame.movement)
                {
                    step->set_analog_forward_delta(0);
                    step->set_analog_left_delta(0);
                }
            }
        }

        static uint64_t ButtonsForAnalog(float analogForward, float analogLeft)
        {
            uint64_t buttons = 0;
            if (analogForward > kAnalogDeadzone)
                buttons |= kInForward;
            else if (analogForward < -kAnalogDeadzone)
                buttons |= kInBack;
            if (analogLeft > kAnalogDeadzone)
                buttons |= kInMoveLeft;
            else if (analogLeft < -kAnalogDeadzone)
                buttons |= kInMoveRight;
            return buttons;
        }

        bool IsSlotControllingBot(int slot)
        {
            return slot >= 0 && slot < kMaxSlots &&
                   g_slotControllingBot[slot].load(std::memory_order_acquire);
        }

        static bool ActiveUsercmdMovementIntent(int slot, UsercmdMovementIntentFrame &out)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return false;

            int64_t expiresAt = g_intentExpireMs[slot].load(std::memory_order_acquire);
            if (expiresAt <= 0)
                return false;
            if (expiresAt <= NowMs())
            {
                int64_t expected = expiresAt;
                g_intentExpireMs[slot].compare_exchange_strong(
                    expected, 0, std::memory_order_acq_rel);
                return false;
            }

            out.buttonsSet = g_intentButtonsSet[slot].load(std::memory_order_relaxed);
            out.buttonsClear = g_intentButtonsClear[slot].load(std::memory_order_relaxed);
            out.analogForward = g_intentAnalogForward[slot].load(std::memory_order_relaxed);
            out.analogLeft = g_intentAnalogLeft[slot].load(std::memory_order_relaxed);
            out.flags = g_intentFlags[slot].load(std::memory_order_relaxed);
            return true;
        }

        static uint64_t ApplyUsercmdMovementButtons(
            uint64_t buttons,
            const UsercmdMovementIntentFrame &intent)
        {
            uint64_t set = intent.buttonsSet |
                           ButtonsForAnalog(intent.analogForward, intent.analogLeft);
            uint64_t clear = intent.buttonsClear;
            if (std::fabs(intent.analogForward) > kAnalogDeadzone)
                clear |= kInForward | kInBack;
            if (std::fabs(intent.analogLeft) > kAnalogDeadzone)
                clear |= kInMoveLeft | kInMoveRight;
            return (buttons & ~clear) | set;
        }

        static void IntentToMoveAxes(
            const UsercmdMovementIntentFrame &intent,
            float &forwardMove,
            float &leftMove,
            float &sideMove)
        {
            forwardMove = intent.analogForward * kCommandMoveSpeed;
            leftMove = intent.analogLeft * kCommandMoveSpeed;

            if (std::fabs(intent.analogForward) <= kAnalogDeadzone)
            {
                if ((intent.buttonsSet & kInForward) != 0)
                    forwardMove += kCommandMoveSpeed;
                if ((intent.buttonsSet & kInBack) != 0)
                    forwardMove -= kCommandMoveSpeed;
            }
            if (std::fabs(intent.analogLeft) <= kAnalogDeadzone)
            {
                if ((intent.buttonsSet & kInMoveLeft) != 0)
                    leftMove += kCommandMoveSpeed;
                if ((intent.buttonsSet & kInMoveRight) != 0)
                    leftMove -= kCommandMoveSpeed;
            }

            // CMoveData and usercmd both use positive left (A).
            sideMove = leftMove;
        }

        static void ApplyUsercmdMovementIntentToMoveData(
            void *services,
            void *moveData,
            const UsercmdMovementIntentFrame &intent)
        {
            if ((intent.flags & kUsercmdMovementIntentPreserveMoveAxes) != 0 &&
                services && tg::kServices_Buttons > 0)
            {
                uint64_t buttons = 0;
                if (SafeRead(services, tg::kServices_Buttons, buttons))
                {
                    buttons = ApplyUsercmdMovementButtons(buttons, intent);
                    WriteField(services, tg::kServices_Buttons, buttons);
                }
            }

            if (!moveData)
                return;
            if ((intent.flags & kUsercmdMovementIntentPreserveMoveAxes) != 0)
                return;
            if (tg::kMove_ForwardMove <= 0 || tg::kMove_SideMove <= 0 ||
                tg::kMove_UpMove <= 0)
                return;

            float forwardMove = 0.0f;
            float leftMove = 0.0f;
            float sideMove = 0.0f;
            IntentToMoveAxes(intent, forwardMove, leftMove, sideMove);
            WriteField(moveData, tg::kMove_ForwardMove, forwardMove);
            WriteField(moveData, tg::kMove_SideMove, sideMove);
            WriteField(moveData, tg::kMove_UpMove, 0.0f);
        }

        static void ApplyUsercmdMovementIntentToCommand(
            void *services,
            PlayerCommand *pc,
            CBaseUserCmdPB *base,
            const UsercmdMovementIntentFrame &intent)
        {
            if (!pc || !base)
                return;

            uint64_t buttons0 = pc->buttonstates.m_pButtonStates[0];
            buttons0 = ApplyUsercmdMovementButtons(buttons0, intent);
            CInButtonStatePB *bp = base->mutable_buttons_pb();
            bp->set_buttonstate1(buttons0);
            pc->buttonstates.m_pButtonStates[0] = buttons0;

            // Keep the previous engine-held state until this command consumes
            // the edge; pre-writing it loses single-tick jump presses.
            uint64_t previous = 0;
            if (services && SafeRead(services, tg::kServices_Buttons, previous))
            {
                const auto mask = intent.buttonsSet | intent.buttonsClear;
                auto &planes = pc->buttonstates.m_pButtonStates;
                const auto edges = ButtonState::OverrideOwnedEdges(
                    {buttons0, planes[1], planes[2]}, previous, mask);
                planes[1] = edges.state2;
                planes[2] = edges.state3;
                bp->set_buttonstate2(planes[1]);
                bp->set_buttonstate3(planes[2]);
                for (int i = 0; i < base->subtick_moves_size(); ++i)
                {
                    auto *step = base->mutable_subtick_moves(i);
                    step->set_button(step->button() & ~mask);
                    if (step->button() == 0)
                        step->set_pressed(false);
                }
            }

            if ((intent.flags & kUsercmdMovementIntentPreserveMoveAxes) != 0)
                return;

            float forwardMove = 0.0f;
            float leftMove = 0.0f;
            float sideMove = 0.0f;
            IntentToMoveAxes(intent, forwardMove, leftMove, sideMove);
            base->set_forwardmove(forwardMove);
            base->set_leftmove(leftMove);
            base->set_upmove(0.0f);
        }

        void SetReplaySubtickViewDeltas(bool enabled)
        {
            g_replaySubtickViewDeltas.store(enabled, std::memory_order_relaxed);
        }

        bool ReplaySubtickViewDeltas()
        {
            return g_replaySubtickViewDeltas.load(std::memory_order_relaxed);
        }

        bool SetUsercmdMovementIntent(int slot, uint64_t buttonsSet, uint64_t buttonsClear,
                                      float analogForward, float analogLeft,
                                      int durationMs, int flags)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return false;
            if (durationMs <= 0)
                return ClearUsercmdMovementIntent(slot);

            const int clampedDuration = std::clamp(durationMs, 1, kMaxIntentDurationMs);
            const int64_t expiresAt = NowMs() + clampedDuration;
            g_intentExpireMs[slot].store(0, std::memory_order_release);
            g_intentButtonsSet[slot].store(
                buttonsSet & kMovementIntentButtons, std::memory_order_relaxed);
            g_intentButtonsClear[slot].store(
                buttonsClear & kMovementIntentButtons, std::memory_order_relaxed);
            g_intentAnalogForward[slot].store(ClampAxis(analogForward), std::memory_order_relaxed);
            g_intentAnalogLeft[slot].store(ClampAxis(analogLeft), std::memory_order_relaxed);
            g_intentFlags[slot].store(
                flags & kMovementIntentFlags, std::memory_order_relaxed);
            g_intentExpireMs[slot].store(expiresAt, std::memory_order_release);
            return true;
        }

        bool ClearUsercmdMovementIntent(int slot)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return false;
            ClearUsercmdInjections(slot);
            g_intentExpireMs[slot].store(0, std::memory_order_release);
            g_intentButtonsSet[slot].store(0, std::memory_order_relaxed);
            g_intentButtonsClear[slot].store(0, std::memory_order_relaxed);
            g_intentAnalogForward[slot].store(0.0f, std::memory_order_relaxed);
            g_intentAnalogLeft[slot].store(0.0f, std::memory_order_relaxed);
            g_intentFlags[slot].store(0, std::memory_order_relaxed);
            return true;
        }

        void ClearAllUsercmdMovementIntents()
        {
            for (int slot = 0; slot < kMaxSlots; ++slot)
                ClearUsercmdMovementIntent(slot);
        }

        static bool ReadHandLatchOwner(int slot, void *pawn, uint32_t &handle)
        {
            void *bot = BotControllerHooks::BotForSlot(slot);
            void *botPawn = nullptr;
            void *identity = nullptr;
            uint8_t lifeState = 1;
            return pawn && bot && !IsSlotControllingBot(slot) &&
                SafeRead(bot, tg::kBot_Pawn, botPawn) && botPawn == pawn &&
                ControllerSlotForPawn(pawn) == slot &&
                SafeRead(pawn, tg::kEnt_LifeState, lifeState) && lifeState == 0 &&
                SafeRead(pawn, tg::kEnt_Identity, identity) && identity &&
                SafeRead(identity, tg::kEntIdentity_EHandle, handle) &&
                handle != 0 && handle != 0xFFFFFFFFu && handle != 0xFFFFFFFEu;
        }

        bool SetLeftHandDesiredLatch(int slot, bool enabled, bool leftHandDesired)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return false;

            auto &latch = g_leftHandLatches[slot];
            latch.Clear();
            if (!enabled) return true;
            void *pawn = g_slotPawns[slot].load(std::memory_order_acquire);
            uint32_t handle = 0;
            if (!ReadHandLatchOwner(slot, pawn, handle)) return false;
            latch.Set(reinterpret_cast<uintptr_t>(pawn), handle, leftHandDesired);
            return true;
        }

        bool ClearLeftHandDesiredLatch(int slot)
        {
            return SetLeftHandDesiredLatch(slot, false, false);
        }

        void ClearAllLeftHandDesiredLatches()
        {
            for (int slot = 0; slot < kMaxSlots; ++slot)
                ClearLeftHandDesiredLatch(slot);
        }

        bool GetLeftHandDesiredLatch(int slot, bool *enabled, bool *leftHandDesired)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return false;

            bool desired = false;
            const bool active = g_leftHandLatches[slot].Get(desired);
            if (enabled) *enabled = active;
            if (leftHandDesired) *leftHandDesired = desired;
            return true;
        }

        void *LiveMovementServices(int slot)
        {
            return slot >= 0 && slot < kMaxSlots
                       ? g_slotServices[slot].load(std::memory_order_acquire)
                       : nullptr;
        }

        static bool ValidSlotIndex(int slot)
        {
            return slot >= 0 && slot < kMaxSlots;
        }

        bool SetReplayPawn(int slot, void *pawn)
        {
            if (!ValidSlotIndex(slot))
                return false;
            if (!pawn)
                return false;

            void *identity = nullptr;
            uint32_t handle = 0;
            if (!SafeRead(pawn, tg::kEnt_Identity, identity) || !identity ||
                !SafeRead(identity, tg::kEntIdentity_EHandle, handle) ||
                handle == 0u || handle == 0xFFFFFFFFu || handle == 0xFFFFFFFEu)
                return false;

            int ownerSlot = ControllerSlotForPawn(pawn);
            if (ownerSlot >= 0 && ownerSlot != slot)
                return false;

            void *previous = g_slotPawns[slot].load(std::memory_order_acquire);
            if (previous != pawn)
                ReplayPawnEquipment::Clear(slot);
            g_slotPawns[slot].store(pawn, std::memory_order_release);
            return true;
        }

        void ClearReplayPawn(int slot)
        {
            if (ValidSlotIndex(slot))
                g_slotPawns[slot].store(nullptr, std::memory_order_release);
        }

        static void *ServicesToPawnField(void *services)
        {
            if (!services)
                return nullptr;
            void *pawn = nullptr;
            return SafeRead(services, tg::kServices_Pawn, pawn) ? pawn : nullptr;
        }

        static bool PawnOwnsServices(void *pawn, void *services)
        {
            if (!pawn || !services)
                return false;
            void *liveServices = nullptr;
            return SafeRead(pawn, tg::kPawn_MovementServices, liveServices) &&
                   liveServices == services;
        }

        void *ResolveReplayPawn(int slot, void *services)
        {
            if (ValidSlotIndex(slot))
            {
                void *registered = g_slotPawns[slot].load(std::memory_order_acquire);
                if (PawnOwnsServices(registered, services))
                    return registered;
            }

            void *fieldPawn = ServicesToPawnField(services);
            return PawnOwnsServices(fieldPawn, services) ? fieldPawn : nullptr;
        }

        static int RegisteredSlotForServices(void *services)
        {
            if (!services)
                return -1;
            for (int slot = 0; slot < kMaxSlots; ++slot)
            {
                void *pawn = g_slotPawns[slot].load(std::memory_order_acquire);
                if (PawnOwnsServices(pawn, services))
                    return slot;
            }
            return -1;
        }

        // services -> player slot via pawn ptr at services+56, then m_hController.
        static int ServicesToSlot(void *services)
        {
            void *pawn = ServicesToPawnField(services);
            if (!PawnOwnsServices(pawn, services))
                pawn = nullptr;

            int slot = pawn ? ControllerSlotForPawn(pawn) : -1;
            return slot >= 0 ? slot : RegisteredSlotForServices(services);
        }

        // services -> pawn -> WeaponServices*, for the recording weapon tap.
        static void *ServicesToWeaponServices(void *services)
        {
            int slot = ServicesToSlot(services);
            void *pawn = ResolveReplayPawn(slot, services);
            if (!pawn)
                return nullptr;
            void *ws = nullptr;
            return SafeRead(pawn, tg::kPawn_WeaponServices, ws) ? ws : nullptr;
        }

        static float NormalizeDeg(float a)
        {
            a = std::fmod(a + 180.0f, 360.0f);
            if (a < 0.0f)
                a += 360.0f;
            return a - 180.0f;
        }

        bool SetControllerControllingBotOffset(int offset)
        {
            if (offset < 0 || offset > 0x10000)
                offset = -1;
            g_controllerControllingBotOffset.store(offset, std::memory_order_release);
            return true;
        }

        static bool ControllerIsControllingBot(void *controller)
        {
            int offset = g_controllerControllingBotOffset.load(std::memory_order_acquire);
            if (!controller || offset < 0)
                return false;
            uint8_t value = 0;
            return SafeRead(controller, offset, value) && value != 0;
        }

        static bool ReplayActiveAndSafe(int slot)
        {
            if (slot < 0 || slot >= kMaxSlots || !MotionRecorder::IsReplaying(slot))
                return false;
            if (!g_setupMoveActive || !g_finishMoveActive || !g_subtickActive)
            {
                MotionRecorder::StopReplay(slot);
                DebugOut("[BotController] stopped replay: required command/movement/view boundary hooks unavailable\n");
                return false;
            }
            if (!g_slotControllingBot[slot].load(std::memory_order_acquire))
                return true;

            MotionRecorder::StopReplay(slot);
            char dbg[128];
            std::snprintf(dbg, sizeof(dbg),
                          "[BotController] stopped replay slot=%d: controller is controlling a bot\n",
                          slot);
            DebugOut(dbg);
            return false;
        }

        bool ReadReplayClock(int slot, float tickInterval, ReplaySourceState::LiveClock &clock)
        {
            if (slot < 0 || slot >= kMaxSlots || !std::isfinite(tickInterval) || tickInterval <= 0) return false;
            void *controller = g_slotControllers[slot].load(std::memory_order_acquire);
            if (!controller || ControllerToSlot(controller) != slot ||
                !SafeRead(controller, g_tickBaseOffset, clock.tick) || clock.tick < 0) return false;
            // At the command boundary, the player's simulation clock is tickbase
            // times the live engine interval supplied by CounterStrikeSharp.
            clock.interval = tickInterval;
            clock.time = static_cast<float>(clock.tick) * tickInterval;
            return std::isfinite(clock.time);
        }
        bool InitializeReplayMoveType(void *pawn, uint8_t moveType)
        {
            uint8_t collide = 0;
            if (!pawn || !g_setMoveType || !SafeRead(pawn, g_moveCollideOffset, collide)) return false;
            g_setMoveType(pawn, moveType, collide);
            return true;
        }

        void PublishReplayState(void *entity)
        {
            if (!entity) return;
            // One boundary notification covers nested service chains too.
            // Continuous playback uses the engine's own change tracking.
            const NetworkStateChangedData changed(true);
            reinterpret_cast<CEntityInstance *>(entity)->NetworkStateChanged(changed);
        }

        bool InitializeReplayPose(void *pawn, const float *origin, const float *velocity)
        {
            if (!pawn || !origin || !velocity || !g_setAbsOrigin || !g_setAbsVelocity)
                return false;
            g_setAbsOrigin(pawn, origin);
            g_setAbsVelocity(pawn, velocity);
            return true;
        }

        // ---- SetupMove: supply input before any subtick movement ----

        static KHook::Return<void> BC_FASTCALL HookedSetupMove(void *services, void *cmd, void *moveData)
        {
            g_hookSetupMove.Continue(services, cmd, moveData);
            const int slot = ServicesToSlot(services);
            if (ReplayActiveAndSafe(slot))
                MotionRecorder::OnReplaySetupMove(slot, moveData);
            return {KHook::Action::Ignore};
        }

        // ---- ProcessMovement: record pre/post; replay simulation is native ----

        // Defined after HookedFinishMove
        static void EnsureVtableHooks(void *services);

        static KHook::Return<void> BC_FASTCALL HookedProcessMovement(void *services, void *moveData)
        {
            MotionRecorder::AddReplayPerf(MotionRecorder::ReplayPerfCounter::ProcessMovementHook);
            g_hookCalls.fetch_add(1, std::memory_order_relaxed);
            int slot = ServicesToSlot(services);
            g_lastSlot.store(slot, std::memory_order_relaxed);

            // Lazily hook FinishMove from the live services vtable on first tick.
            EnsureVtableHooks(services);

            // Cache slot -> services so PhysicsSimulate
            if (slot >= 0 && slot < kMaxSlots)
                g_slotServices[slot].store(services, std::memory_order_release);

            bool recording = slot >= 0 && slot < kMaxSlots &&
                             MotionRecorder::IsRecording(slot);
            bool replaying = ReplayActiveAndSafe(slot);
            if (replaying)
            {
                ReplayPawnEquipment::ApplyPendingForPawn(
                    slot, ResolveReplayPawn(slot, services));
            }
            UsercmdMovementIntentFrame movementIntent{};
            bool hasMovementIntent =
                !replaying && !IsSlotControllingBot(slot) &&
                ActiveUsercmdMovementIntent(slot, movementIntent);

            // Recording weapon tap
            if (recording)
            {
                MotionRecorder::SetLiveWs(slot, ServicesToWeaponServices(services));
                if (!g_physicsActive)
                    MotionRecorder::OnCapturePre(slot, services, moveData);
            }

            if (hasMovementIntent)
                ApplyUsercmdMovementIntentToMoveData(services, moveData, movementIntent);

            g_hookProcessMovement.Continue(services, moveData);

            // Recording: commit the tick here only when PhysicsSimulate isn't the boundary
            if (recording && !g_physicsActive)
                MotionRecorder::OnCapturePost(slot, services, moveData);
            return {KHook::Action::Ignore};
        }

        // ---- FinishMove: replay post-move and final local view ----

        static KHook::Return<void> BC_FASTCALL HookedFinishMove(void *services, void *cmd,
                                                void *moveData)
        {
            MotionRecorder::AddReplayPerf(MotionRecorder::ReplayPerfCounter::FinishMoveHook);
            int slot = ServicesToSlot(services);
            bool replaying = ReplayActiveAndSafe(slot);

            // Publish the engine's output through its normal origin/velocity
            // setters. Never replace it with post snapshots or fake an origin delta.
            g_hookFinishMove.Continue(services, cmd, moveData);

            // After original: prepare the final getter before the engine publishes
            // network eye angles in the remaining PlayerRunCommand tail.
            if (replaying)
                MotionRecorder::OnReplayFinalView(slot, services);
            return {KHook::Action::Ignore};
        }

        // ---- PlayerRunCommand: subtick record + re-inject ----

        static KHook::Return<void> BC_FASTCALL HookedPlayerRunCommand(void *services, void *cmd)
        {
            ProjectileBirthAlign::ProcessPending();
            MotionRecorder::AddReplayPerf(MotionRecorder::ReplayPerfCounter::PlayerRunCommandHook);
            int slot = ServicesToSlot(services);
            bool recording = slot >= 0 && slot < kMaxSlots &&
                             MotionRecorder::IsRecording(slot);
            bool replaying = ReplayActiveAndSafe(slot);
            if (replaying)
            {
                ReplayPawnEquipment::ApplyPendingForPawn(
                    slot, ResolveReplayPawn(slot, services));
            }
            UsercmdMovementIntentFrame movementIntent{};
            bool hasMovementIntent =
                !replaying && !IsSlotControllingBot(slot) &&
                ActiveUsercmdMovementIntent(slot, movementIntent);
            bool leftHandDesired = false;
            bool hasLeftHandLatch = slot >= 0 && slot < kMaxSlots &&
                                    g_leftHandLatches[slot].Get(leftHandDesired);
            if (hasLeftHandLatch)
            {
                void *pawn = ResolveReplayPawn(slot, services);
                uint32_t handle = 0;
                const bool safeOwner = ReadHandLatchOwner(slot, pawn, handle);
                hasLeftHandLatch = g_leftHandLatches[slot].Validate(
                    reinterpret_cast<uintptr_t>(pawn), handle, safeOwner);
            }

            bool hasPublicControl = g_requests.Pending(slot);
            if (hasPublicControl && !CanUsePublicControl(slot))
            {
                ClearUsercmdInjections(slot);
                hasPublicControl = false;
            }
            if (cmd && (recording || replaying || hasMovementIntent || hasLeftHandLatch || hasPublicControl))
            {
                // Compiler computes the multiple-inheritance adjust here.
                auto *pc = reinterpret_cast<PlayerCommand *>(cmd);
                CBaseUserCmdPB *base = pc->mutable_base();

                if (recording)
                {
                    // Read this tick's subtick_moves into SubtickMove[] and
                    // stash; OnCapturePost (PhysicsSimulate-post) commits them.
                    int n = base->subtick_moves_size();
                    if (n > MotionRecorder::kMaxSubtickPerTick)
                        n = MotionRecorder::kMaxSubtickPerTick;
                    SubtickMove moves[MotionRecorder::kMaxSubtickPerTick];
                    for (int i = 0; i < n; ++i)
                    {
                        const CSubtickMoveStep &s = base->subtick_moves(i);
                        moves[i].when = s.when();
                        moves[i].button = static_cast<uint32_t>(s.button());
                        moves[i].pressed = s.pressed() ? 1.0f : 0.0f;
                        moves[i].analogForward = s.analog_forward_delta();
                        moves[i].analogLeft = s.analog_left_delta();
                        moves[i].pitchDelta = s.pitch_delta();
                        moves[i].yawDelta = s.yaw_delta();
                    }
                    MotionRecorder::OnCaptureSubticks(slot, moves, n);

                ReplayCommandFrameData command{};
                command.buttons = pc->buttonstates.m_pButtonStates[0];
                command.buttons1 = pc->buttonstates.m_pButtonStates[1];
                command.buttons2 = pc->buttonstates.m_pButtonStates[2];
                command.fields |= MotionRecorder::kCommandFieldButtons;
                if (base->has_forwardmove())
                {
                    command.forwardMove = base->forwardmove();
                    command.fields |= MotionRecorder::kCommandFieldForwardMove;
                }
                if (base->has_leftmove())
                {
                    command.leftMove = base->leftmove();
                    command.fields |= MotionRecorder::kCommandFieldLeftMove;
                }
                if (base->has_upmove())
                {
                    command.upMove = base->upmove();
                    command.fields |= MotionRecorder::kCommandFieldUpMove;
                }
                if (base->has_viewangles())
                {
                    const CMsgQAngle& view = base->viewangles();
                    command.pitch = view.x();
                    command.yaw = view.y();
                    command.roll = view.z();
                    command.fields |= MotionRecorder::kCommandFieldViewAngles;
                }
                if (base->has_mousedx() || base->has_mousedy())
                {
                    command.mouseDx = base->mousedx();
                    command.mouseDy = base->mousedy();
                    command.fields |= MotionRecorder::kCommandFieldMouse;
                }
                if (base->has_weaponselect())
                {
                    command.weaponSelect = base->weaponselect();
                    command.fields |= MotionRecorder::kCommandFieldWeaponSelect;
                }
                // This is a complete engine command, not a demo delta. An
                // omitted protobuf bool is the authoritative default false.
                command.leftHandDesired = pc->left_hand_desired() ? 1 : 0;
                command.fields |= MotionRecorder::kCommandFieldLeftHand;
                MotionRecorder::OnCaptureCommand(slot, command);
                }

                if (replaying)
                {
                    MotionRecorder::ReplayCommandFrame frame{};
                    if (MotionRecorder::ReplayCommandFrameForSimulation(slot, frame) &&
                        MotionRecorder::OnReplayCommandPre(
                            slot, services, *frame.tick, frame.commandView))
                    {
                        CInButtonStatePB *bp = base->mutable_buttons_pb();
                        bp->set_buttonstate1(frame.buttons0);
                        bp->set_buttonstate2(frame.buttons1);
                        bp->set_buttonstate3(frame.buttons2);
                        pc->buttonstates.m_pButtonStates[0] = frame.buttons0;
                        pc->buttonstates.m_pButtonStates[1] = frame.buttons1;
                        pc->buttonstates.m_pButtonStates[2] = frame.buttons2;

                        CMsgQAngle *view = base->mutable_viewangles();
                        view->set_x(frame.commandView.pitch);
                        view->set_y(NormalizeDeg(frame.commandView.yaw));
                        view->set_z(
                            (frame.commandFields & MotionRecorder::kCommandFieldViewAngles) != 0
                                ? frame.commandView.roll
                                : 0.0f);

                        // Missing recorded axes are neutral. Never inherit the
                        // shadow-running bot AI's movement as replay input.
                        base->set_forwardmove(frame.forwardMove);
                        base->set_leftmove(frame.leftMove);
                        base->set_upmove(frame.upMove);
                        if ((frame.commandFields & MotionRecorder::kCommandFieldMouse) != 0)
                        {
                            base->set_mousedx(frame.mouseDx);
                            base->set_mousedy(frame.mouseDy);
                        }
                        const bool frameHasLeftHand =
                            (frame.commandFields & MotionRecorder::kCommandFieldLeftHand) != 0;
                        if (frameHasLeftHand)
                        {
                            pc->set_left_hand_desired(frame.leftHandDesired != 0);
                            // The command is the truth source. Keep its latest
                            // desire across the first native command after handoff.
                            leftHandDesired = frame.leftHandDesired != 0;
                            g_leftHandLatches[slot].ObserveReplayCommand(leftHandDesired);
                        }

                        if (frame.weaponSelect >= 0)
                            base->set_weaponselect(frame.weaponSelect);

                        if (frame.subtickCount <= 0)
                        {
                            if (base->subtick_moves_size() > 0)
                            {
                                base->clear_subtick_moves();
                                MotionRecorder::AddReplayPerf(MotionRecorder::ReplayPerfCounter::SubtickClear);
                            }
                            else
                            {
                                MotionRecorder::AddReplayPerf(MotionRecorder::ReplayPerfCounter::SubtickNoopSkip);
                            }
                        }
                        else
                        {
                            base->clear_subtick_moves();
                            MotionRecorder::AddReplayPerf(MotionRecorder::ReplayPerfCounter::SubtickClear);
                            MotionRecorder::AddReplayPerf(MotionRecorder::ReplayPerfCounter::SubtickRebuild);
                            const bool injectViewDeltas = ReplaySubtickViewDeltas();
                            for (int i = 0; i < frame.subtickCount; ++i)
                            {
                                const SubtickMove &subtick = frame.subticks[i];
                                CSubtickMoveStep *m = base->add_subtick_moves();
                                MotionRecorder::AddReplayPerf(MotionRecorder::ReplayPerfCounter::SubticksAdded);
                                m->set_when(
                                    ReplaySubtickLayout::ProjectSubtickWhenForEngine(subtick.when));
                                m->set_button(subtick.button);
                                if (subtick.button != 0) // digital press/release
                                    m->set_pressed(subtick.pressed != 0.0f);
                                if (injectViewDeltas && subtick.pitchDelta != 0.0f)
                                    m->set_pitch_delta(subtick.pitchDelta);
                                if (injectViewDeltas && subtick.yawDelta != 0.0f)
                                    m->set_yaw_delta(subtick.yawDelta);
                                if (subtick.analogForward != 0.0f)
                                    m->set_analog_forward_delta(subtick.analogForward);
                                if (subtick.analogLeft != 0.0f)
                                    m->set_analog_left_delta(subtick.analogLeft);
                            }
                        }
                    }
                    else
                    {
                        MotionRecorder::StopReplay(slot);
                        replaying = false;
                    }
                }

                if (hasLeftHandLatch)
                {
                    pc->set_left_hand_desired(leftHandDesired);
                }

                if (hasMovementIntent)
                    ApplyUsercmdMovementIntentToCommand(services, pc, base, movementIntent);
                if (hasPublicControl && !hasMovementIntent)
                    ApplyPublicControl(slot, pc, base);
            }

            g_hookPlayerRunCommand.Continue(services, cmd);
            // The normal publisher runs inside PlayerRunCommand after FinishMove.
            // Even without PhysicsSimulate, retain the final getter until that
            // tail completes; ending replay in FinishMove loses the final view.
            if (replaying && !g_physicsActive)
                MotionRecorder::OnReplayCommit(slot, services);
            MotionRecorder::OnReplayCommandPost(slot, services, replaying);
            return {KHook::Action::Ignore};
        }

        // ---- PhysicsSimulate: the per-tick boundary ----
        // Records pre/post + commits

        static KHook::Return<void> BC_FASTCALL HookedPhysicsSimulate(void *controller)
        {
            ProjectileBirthAlign::ProcessPending();
            MotionRecorder::AddReplayPerf(MotionRecorder::ReplayPerfCounter::PhysicsSimulateHook);
            int slot = ControllerToSlot(controller);
            void *services = (slot >= 0 && slot < kMaxSlots)
                                 ? g_slotServices[slot].load(std::memory_order_acquire)
                                 : nullptr;

            bool recording = slot >= 0 && slot < kMaxSlots && services &&
                             MotionRecorder::IsRecording(slot);
            if (slot >= 0 && slot < kMaxSlots)
            {
                g_slotControllers[slot].store(controller, std::memory_order_release);
                g_slotControllingBot[slot].store(ControllerIsControllingBot(controller), std::memory_order_release);
            }

            bool replaying = services && ReplayActiveAndSafe(slot);

            // pre: snapshot start-of-tick state once (before any subtick mover).
            if (recording)
                MotionRecorder::OnCapturePre(slot, services, nullptr);

            g_hookPhysicsSimulate.Continue(controller);

            // post: snapshot end-of-tick state + commit one frame
            if (recording)
                MotionRecorder::OnCapturePost(slot, services, nullptr);
            if (replaying)
                MotionRecorder::OnReplayCommit(slot, services);
            return {KHook::Action::Ignore};
        }

        static std::atomic<bool> g_vtHooksTried{false};

        static void EnsureVtableHooks(void *services)
        {
            // Once installed, this runs on every ProcessMovement call. Keep
            // the steady-state path read-only instead of issuing a cache-line
            // RMW, while retaining a one-winner lazy installation.
            if (g_vtHooksTried.load(std::memory_order_acquire))
                return;
            if (!services)
                return;
            void **vt = nullptr;
            if (!SafeRead(services, 0, vt) || !vt)
                return;
            bool expected = false;
            if (!g_vtHooksTried.compare_exchange_strong(
                    expected, true,
                    std::memory_order_acq_rel,
                    std::memory_order_acquire))
                return;

            if (!SafeRead(vt,
                          tg::kVtIdx_SetupMove * static_cast<int>(sizeof(void *)),
                          g_addrSetupMove))
                g_addrSetupMove = nullptr;
            if (g_addrSetupMove &&
                g_hookSetupMove.Create(g_addrSetupMove,
                                       &HookedSetupMove,
                                       &g_origSetupMove) &&
                g_hookSetupMove.Enable())
            {
                g_setupMoveActive = true;
            }
            else
            {
                g_hookSetupMove.Remove();
                g_addrSetupMove = nullptr;
                g_origSetupMove = nullptr;
            }

            if (!SafeRead(vt,
                          tg::kVtIdx_FinishMove * static_cast<int>(sizeof(void *)),
                          g_addrFinishMove))
                g_addrFinishMove = nullptr;
            if (g_addrFinishMove &&
                g_hookFinishMove.Create(g_addrFinishMove,
                                        &HookedFinishMove,
                                        &g_origFinishMove) &&
                g_hookFinishMove.Enable())
            {
                g_finishMoveActive = true;
            }
            else
            {
                g_hookFinishMove.Remove();
                g_addrFinishMove = nullptr;
                g_origFinishMove = nullptr;
            }

            // PlayerRunCommand (subtick record/re-inject)
            if (!SafeRead(vt,
                          tg::kVtIdx_PlayerRunCommand * static_cast<int>(sizeof(void *)),
                          g_addrPlayerRunCommand))
                g_addrPlayerRunCommand = nullptr;
            if (g_addrPlayerRunCommand &&
                g_hookPlayerRunCommand.Create(g_addrPlayerRunCommand,
                                              &HookedPlayerRunCommand,
                                              &g_origPlayerRunCommand) &&
                g_hookPlayerRunCommand.Enable())
            {
                g_subtickActive = true;
            }
            else if (g_addrPlayerRunCommand)
            {
                g_hookPlayerRunCommand.Remove();
                g_addrPlayerRunCommand = nullptr;
                g_origPlayerRunCommand = nullptr;
            }

            if (!g_setupMoveActive || !g_finishMoveActive || !g_subtickActive)
                g_status = "failed: replay command/movement/view boundary hooks";

            char dbg[200];
            std::snprintf(dbg, sizeof(dbg),
                          "[BotController] vtable hooks: SetupMove @ %p, FinishMove @ %p, "
                          "PlayerRunCommand @ %p (subtick=%d)\n",
                          g_addrSetupMove, g_addrFinishMove, g_addrPlayerRunCommand,
                          g_subtickActive ? 1 : 0);
            DebugOut(dbg);
        }

        bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                     char *errorOut, size_t errorOutLen)
        {
            g_moveCollideOffset = Schema::GetFieldOffset("CBaseEntity", "m_MoveCollide");
            g_tickBaseOffset = Schema::GetFieldOffset("CBasePlayerController", "m_nTickBase");
            if (g_tickBaseOffset < 0) g_tickBaseOffset = Schema::GetFieldOffset("CCSPlayerController", "m_nTickBase");
            g_setMoveType = reinterpret_cast<SetMoveType_t>(Sig::ResolveSig(gd, serverModule, "CBaseEntity::SetMoveType", errorOut, errorOutLen));
            if (!g_setMoveType || g_moveCollideOffset < 0 || g_tickBaseOffset < 0 || !ReplaySourceState::InitializeOffsets()) {
                g_status = "failed: replay source-state schema/setter";
                std::snprintf(errorOut, errorOutLen, "%s", g_status.c_str());
                return false;
            }
            g_setAbsOrigin = reinterpret_cast<SetEntityVector_t>(Sig::ResolveSig(
                gd, serverModule, "CBaseEntity::SetAbsOrigin", errorOut, errorOutLen));
            if (!g_setAbsOrigin)
            {
                g_status = "failed: replay origin initialization sig";
                return false;
            }
            g_setAbsVelocity = reinterpret_cast<SetEntityVector_t>(Sig::ResolveSig(
                gd, serverModule, "CBaseEntity::SetAbsVelocity", errorOut, errorOutLen));
            if (!g_setAbsVelocity)
            {
                g_setAbsOrigin = nullptr;
                g_status = "failed: replay velocity initialization sig";
                return false;
            }
            g_addrProcessMovement = Sig::ResolveSig(
                gd, serverModule, "CCSPlayer_MovementServices::ProcessMovement",
                errorOut, errorOutLen);
            if (!g_addrProcessMovement)
            {
                g_status = "failed: ProcessMovement sig";
                return false;
            }
            if (!g_hookProcessMovement.Create(g_addrProcessMovement,
                                              &HookedProcessMovement,
                                              &g_origProcessMovement) ||
                !g_hookProcessMovement.Enable())
            {
                std::snprintf(errorOut, errorOutLen, "hook ProcessMovement failed");
                g_hookProcessMovement.Remove();
                g_origProcessMovement = nullptr;
                g_status = "failed: hook ProcessMovement";
                return false;
            }

            // PhysicsSimulate: the per-tick boundary
            char psErr[256] = {0};
            g_addrPhysicsSimulate = Sig::ResolveSig(
                gd, serverModule, "CCSPlayer_MovementServices::PhysicsSimulate",
                psErr, sizeof(psErr));
            if (g_addrPhysicsSimulate &&
                g_hookPhysicsSimulate.Create(g_addrPhysicsSimulate,
                                             &HookedPhysicsSimulate,
                                             &g_origPhysicsSimulate) &&
                g_hookPhysicsSimulate.Enable())
            {
                g_physicsActive = true;
            }
            else
            {
                if (g_addrPhysicsSimulate)
                {
                    g_hookPhysicsSimulate.Remove();
                    g_addrPhysicsSimulate = nullptr;
                }
                g_origPhysicsSimulate = nullptr;
                char dbg[320];
                std::snprintf(dbg, sizeof(dbg),
                              "[BotController] WARN: PhysicsSimulate hook unavailable (%s); "
                              "replay falls back to PlayerRunCommand-post (may stutter)\n",
                              psErr[0] ? psErr : "KHook failed");
                DebugOut(dbg);
            }

            // FinishMove is hooked lazily from the live vtable on the first ProcessMovement tick.
            g_installed = true;
            g_status = "ok";
            char dbg[160];
            std::snprintf(dbg, sizeof(dbg),
                          "[BotController] ProcessMovement @ %p\n",
                          g_addrProcessMovement);
            DebugOut(dbg);
            return true;
        }

        void Remove()
        {
            if (!g_installed)
                return;
            g_hookProcessMovement.Remove();
            g_hookFinishMove.Remove();
            g_hookSetupMove.Remove();
            g_hookPlayerRunCommand.Remove();
            g_hookPhysicsSimulate.Remove();
            g_origProcessMovement = nullptr;
            g_origFinishMove = nullptr;
            g_origSetupMove = nullptr;
            g_setAbsOrigin = nullptr;
            g_setAbsVelocity = nullptr;
            g_setMoveType = nullptr;
            for (auto &controller : g_slotControllers) controller.store(nullptr, std::memory_order_release);
            g_origPlayerRunCommand = nullptr;
            g_origPhysicsSimulate = nullptr;
            g_addrProcessMovement = nullptr;
            g_addrFinishMove = nullptr;
            g_addrSetupMove = nullptr;
            g_addrPlayerRunCommand = nullptr;
            g_addrPhysicsSimulate = nullptr;
            g_physicsActive = false;
            g_finishMoveActive = false;
            g_setupMoveActive = false;
            g_subtickActive = false;
            g_vtHooksTried.store(false, std::memory_order_release);
            for (auto &s : g_slotServices)
                s.store(nullptr, std::memory_order_release);
            for (auto &taken : g_slotControllingBot)
                taken.store(false, std::memory_order_release);
            ClearAllUsercmdMovementIntents();
            ClearAllLeftHandDesiredLatches();
            g_installed = false;
            g_status = "not_attempted";
        }

        const char *Status() { return g_status.c_str(); }

        void *ProcessUsercmdAddress() { return g_addrProcessMovement; }

        uint64_t HookCallCount() { return g_hookCalls.load(std::memory_order_relaxed); }
        int LastResolvedSlot() { return g_lastSlot.load(std::memory_order_relaxed); }
    }
}
