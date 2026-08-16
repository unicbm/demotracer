// CS2 movement hooks
// ProcessMovement (record + apply pre)
// FinishMove (replay post into MoveData + commit)
// PlayerRunCommand(subtick record + re-inject)

#include "playercommand.h"

#include "InputInjector.h"
#include "ccsbot_slot.h"
#include "sig_scan.h"
#include "MotionRecorder.h"
#include "ReplayPawnEquipment.h"
#include "projectile_birth_align.h"
#include "version_targets.h"
#include "hook.h"
#include "platform.h"

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
using PlayerRunCommand_t = void(BC_FASTCALL *)(void *services, void *cmd);
using PhysicsSimulate_t = void(BC_FASTCALL *)(void *controller);

namespace BotController
{
    namespace InputInjector
    {
        static ProcessMovement_t g_origProcessMovement = nullptr;
        static FinishMove_t g_origFinishMove = nullptr;
        static PlayerRunCommand_t g_origPlayerRunCommand = nullptr;
        static PhysicsSimulate_t g_origPhysicsSimulate = nullptr;

        static void *g_addrProcessMovement = nullptr;
        static void *g_addrFinishMove = nullptr;
        static void *g_addrPlayerRunCommand = nullptr;
        static void *g_addrPhysicsSimulate = nullptr;

        static Hook g_hookProcessMovement;
        static Hook g_hookFinishMove;
        static Hook g_hookPlayerRunCommand;
        static Hook g_hookPhysicsSimulate;
        static bool g_installed = false;
        // True once PhysicsSimulate is hooked
        static bool g_physicsActive = false;
        // True once PlayerRunCommand is hooked
        static bool g_subtickActive = false;
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
        static std::array<std::atomic<int>, kMaxSlots> g_leftHandLatchEnabled{};
        static std::array<std::atomic<int>, kMaxSlots> g_leftHandLatchDesired{};

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

            sideMove = -leftMove;
        }

        static void ApplyUsercmdMovementIntentToMoveData(
            void *services,
            void *moveData,
            const UsercmdMovementIntentFrame &intent)
        {
            if (services && tg::kServices_Buttons > 0)
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

        bool SetLeftHandDesiredLatch(int slot, bool enabled, bool leftHandDesired)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return false;

            g_leftHandLatchDesired[slot].store(leftHandDesired ? 1 : 0, std::memory_order_relaxed);
            g_leftHandLatchEnabled[slot].store(enabled ? 1 : 0, std::memory_order_release);
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

            if (enabled)
                *enabled = g_leftHandLatchEnabled[slot].load(std::memory_order_acquire) != 0;
            if (leftHandDesired)
                *leftHandDesired = g_leftHandLatchDesired[slot].load(std::memory_order_relaxed) != 0;
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
            {
                ReplayPawnEquipment::Clear(slot);
                g_slotPawns[slot].store(nullptr, std::memory_order_release);
            }
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

        // ---- ProcessMovement: record pre/post + replay pre ----

        // Defined after HookedFinishMove
        static void EnsureVtableHooks(void *services);

        static void BC_FASTCALL HookedProcessMovement(void *services, void *moveData)
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

            // Replay: seed CMoveData + pawn with this tick's pre snapshot
            if (replaying)
                MotionRecorder::OnReplayPre(slot, services, moveData);
            if (hasMovementIntent)
                ApplyUsercmdMovementIntentToMoveData(services, moveData, movementIntent);

            g_origProcessMovement(services, moveData);

            // Recording: commit the tick here only when PhysicsSimulate isn't the boundary
            if (recording && !g_physicsActive)
                MotionRecorder::OnCapturePost(slot, services, moveData);
        }

        // ---- FinishMove: replay post-write + commit ----

        static void BC_FASTCALL HookedFinishMove(void *services, void *cmd,
                                                void *moveData)
        {
            MotionRecorder::AddReplayPerf(MotionRecorder::ReplayPerfCounter::FinishMoveHook);
            int slot = ServicesToSlot(services);
            bool replaying = ReplayActiveAndSafe(slot);

            // Before original: write post snapshot into MoveData + force resync.
            if (replaying)
                MotionRecorder::OnReplayFinishMove(slot, services, moveData);

            g_origFinishMove(services, cmd, moveData);

            // After original: publish post view while the current replay cursor
            // still points at this simulation tick.
            if (replaying)
                MotionRecorder::OnReplayFinalView(slot, services);

            // After original: commit moveType/flags + advance the replay cursor
            if (replaying && !g_physicsActive)
                MotionRecorder::OnReplayCommit(slot, services);
        }

        // ---- PlayerRunCommand: subtick record + re-inject ----

        static void BC_FASTCALL HookedPlayerRunCommand(void *services, void *cmd)
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
            bool hasLeftHandLatch = slot >= 0 && slot < kMaxSlots &&
                                    g_leftHandLatchEnabled[slot].load(std::memory_order_acquire) != 0;

            if (cmd && (recording || replaying || hasMovementIntent || hasLeftHandLatch))
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
                }

                if (replaying)
                {
                    MotionRecorder::ReplayCommandFrame frame{};
                    if (MotionRecorder::ReplayCommandFrameForSimulation(slot, frame))
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

                        if ((frame.commandFields & MotionRecorder::kCommandFieldForwardMove) != 0)
                            base->set_forwardmove(frame.forwardMove);
                        if ((frame.commandFields & MotionRecorder::kCommandFieldLeftMove) != 0)
                            base->set_leftmove(frame.leftMove);
                        if ((frame.commandFields & MotionRecorder::kCommandFieldUpMove) != 0)
                            base->set_upmove(frame.upMove);
                        if ((frame.commandFields & MotionRecorder::kCommandFieldMouse) != 0)
                        {
                            base->set_mousedx(frame.mouseDx);
                            base->set_mousedy(frame.mouseDy);
                        }
                        const bool frameHasLeftHand =
                            (frame.commandFields & MotionRecorder::kCommandFieldLeftHand) != 0;
                        if (frameHasLeftHand)
                            pc->set_left_hand_desired(frame.leftHandDesired != 0);

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
                                m->set_when(subtick.when);
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

                        MotionRecorder::OnReplayCommandPre(
                            slot, services, *frame.tick, frame.commandView);
                    }
                }

                if (hasLeftHandLatch)
                {
                    const bool leftHandDesired =
                        g_leftHandLatchDesired[slot].load(std::memory_order_relaxed) != 0;
                    pc->set_left_hand_desired(leftHandDesired);
                }

                if (hasMovementIntent)
                    ApplyUsercmdMovementIntentToCommand(pc, base, movementIntent);
            }

            g_origPlayerRunCommand(services, cmd);
            MotionRecorder::OnReplayCommandPost(slot, services, replaying);
        }

        // ---- PhysicsSimulate: the per-tick boundary ----
        // Records pre/post + commits

        static void BC_FASTCALL HookedPhysicsSimulate(void *controller)
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
                g_slotControllingBot[slot].store(ControllerIsControllingBot(controller), std::memory_order_release);
            }

            bool replaying = services && ReplayActiveAndSafe(slot);

            // pre: snapshot start-of-tick state once (before any subtick mover).
            if (recording)
                MotionRecorder::OnCapturePre(slot, services, nullptr);

            g_origPhysicsSimulate(controller);

            // post: snapshot end-of-tick state + commit one frame
            if (recording)
                MotionRecorder::OnCapturePost(slot, services, nullptr);
            if (replaying)
                MotionRecorder::OnReplayCommit(slot, services);
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
                          tg::kVtIdx_FinishMove * static_cast<int>(sizeof(void *)),
                          g_addrFinishMove))
                g_addrFinishMove = nullptr;
            if (g_addrFinishMove &&
                g_hookFinishMove.Create(g_addrFinishMove,
                                        reinterpret_cast<void *>(&HookedFinishMove),
                                        reinterpret_cast<void **>(&g_origFinishMove)))
                g_hookFinishMove.Enable();

            // PlayerRunCommand (subtick record/re-inject)
            if (!SafeRead(vt,
                          tg::kVtIdx_PlayerRunCommand * static_cast<int>(sizeof(void *)),
                          g_addrPlayerRunCommand))
                g_addrPlayerRunCommand = nullptr;
            if (g_addrPlayerRunCommand &&
                g_hookPlayerRunCommand.Create(g_addrPlayerRunCommand,
                                              reinterpret_cast<void *>(&HookedPlayerRunCommand),
                                              reinterpret_cast<void **>(&g_origPlayerRunCommand)) &&
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

            char dbg[200];
            std::snprintf(dbg, sizeof(dbg),
                          "[BotController] vtable hooks: FinishMove @ %p, "
                          "PlayerRunCommand @ %p (subtick=%d)\n",
                          g_addrFinishMove, g_addrPlayerRunCommand,
                          g_subtickActive ? 1 : 0);
            DebugOut(dbg);
        }

        bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                     char *errorOut, size_t errorOutLen)
        {
            g_addrProcessMovement = Sig::ResolveSig(
                gd, serverModule, "CCSPlayer_MovementServices::ProcessMovement",
                errorOut, errorOutLen);
            if (!g_addrProcessMovement)
            {
                g_status = "failed: ProcessMovement sig";
                return false;
            }
            if (!g_hookProcessMovement.Create(g_addrProcessMovement,
                                              reinterpret_cast<void *>(&HookedProcessMovement),
                                              reinterpret_cast<void **>(&g_origProcessMovement)) ||
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
                                             reinterpret_cast<void *>(&HookedPhysicsSimulate),
                                             reinterpret_cast<void **>(&g_origPhysicsSimulate)) &&
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
                              "replay falls back to per-subtick boundary (may stutter)\n",
                              psErr[0] ? psErr : "funchook failed");
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
            g_hookPlayerRunCommand.Remove();
            g_hookPhysicsSimulate.Remove();
            g_origProcessMovement = nullptr;
            g_origFinishMove = nullptr;
            g_origPlayerRunCommand = nullptr;
            g_origPhysicsSimulate = nullptr;
            g_addrProcessMovement = nullptr;
            g_addrFinishMove = nullptr;
            g_addrPlayerRunCommand = nullptr;
            g_addrPhysicsSimulate = nullptr;
            g_physicsActive = false;
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
