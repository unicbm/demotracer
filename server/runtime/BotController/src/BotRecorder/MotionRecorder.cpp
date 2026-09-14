// Motion recording & replay implementation

#include "MotionRecorder.h"
#include "ButtonState.h"
#include "InputInjector.h"
#include "ReplayPawnEquipment.h"
#include "ReplaySubtickLayout.h"
#include "WeaponLocker.h"
#include "ccsbot_slot.h"
#include "version_targets.h"

#include <array>
#include <atomic>
#include <cmath>
#include <mutex>
#include <vector>

#ifdef _WIN32
#include <Windows.h>
#endif

namespace tg = BotController::targets;

namespace BotController
{
    namespace MotionRecorder
    {
        struct RecordState
        {
            std::atomic<bool> recording{false};
            std::vector<ReplayTick> ticks;
            std::vector<SubtickMove> subs;
            // Subtick moves seen on PlayerRunCommand, awaiting the matching
            // ProcessMovement post that commits them to a tick.
            std::vector<SubtickMove> pendingSubs;
            std::vector<ReplayCommandFrameData> commands;
            ReplayCommandFrameData pendingCommand{};
            MovementSnapshot pendingPre{};
            bool havePre{false};
            std::atomic<void *> liveWs{nullptr};
            std::atomic<int> currentDef{-1};
            std::mutex mu; // guards ticks/subs/pending/pre
        };

        struct ReplayState
        {
            std::atomic<bool> playing{false};
            std::atomic<bool> loop{false};
            std::vector<ReplayTick> ticks;
            std::vector<SubtickMove> subs;
            std::vector<ReplayCommandFrameData> commands;
            std::vector<ReplayMovementExtra> movementExtras;
            std::vector<ReplayInputHistoryTick> inputHistoryTicks;
            std::vector<ReplayInputHistoryEntry> inputHistoryEntries;
            std::vector<size_t> inputHistoryOffset;
            std::vector<size_t> subOffset; // prefix sum, size ticks.size()+1
            std::atomic<int> cursor{0};
            std::atomic<int> startCursor{0};
            std::atomic<int> holdBeforeCursor{-1};
            // Published with playing; consumed only by the simulation thread.
            bool initializeMovement = false;
            // Replay weapon-select cache. cachedWeaponDef is the publication
            // marker and is stored only after the remaining fields are ready.
            std::atomic<int> cachedWeaponDef{-1};
            std::atomic<void *> cachedWeaponServices{nullptr};
            std::atomic<void *> cachedWeapon{nullptr};
            std::atomic<int> cachedWeaponEntIndex{-1};
            std::atomic<int> cachedWeaponSlot{-1};
            std::atomic<unsigned int> cachedWeaponPosition{0xFFFFFFFFu};
            std::mutex mu; // guards replay buffers and offset tables
        };

        static std::array<RecordState, kMaxSlots> g_rec;
        static std::array<ReplayState, kMaxSlots> g_rep;

        static void InvalidateReplayWeaponCache(ReplayState &p)
        {
            p.cachedWeaponDef.store(-1, std::memory_order_release);
            p.cachedWeaponServices.store(nullptr, std::memory_order_relaxed);
            p.cachedWeapon.store(nullptr, std::memory_order_relaxed);
            p.cachedWeaponEntIndex.store(-1, std::memory_order_relaxed);
            p.cachedWeaponSlot.store(-1, std::memory_order_relaxed);
            p.cachedWeaponPosition.store(0xFFFFFFFFu, std::memory_order_relaxed);
        }

        // Caller must hold p.mu and must have published playing=false first.
        static void ReleaseReplayVectors(ReplayState &p)
        {
            std::vector<ReplayTick>().swap(p.ticks);
            std::vector<SubtickMove>().swap(p.subs);
            std::vector<ReplayCommandFrameData>().swap(p.commands);
            std::vector<ReplayMovementExtra>().swap(p.movementExtras);
            std::vector<ReplayInputHistoryTick>().swap(p.inputHistoryTicks);
            std::vector<ReplayInputHistoryEntry>().swap(p.inputHistoryEntries);
            std::vector<size_t>().swap(p.inputHistoryOffset);
            std::vector<size_t>().swap(p.subOffset);
        }

        constexpr uint64_t kPrimeAttackButtons = (1ull << 0) | (1ull << 11);

        static std::array<int, kMaxSlots> g_lastFinalViewCursor = [] {
            std::array<int, kMaxSlots> values{};
            values.fill(-1);
            return values;
        }();
        struct ReplayPerfState
        {
            std::atomic<bool> enabled{false};
            std::atomic<uint64_t> processMovementHooks{0};
            std::atomic<uint64_t> finishMoveHooks{0};
            std::atomic<uint64_t> playerRunCommandHooks{0};
            std::atomic<uint64_t> physicsSimulateHooks{0};
            std::atomic<uint64_t> syncReplayLocalViewCalls{0};
            std::atomic<uint64_t> virtualQueryCalls{0};
            std::atomic<uint64_t> replayTickReads{0};
            std::atomic<uint64_t> subtickRebuilds{0};
            std::atomic<uint64_t> subticksAdded{0};
            std::atomic<uint64_t> replayCommandFrameReads{0};
            std::atomic<uint64_t> subtickClears{0};
            std::atomic<uint64_t> subtickNoopSkips{0};
            std::atomic<uint64_t> movementInputs{0};
            std::atomic<uint64_t> movementInitializations{0};
        };

        static ReplayPerfState g_perf;

        static bool ValidSlot(int s) { return s >= 0 && s < kMaxSlots; }
        static bool CanWriteMemory(void *ptr, size_t len);

        static void ClearReplayStopButtonResidue(void *services)
        {
            if (!services)
                return;

            WriteField(services, tg::kServices_Buttons, uint64_t{0});
            WriteField(services, tg::kServices_Buttons1, uint64_t{0});
            WriteField(services, tg::kServices_Buttons2, uint64_t{0});
            WriteField(services, tg::kServices_DesiresDuck, uint8_t{0});
        }

        static void FinalizeReplayStopState(int slot,
                                            void *services = nullptr)
        {
            // The takeover safety hook stops replay after the pawn is already
            // human-controlled. Retire our requests without rewriting its live
            // buttons, velocity or movement mode.
            if (InputInjector::IsSlotControllingBot(slot))
                return;
            if (!services)
                services = InputInjector::LiveMovementServices(slot);
            if (!services)
                return;

            void *pawn = InputInjector::ResolveReplayPawn(slot, services);
            uint32_t controllerHandle = 0;
            // The cached flag belongs to the original replay slot. On human
            // takeover only the new controller's flag may be set, while the
            // old slot still holds these movement services. Require the pawn's
            // current controller, never its original-controller fallback,
            // before touching either the pawn or its service-side buttons.
            if (!pawn || !SafeRead(pawn, tg::kPawn_Controller, controllerHandle) ||
                (controllerHandle & 0x7FFFu) != static_cast<uint32_t>(slot + 1))
                return;

            // Buttons and desire flags are replay-owned input state. Do not let
            // their down/changed edges leak into the first AI-controlled tick.
            ClearReplayStopButtonResidue(services);

            // Velocity, crouch transitions and ladder contact are engine state.
            // A retained ladder normal is also valid after leaving a ladder.
            // Releasing replay input must not manufacture a stand/land transition.
        }

        // End the execution once. A stopped replay may retain its buffer while
        // another provider owns this slot's inputs and equipment.
        static void EndReplayExecution(int slot, ReplayState &p,
                                       void *services = nullptr)
        {
            if (!p.playing.exchange(false, std::memory_order_acq_rel))
                return;

            FinalizeReplayStopState(slot, services);
            InputInjector::ClearUsercmdMovementIntent(slot);
            ReplayPawnEquipment::Clear(slot);
            p.holdBeforeCursor.store(-1, std::memory_order_relaxed);
            p.initializeMovement = false;
            InvalidateReplayWeaponCache(p);
            g_lastFinalViewCursor[slot] = -1;
        }

        static float NormalizeDeg(float a)
        {
            a = std::fmod(a + 180.0f, 360.0f);
            if (a < 0.0f)
                a += 360.0f;
            return a - 180.0f;
        }

        void AddReplayPerf(ReplayPerfCounter counter, uint64_t amount)
        {
            if (amount == 0 || !g_perf.enabled.load(std::memory_order_relaxed))
                return;

            switch (counter)
            {
            case ReplayPerfCounter::ProcessMovementHook:
                g_perf.processMovementHooks.fetch_add(amount, std::memory_order_relaxed);
                break;
            case ReplayPerfCounter::FinishMoveHook:
                g_perf.finishMoveHooks.fetch_add(amount, std::memory_order_relaxed);
                break;
            case ReplayPerfCounter::PlayerRunCommandHook:
                g_perf.playerRunCommandHooks.fetch_add(amount, std::memory_order_relaxed);
                break;
            case ReplayPerfCounter::PhysicsSimulateHook:
                g_perf.physicsSimulateHooks.fetch_add(amount, std::memory_order_relaxed);
                break;
            case ReplayPerfCounter::SyncReplayLocalView:
                g_perf.syncReplayLocalViewCalls.fetch_add(amount, std::memory_order_relaxed);
                break;
            case ReplayPerfCounter::VirtualQuery:
                g_perf.virtualQueryCalls.fetch_add(amount, std::memory_order_relaxed);
                break;
            case ReplayPerfCounter::ReplayTickRead:
                g_perf.replayTickReads.fetch_add(amount, std::memory_order_relaxed);
                break;
            case ReplayPerfCounter::SubtickRebuild:
                g_perf.subtickRebuilds.fetch_add(amount, std::memory_order_relaxed);
                break;
            case ReplayPerfCounter::SubticksAdded:
                g_perf.subticksAdded.fetch_add(amount, std::memory_order_relaxed);
                break;
            case ReplayPerfCounter::ReplayCommandFrameRead:
                g_perf.replayCommandFrameReads.fetch_add(amount, std::memory_order_relaxed);
                break;
            case ReplayPerfCounter::SubtickClear:
                g_perf.subtickClears.fetch_add(amount, std::memory_order_relaxed);
                break;
            case ReplayPerfCounter::SubtickNoopSkip:
                g_perf.subtickNoopSkips.fetch_add(amount, std::memory_order_relaxed);
                break;
            case ReplayPerfCounter::ReplayMovementInput:
                g_perf.movementInputs.fetch_add(amount, std::memory_order_relaxed);
                break;
            case ReplayPerfCounter::ReplayMovementInitialization:
                g_perf.movementInitializations.fetch_add(amount, std::memory_order_relaxed);
                break;
            }
        }

        void SetReplayPerfEnabled(bool enabled)
        {
            g_perf.enabled.store(enabled, std::memory_order_relaxed);
        }

        bool ReplayPerfEnabled()
        {
            return g_perf.enabled.load(std::memory_order_relaxed);
        }

        void ResetReplayPerfCounters()
        {
            g_perf.processMovementHooks.store(0, std::memory_order_relaxed);
            g_perf.finishMoveHooks.store(0, std::memory_order_relaxed);
            g_perf.playerRunCommandHooks.store(0, std::memory_order_relaxed);
            g_perf.physicsSimulateHooks.store(0, std::memory_order_relaxed);
            g_perf.syncReplayLocalViewCalls.store(0, std::memory_order_relaxed);
            g_perf.virtualQueryCalls.store(0, std::memory_order_relaxed);
            g_perf.replayTickReads.store(0, std::memory_order_relaxed);
            g_perf.subtickRebuilds.store(0, std::memory_order_relaxed);
            g_perf.subticksAdded.store(0, std::memory_order_relaxed);
            g_perf.replayCommandFrameReads.store(0, std::memory_order_relaxed);
            g_perf.subtickClears.store(0, std::memory_order_relaxed);
            g_perf.subtickNoopSkips.store(0, std::memory_order_relaxed);
            g_perf.movementInputs.store(0, std::memory_order_relaxed);
            g_perf.movementInitializations.store(0, std::memory_order_relaxed);
        }

        ReplayPerfCounters GetReplayPerfCounters()
        {
            return ReplayPerfCounters{
                g_perf.processMovementHooks.load(std::memory_order_relaxed),
                g_perf.finishMoveHooks.load(std::memory_order_relaxed),
                g_perf.playerRunCommandHooks.load(std::memory_order_relaxed),
                g_perf.physicsSimulateHooks.load(std::memory_order_relaxed),
                g_perf.syncReplayLocalViewCalls.load(std::memory_order_relaxed),
                g_perf.virtualQueryCalls.load(std::memory_order_relaxed),
                g_perf.replayTickReads.load(std::memory_order_relaxed),
                g_perf.subtickRebuilds.load(std::memory_order_relaxed),
                g_perf.subticksAdded.load(std::memory_order_relaxed),
                g_perf.replayCommandFrameReads.load(std::memory_order_relaxed),
                g_perf.subtickClears.load(std::memory_order_relaxed),
                g_perf.subtickNoopSkips.load(std::memory_order_relaxed),
                g_perf.movementInputs.load(std::memory_order_relaxed),
                g_perf.movementInitializations.load(std::memory_order_relaxed),
            };
        }

        static void *ResolveSceneNode(char *entity)
        {
            if (!entity)
                return nullptr;

#if defined(_WIN32)
            if (tg::kEnt_BodyComponent > 0 && tg::kBody_SceneNode >= 0)
            {
                void *body = nullptr;
                if (SafeRead(entity, tg::kEnt_BodyComponent, body) && body)
                {
                    void *node = nullptr;
                    if (SafeRead(body, tg::kBody_SceneNode, node) && node)
                        return node;
                }
            }

            if (tg::kEnt_GameSceneNode > 0)
            {
                void *node = nullptr;
                if (SafeRead(entity, tg::kEnt_GameSceneNode, node))
                    return node;
            }
#else
            if (tg::kEnt_BodyComponent > 0 && tg::kBody_SceneNode >= 0 &&
                CanWriteMemory(entity + tg::kEnt_BodyComponent, sizeof(void *)))
            {
                void *body = *reinterpret_cast<void **>(entity + tg::kEnt_BodyComponent);
                if (body &&
                    CanWriteMemory(reinterpret_cast<char *>(body) + tg::kBody_SceneNode,
                                   sizeof(void *)))
                {
                    void *node = *reinterpret_cast<void **>(
                        reinterpret_cast<char *>(body) + tg::kBody_SceneNode);
                    if (node)
                        return node;
                }
            }

            if (tg::kEnt_GameSceneNode > 0 &&
                CanWriteMemory(entity + tg::kEnt_GameSceneNode, sizeof(void *)))
            {
                return *reinterpret_cast<void **>(entity + tg::kEnt_GameSceneNode);
            }
#endif
            return nullptr;
        }

        // Read a MovementSnapshot from live engine state (services -> pawn).
        static bool ReadSnapshot(int slot, void *services, MovementSnapshot &out)
        {
            if (!services)
                return false;
            void *pawn = InputInjector::ResolveReplayPawn(slot, services);
            if (!pawn)
                return false;

            MovementSnapshot value = out;
            std::array<float, 3> velocity{};
            std::array<float, 3> ladderNormal{};
            std::array<float, 3> viewAngles{};
            if (!SafeRead(pawn, tg::kEnt_AbsVelocity, velocity) ||
                !SafeRead(pawn, tg::kEnt_Flags, value.entityFlags) ||
                !SafeRead(pawn, tg::kEnt_MoveType, value.moveType) ||
                !SafeRead(pawn, tg::kEnt_ActualMoveType, value.actualMoveType) ||
                !SafeRead(services, tg::kServices_Buttons, value.buttons) ||
                !SafeRead(services, tg::kServices_Buttons1, value.buttons1) ||
                !SafeRead(services, tg::kServices_Buttons2, value.buttons2) ||
                !SafeRead(services, tg::kServices_DuckAmount, value.duckAmount) ||
                !SafeRead(services, tg::kServices_DuckSpeed, value.duckSpeed) ||
                !SafeRead(services, tg::kServices_LadderNormal, ladderNormal) ||
                !SafeRead(services, tg::kServices_Ducked, value.ducked) ||
                !SafeRead(services, tg::kServices_Ducking, value.ducking) ||
                !SafeRead(services, tg::kServices_DesiresDuck, value.desiresDuck) ||
                !SafeRead(pawn, tg::kPawn_ViewAngle, viewAngles))
                return false;

            value.velX = velocity[0];
            value.velY = velocity[1];
            value.velZ = velocity[2];
            value.ladderNormalX = ladderNormal[0];
            value.ladderNormalY = ladderNormal[1];
            value.ladderNormalZ = ladderNormal[2];
            value.pitch = viewAngles[0];
            value.yaw = viewAngles[1];
            value.roll = viewAngles[2];

            void *node = ResolveSceneNode(reinterpret_cast<char *>(pawn));
            if (node)
            {
                std::array<float, 3> origin{};
                if (!SafeRead(node, tg::kNode_AbsOrigin, origin))
                    return false;
                value.originX = origin[0];
                value.originY = origin[1];
                value.originZ = origin[2];
            }
            out = value;
            return true;
        }

        // ---- recording ----

        bool StartRecord(int slot)
        {
            if (!ValidSlot(slot))
                return false;
            RecordState &r = g_rec[slot];
            {
                std::lock_guard<std::mutex> lk(r.mu);
                r.ticks.clear();
                r.subs.clear();
                r.pendingSubs.clear();
                r.havePre = false;
                r.commands.clear();
                r.pendingCommand = {};
                r.ticks.reserve(4096); // ~64s @ 64 tick
                r.subs.reserve(4096);
            }
            r.currentDef.store(-1, std::memory_order_relaxed);
            r.liveWs.store(nullptr, std::memory_order_relaxed);
            r.recording.store(true, std::memory_order_release);
            return true;
        }

        bool StopRecord(int slot)
        {
            if (!ValidSlot(slot))
                return false;
            g_rec[slot].recording.store(false, std::memory_order_release);
            return true;
        }

        bool ClearRecordedMotion(int slot)
        {
            if (!ValidSlot(slot)) return false;
            auto &record = g_rec[slot];
            record.recording.store(false, std::memory_order_release);
            std::lock_guard lock(record.mu);
            std::vector<ReplayTick>().swap(record.ticks);
            std::vector<SubtickMove>().swap(record.subs);
            std::vector<SubtickMove>().swap(record.pendingSubs);
            std::vector<ReplayCommandFrameData>().swap(record.commands);
            record.pendingCommand = {};
            record.havePre = false;
            record.currentDef.store(-1, std::memory_order_relaxed);
            record.liveWs.store(nullptr, std::memory_order_relaxed);
            return true;
        }

        bool IsRecording(int slot)
        {
            return ValidSlot(slot) &&
                   g_rec[slot].recording.load(std::memory_order_acquire);
        }

        int RecordedTickCount(int slot)
        {
            if (!ValidSlot(slot))
                return -1;
            RecordState &r = g_rec[slot];
            std::lock_guard<std::mutex> lk(r.mu);
            return static_cast<int>(r.ticks.size());
        }

        int RecordedSubtickCount(int slot)
        {
            if (!ValidSlot(slot))
                return -1;
            RecordState &r = g_rec[slot];
            std::lock_guard<std::mutex> lk(r.mu);
            return static_cast<int>(r.subs.size());
        }

        void SetLiveWs(int slot, void *ws)
        {
            if (ValidSlot(slot))
            {
                g_rec[slot].liveWs.store(ws, std::memory_order_relaxed);
            }
        }

        void *LiveWs(int slot)
        {
            return ValidSlot(slot)
                       ? g_rec[slot].liveWs.load(std::memory_order_relaxed)
                       : nullptr;
        }

        void SetCurrentDef(int slot, int defIndex)
        {
            if (ValidSlot(slot))
                g_rec[slot].currentDef.store(defIndex, std::memory_order_relaxed);
        }

        void OnCapturePre(int slot, void *services, void *cmd)
        {
            (void)cmd;
            if (!ValidSlot(slot) || !services)
                return;
            RecordState &r = g_rec[slot];
            if (!r.recording.load(std::memory_order_acquire))
                return;
            MovementSnapshot pre{};
            if (!ReadSnapshot(slot, services, pre))
                return;
            std::lock_guard<std::mutex> lk(r.mu);
            r.pendingPre = pre;
            r.havePre = true;
        }

        void OnCaptureSubticks(int slot, const SubtickMove *moves, int count)
        {
            if (!ValidSlot(slot) || count < 0)
                return;
            RecordState &r = g_rec[slot];
            if (!r.recording.load(std::memory_order_acquire))
                return;
            if (count > kMaxSubtickPerTick)
                count = kMaxSubtickPerTick;
            std::lock_guard<std::mutex> lk(r.mu);
            r.pendingSubs.clear();
            for (int i = 0; i < count; ++i)
                r.pendingSubs.push_back(moves[i]);
        }

        void OnCapturePost(int slot, void *services, void *cmd)
        {
            // cmd is actually the CMoveData* (hook passes moveData here)
            if (!ValidSlot(slot) || !services)
                return;
            RecordState &r = g_rec[slot];
            if (!r.recording.load(std::memory_order_acquire))
                return;

            MovementSnapshot post{};
            if (!ReadSnapshot(slot, services, post))
                return;

            if (cmd)
            {
                std::array<float, 3> origin{};
                if (!SafeRead(cmd, tg::kMove_AbsOrigin, origin))
                    return;
                post.originX = origin[0];
                post.originY = origin[1];
                post.originZ = origin[2];
            }

            // Active weapon def for this tick.
            void *ws = r.liveWs.load(std::memory_order_relaxed);
            int def = WeaponLockerHooks::ActiveWeaponDef(ws);
            if (def < 0)
                def = r.currentDef.load(std::memory_order_relaxed);

            {
                std::lock_guard<std::mutex> lk(r.mu);
                ReplayTick t{};
                t.pre = r.havePre ? r.pendingPre : post;
                t.post = post;
                t.weaponDefIndex = def;
                t.numSubtick = static_cast<uint32_t>(r.pendingSubs.size());
                for (const auto &sm : r.pendingSubs)
                    r.subs.push_back(sm);
                r.ticks.push_back(t);
                r.commands.push_back(r.pendingCommand);
                r.pendingCommand = {};
                r.pendingSubs.clear();
                r.havePre = false;
            }
        }

        int CopyTicks(int slot, ReplayTick *out, int maxTicks)
        {
            if (!ValidSlot(slot) || !out || maxTicks <= 0)
                return 0;
            RecordState &r = g_rec[slot];
            std::lock_guard<std::mutex> lk(r.mu);
            int n = static_cast<int>(r.ticks.size());
            if (n > maxTicks)
                n = maxTicks;
            for (int i = 0; i < n; ++i)
                out[i] = r.ticks[i];
            return n;
        }

        int CopySubticks(int slot, SubtickMove *out, int maxSubticks)
        {
            if (!ValidSlot(slot) || !out || maxSubticks <= 0)
                return 0;
            RecordState &r = g_rec[slot];
            std::lock_guard<std::mutex> lk(r.mu);
            int n = static_cast<int>(r.subs.size());
            if (n > maxSubticks)
                n = maxSubticks;
            for (int i = 0; i < n; ++i)
                out[i] = r.subs[i];
            return n;
        }

        void OnCaptureCommand(int slot, const ReplayCommandFrameData &command)
        {
            if (!ValidSlot(slot)) return;
            auto &record = g_rec[slot];
            std::lock_guard lock(record.mu);
            if (record.recording.load(std::memory_order_acquire)) record.pendingCommand = command;
        }

        int RecordedCommandCount(int slot)
        {
            if (!ValidSlot(slot)) return -1;
            auto &record = g_rec[slot];
            std::lock_guard lock(record.mu);
            return static_cast<int>(record.commands.size());
        }

        int CopyCommands(int slot, ReplayCommandFrameData *out, int maxCommands)
        {
            if (!ValidSlot(slot) || !out || maxCommands <= 0) return 0;
            auto &record = g_rec[slot];
            std::lock_guard lock(record.mu);
            const int count = std::min(maxCommands, static_cast<int>(record.commands.size()));
            std::copy_n(record.commands.begin(), count, out);
            return count;
        }

        // ---- replay ----

        static const ReplayTick *CurrentReplayTickPtr(ReplayState &p, int &cur, int &total)
        {
            total = static_cast<int>(p.ticks.size());
            cur = p.cursor.load(std::memory_order_relaxed);
            if (cur < 0 || cur >= total)
                return nullptr;
            AddReplayPerf(ReplayPerfCounter::ReplayTickRead);
            return &p.ticks[static_cast<size_t>(cur)];
        }

        static uint64_t ReplayPressedButtonsForPreStartTick(const ReplayState &p, int index)
        {
            if (index < 0 || index >= static_cast<int>(p.ticks.size()))
                return 0;

            const MovementSnapshot &pre = p.ticks[static_cast<size_t>(index)].pre;
            if (pre.buttons1 != 0 || pre.buttons2 != 0)
                return ButtonState::Decode(
                           pre.buttons, pre.buttons1, pre.buttons2)
                    .pressed;

            // Do not infer a press from the first stored context tick. If it is
            // already held there, the hold may have begun before the bounded
            // freeze-time window.
            if (index == 0)
                return 0;

            const uint64_t heldPrev =
                p.ticks[static_cast<size_t>(index - 1)].pre.buttons;
            return pre.buttons & ~heldPrev;
        }

        static uint64_t ReplayPrimeAttackButtonsForStart(
            const ReplayState &p,
            int cur,
            uint64_t heldButtons,
            uint64_t pressButtons)
        {
            const int start = p.startCursor.load(std::memory_order_relaxed);
            if (cur != start || start <= 0)
                return 0;

            const uint64_t candidates =
                heldButtons & kPrimeAttackButtons & ~pressButtons;
            if (candidates == 0)
                return 0;

            uint64_t found = 0;
            for (int i = 0; i < start; ++i)
            {
                found |= ReplayPressedButtonsForPreStartTick(p, i) & candidates;
                if ((found & candidates) == candidates)
                    break;
            }
            return found & candidates;
        }

        static bool ReplayWeaponMatchesRecordedDef(void *weapon,
                                                   int engineSlot,
                                                   int recordedDef)
        {
            const int liveDef = WeaponLockerHooks::ReadDefIndex(weapon);
            if (liveDef < 0)
                return false;
            if (recordedDef == WeaponLockerHooks::kKnifeDef)
                return engineSlot == 2 && liveDef != 31;
            return liveDef == recordedDef;
        }

        static int ReplayWeaponSelectForDef(int slot, int recordedDef)
        {
            if (!ValidSlot(slot) || recordedDef < 0 ||
                !WeaponLockerHooks::WeaponHooksReady())
                return -1;

            ReplayState &p = g_rep[slot];
            void *ws = WeaponLockerHooks::WsForSlot(slot);
            if (!ws)
            {
                InvalidateReplayWeaponCache(p);
                return -1;
            }

            if (p.cachedWeaponDef.load(std::memory_order_acquire) == recordedDef &&
                p.cachedWeaponServices.load(std::memory_order_relaxed) == ws)
            {
                void *cachedWeapon =
                    p.cachedWeapon.load(std::memory_order_relaxed);
                const int cachedEntIndex =
                    p.cachedWeaponEntIndex.load(std::memory_order_relaxed);
                const int cachedSlot =
                    p.cachedWeaponSlot.load(std::memory_order_relaxed);
                const unsigned int cachedPosition =
                    p.cachedWeaponPosition.load(std::memory_order_relaxed);
                void *currentWeapon =
                    WeaponLockerHooks::WeaponAtInventoryPosition(
                        ws, cachedSlot, cachedPosition);
                if (cachedWeapon && currentWeapon == cachedWeapon &&
                    cachedEntIndex >= 0 &&
                    WeaponLockerHooks::WeaponEntIndex(currentWeapon) ==
                        cachedEntIndex &&
                    ReplayWeaponMatchesRecordedDef(
                        currentWeapon, cachedSlot, recordedDef))
                {
                    return WeaponLockerHooks::ActiveWeaponEntIndex(ws) ==
                                   cachedEntIndex
                               ? -1
                               : cachedEntIndex;
                }

                // Give/drop/replacement, in-place def mutation, entity reuse,
                // or grenade-position change. Fall through to a full lookup
                // immediately; do not negative-cache.
                InvalidateReplayWeaponCache(p);
            }

            int engineSlot = -1;
            unsigned int position = 0xFFFFFFFFu;
            void *weapon = WeaponLockerHooks::FindWeaponByDef(
                ws, recordedDef, &engineSlot, &position);
            if (!weapon)
                return -1;
            const int weaponEntIndex = WeaponLockerHooks::WeaponEntIndex(weapon);
            if (weaponEntIndex < 0)
                return -1;

            p.cachedWeaponServices.store(ws, std::memory_order_relaxed);
            p.cachedWeapon.store(weapon, std::memory_order_relaxed);
            p.cachedWeaponEntIndex.store(weaponEntIndex, std::memory_order_relaxed);
            p.cachedWeaponSlot.store(engineSlot, std::memory_order_relaxed);
            p.cachedWeaponPosition.store(position, std::memory_order_relaxed);
            p.cachedWeaponDef.store(recordedDef, std::memory_order_release);

            return WeaponLockerHooks::ActiveWeaponEntIndex(ws) == weaponEntIndex
                       ? -1
                       : weaponEntIndex;
        }

        bool LoadReplay(int slot, const ReplayTick *ticks, int tickCount,
                        const SubtickMove *subs, int subCount) noexcept
        {
            return LoadReplayExtended(slot, ticks, tickCount, subs, subCount,
                                      nullptr, 0, nullptr, 0);
        }

        bool LoadReplayExtended(int slot, const ReplayTick *ticks, int tickCount,
                                const SubtickMove *subs, int subCount,
                                const ReplayCommandFrameData *commands,
                                int commandCount,
                                const ReplayMovementExtra *movementExtras,
                                int movementExtraCount) noexcept
        {
            return LoadReplayWithInputHistory(
                slot, ticks, tickCount, subs, subCount,
                commands, commandCount, movementExtras, movementExtraCount,
                nullptr, 0, nullptr, 0);
        }

        bool LoadReplayWithInputHistory(
            int slot, const ReplayTick *ticks, int tickCount,
            const SubtickMove *subs, int subCount,
            const ReplayCommandFrameData *commands, int commandCount,
            const ReplayMovementExtra *movementExtras, int movementExtraCount,
            const ReplayInputHistoryTick *inputHistoryTicks, int inputHistoryTickCount,
            const ReplayInputHistoryEntry *inputHistoryEntries, int inputHistoryEntryCount) noexcept
        {
            bool committed = false;
            try
            {
                if (!ValidSlot(slot))
                    return false;

                ReplayState &p = g_rep[slot];
                if (p.playing.load(std::memory_order_acquire))
                    return false;

                ReplaySubtickLayout::ReplayLoadStaging staged;
                if (!ReplaySubtickLayout::TryStageReplayLoad(
                        ticks, tickCount, subs, subCount,
                        commands, commandCount,
                        movementExtras, movementExtraCount,
                        inputHistoryTicks, inputHistoryTickCount,
                        inputHistoryEntries, inputHistoryEntryCount,
                        staged))
                {
                    return false;
                }

                std::lock_guard<std::mutex> lk(p.mu);
                if (p.playing.load(std::memory_order_acquire))
                    return false; // don't swap frames mid-playback

                p.ticks.swap(staged.ticks);
                p.subs.swap(staged.subs);
                p.commands.swap(staged.commands);
                p.movementExtras.swap(staged.movementExtras);
                p.inputHistoryTicks.swap(staged.inputHistoryTicks);
                p.inputHistoryEntries.swap(staged.inputHistoryEntries);
                p.inputHistoryOffset.swap(staged.inputHistoryOffsets);
                p.subOffset.swap(staged.offsets);
                committed = true;
                p.cursor.store(0, std::memory_order_relaxed);
                p.startCursor.store(0, std::memory_order_relaxed);
                p.holdBeforeCursor.store(-1, std::memory_order_relaxed);
                InvalidateReplayWeaponCache(p);
                g_lastFinalViewCursor[slot] = -1;
                InputInjector::ClearReplayPawn(slot);
                return true;
            }
            catch (...)
            {
                // All buffer swaps above are noexcept. If a future post-commit
                // cleanup gains a throwing operation, do not report failure
                // after the new replay has already become authoritative.
                return committed;
            }
        }

        bool StartReplay(int slot, bool loop)
        {
            return StartReplayAt(slot, loop, 0);
        }

        bool StartReplayAt(int slot, bool loop, int startIndex)
        {
            if (!ValidSlot(slot))
                return false;
            ReplayState &p = g_rep[slot];
            std::lock_guard<std::mutex> lk(p.mu);
            if (p.ticks.empty() || startIndex < 0 ||
                startIndex >= static_cast<int>(p.ticks.size()))
            {
                return false;
            }
            // Equipment initialization is best-effort here. A newly spawned
            // Pawn may not expose ItemServices until its first movement hook;
            // refusing to start would prevent that final one-shot pass.
            ReplayPawnEquipment::PrepareForReplayStart(slot);
            const bool resumesHeldReplay =
                p.playing.load(std::memory_order_acquire) &&
                p.holdBeforeCursor.load(std::memory_order_relaxed) == startIndex;
            p.cursor.store(startIndex, std::memory_order_relaxed);
            // Freeze pre-roll is one continuous replay. Keep its original
            // start cursor when releasing the hold so start-only input
            // synthesis cannot replay an old attack edge at live start.
            if (!resumesHeldReplay)
            {
                p.startCursor.store(startIndex, std::memory_order_relaxed);
                p.initializeMovement = true;
            }
            p.holdBeforeCursor.store(-1, std::memory_order_relaxed);
            InvalidateReplayWeaponCache(p);
            g_lastFinalViewCursor[slot] = -1;
            p.loop.store(loop, std::memory_order_relaxed);
            InputInjector::ClearUsercmdMovementIntent(slot);
            p.playing.store(true, std::memory_order_release);
            return true;
        }

        bool StartReplayUntil(int slot, bool loop, int startIndex, int holdBeforeIndex)
        {
            if (!ValidSlot(slot))
                return false;
            ReplayState &p = g_rep[slot];
            std::lock_guard<std::mutex> lk(p.mu);
            const int total = static_cast<int>(p.ticks.size());
            if (total <= 0 || startIndex < 0 || startIndex >= total ||
                holdBeforeIndex <= startIndex || holdBeforeIndex > total)
            {
                return false;
            }
            ReplayPawnEquipment::PrepareForReplayStart(slot);
            p.cursor.store(startIndex, std::memory_order_relaxed);
            p.startCursor.store(startIndex, std::memory_order_relaxed);
            p.holdBeforeCursor.store(holdBeforeIndex, std::memory_order_relaxed);
            p.initializeMovement = true;
            InvalidateReplayWeaponCache(p);
            g_lastFinalViewCursor[slot] = -1;
            p.loop.store(loop, std::memory_order_relaxed);
            InputInjector::ClearUsercmdMovementIntent(slot);
            p.playing.store(true, std::memory_order_release);
            return true;
        }

        bool StopReplay(int slot)
        {
            if (!ValidSlot(slot))
                return false;
            ReplayState &p = g_rep[slot];
            std::lock_guard<std::mutex> lk(p.mu);
            EndReplayExecution(slot, p);
            return true;
        }

        bool ReleaseReplayBuffer(int slot)
        {
            if (!ValidSlot(slot))
                return false;

            ReplayState &p = g_rep[slot];
            std::lock_guard<std::mutex> lk(p.mu);
            EndReplayExecution(slot, p);
            ReleaseReplayVectors(p);
            p.cursor.store(0, std::memory_order_relaxed);
            p.startCursor.store(0, std::memory_order_relaxed);
            p.holdBeforeCursor.store(-1, std::memory_order_relaxed);
            p.loop.store(false, std::memory_order_relaxed);
            InvalidateReplayWeaponCache(p);
            g_lastFinalViewCursor[slot] = -1;
            InputInjector::ClearReplayPawn(slot);
            return true;
        }

        bool IsReplaying(int slot)
        {
            return ValidSlot(slot) &&
                   g_rep[slot].playing.load(std::memory_order_acquire);
        }

        int ReplayCursor(int slot)
        {
            if (!ValidSlot(slot))
                return -1;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return -1;
            return p.cursor.load(std::memory_order_relaxed);
        }

        int ReplayTotal(int slot)
        {
            if (!ValidSlot(slot))
                return 0;
            ReplayState &p = g_rep[slot];
            std::lock_guard<std::mutex> lk(p.mu);
            return static_cast<int>(p.ticks.size());
        }

        bool GetReplaySlotState(int slot, ReplaySlotState &out)
        {
            out = ReplaySlotState{0, -1, 0, -1, -1, 0};
            if (!ValidSlot(slot))
                return false;

            ReplayState &p = g_rep[slot];
            const bool playing = p.playing.load(std::memory_order_acquire);
            if (!playing)
            {
                std::lock_guard<std::mutex> lk(p.mu);
                out.total = static_cast<int32_t>(p.ticks.size());
                // The aggregate state retains the terminal cursor so managed
                // consumers can distinguish natural completion from a stop.
                // GetReplayCursor keeps its legacy -1-when-idle contract.
                out.cursor = p.cursor.load(std::memory_order_relaxed);
                return true;
            }

            const int total = static_cast<int>(p.ticks.size());
            const int cursor = p.cursor.load(std::memory_order_relaxed);
            out.playing = 1;
            out.cursor = cursor;
            out.total = total;

            int idx = cursor - 1;
            if (idx < 0)
                idx = 0;
            if (idx < 0 || idx >= total)
                return true;

            const ReplayTick &tick = p.ticks[static_cast<size_t>(idx)];
            out.currentTickIndex = idx;
            out.weaponDefIndex = tick.weaponDefIndex;
            out.numSubtick = static_cast<int32_t>(tick.numSubtick);
            return true;
        }

        bool ReplayTickForSimulation(int slot, ReplayTick &out)
        {
            if (!ValidSlot(slot))
                return false;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return false;
            int cur = -1;
            int total = 0;
            const ReplayTick *tick = CurrentReplayTickPtr(p, cur, total);
            if (!tick)
                return false;
            size_t subtickBegin = 0;
            size_t subtickEnd = 0;
            if (!ReplaySubtickLayout::TryGetReplaySubtickRange(
                    p.ticks.data(), p.ticks.size(), p.subOffset, p.subs.size(),
                    static_cast<size_t>(cur), subtickBegin, subtickEnd))
            {
                return false;
            }
            out = *tick;
            return true;
        }

        bool ReplayCommandFrameForSimulation(int slot, ReplayCommandFrame &out)
        {
            out = ReplayCommandFrame{};
            if (!ValidSlot(slot))
                return false;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return false;

            int cur = -1;
            int total = 0;
            const ReplayTick *tick = CurrentReplayTickPtr(p, cur, total);
            if (!tick)
                return false;

            const MovementSnapshot &pre = tick->pre;
            uint64_t b0 = pre.buttons;
            uint64_t b1 = pre.buttons1;
            uint64_t b2 = pre.buttons2;

            const ReplayCommandFrameData *command = nullptr;
            if (cur >= 0 && static_cast<size_t>(cur) < p.commands.size())
                command = &p.commands[static_cast<size_t>(cur)];

            const ReplayInputHistoryTick *inputHistoryTick = nullptr;
            const ReplayInputHistoryEntry *inputHistory = nullptr;
            int32_t inputHistoryCount = 0;
            if (cur >= 0 && static_cast<size_t>(cur) < p.inputHistoryTicks.size() &&
                p.inputHistoryOffset.size() == p.inputHistoryTicks.size() + 1)
            {
                const size_t historyBegin = p.inputHistoryOffset[static_cast<size_t>(cur)];
                const size_t historyEnd = p.inputHistoryOffset[static_cast<size_t>(cur) + 1];
                if (historyBegin <= historyEnd && historyEnd <= p.inputHistoryEntries.size())
                {
                    inputHistoryTick = &p.inputHistoryTicks[static_cast<size_t>(cur)];
                    inputHistoryCount = static_cast<int32_t>(historyEnd - historyBegin);
                    if (inputHistoryCount > 0)
                        inputHistory = p.inputHistoryEntries.data() + historyBegin;
                }
            }

            const bool hasCommandButtons =
                command && ((command->fields & kCommandFieldButtons) != 0);
            if (hasCommandButtons)
            {
                b0 = command->buttons;
                b1 = command->buttons1;
                b2 = command->buttons2;
            }
            else if (b1 == 0 && b2 == 0)
            {
                uint64_t heldPrev = (cur > 0) ? p.ticks[static_cast<size_t>(cur - 1)].pre.buttons : 0;
                const ButtonState::Planes planes =
                    ButtonState::EncodeAdjacentHeld(b0, heldPrev);
                b1 = planes.state2;
                b2 = planes.state3;
            }
            if (!hasCommandButtons)
            {
                const uint64_t pressed = ButtonState::Decode(b0, b1, b2).pressed;
                b1 |= ReplayPrimeAttackButtonsForStart(p, cur, b0, pressed);
            }

            const SubtickMove *subticks = nullptr;
            size_t subtickBegin = 0;
            size_t subtickEnd = 0;
            if (cur < 0 ||
                !ReplaySubtickLayout::TryGetReplaySubtickRange(
                    p.ticks.data(), p.ticks.size(), p.subOffset, p.subs.size(),
                    static_cast<size_t>(cur), subtickBegin, subtickEnd))
            {
                return false;
            }
            const int subtickCount = static_cast<int>(subtickEnd - subtickBegin);
            subticks = subtickCount > 0 ? p.subs.data() + subtickBegin : nullptr;

            out.tick = tick;
            out.subticks = subticks;
            out.command = command;
            out.inputHistoryTick = inputHistoryTick;
            out.inputHistory = inputHistory;
            out.inputHistoryCount = inputHistoryCount;
            out.subtickCount = subtickCount;
            out.weaponSelect = ReplayWeaponSelectForDef(slot, tick->weaponDefIndex);
            out.commandView = tick->pre;
            if (command && ((command->fields & kCommandFieldViewAngles) != 0))
            {
                out.commandView.pitch = command->pitch;
                out.commandView.yaw = command->yaw;
                out.commandView.roll = command->roll;
            }
            out.buttons0 = b0;
            out.buttons1 = b1;
            out.buttons2 = b2;
            if (command)
            {
                out.commandFields = command->fields;
                if ((command->fields & kCommandFieldForwardMove) != 0)
                    out.forwardMove = command->forwardMove;
                if ((command->fields & kCommandFieldLeftMove) != 0)
                    out.leftMove = command->leftMove;
                if ((command->fields & kCommandFieldUpMove) != 0)
                    out.upMove = command->upMove;
                if ((command->fields & kCommandFieldMouse) != 0)
                {
                    out.mouseDx = command->mouseDx;
                    out.mouseDy = command->mouseDy;
                }
                if ((command->fields & kCommandFieldWeaponSelect) != 0)
                    out.rawWeaponSelect = command->weaponSelect;
                if ((command->fields & kCommandFieldLeftHand) != 0)
                    out.leftHandDesired = command->leftHandDesired;
            }
            AddReplayPerf(ReplayPerfCounter::ReplayCommandFrameRead);
            return true;
        }

        bool ReplaySpectatorView(int slot, MovementSnapshot &out)
        {
            if (!ValidSlot(slot))
                return false;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return false;
            int total = static_cast<int>(p.ticks.size());
            if (total <= 0)
                return false;

            const int cur = p.cursor.load(std::memory_order_relaxed);
            const int lastFinal = g_lastFinalViewCursor[slot];
            int idx = -1;

            if (lastFinal >= 0 && lastFinal < total &&
                (lastFinal == cur || lastFinal + 1 == cur || cur >= total))
            {
                idx = lastFinal;
            }
            else if (cur >= 0 && cur < total)
            {
                idx = cur;
            }
            else if (lastFinal >= 0 && lastFinal < total)
            {
                idx = lastFinal;
            }

            if (idx < 0 || idx >= total)
                return false;

            AddReplayPerf(ReplayPerfCounter::ReplayTickRead);
            out = p.ticks[static_cast<size_t>(idx)].post;
            return true;
        }

        // Public status API: cursor points at the next tick, so the last
        // applied tick is cursor - 1. Clamp at 0 during the opening tick.
        bool CurrentReplayTick(int slot, ReplayTick &out)
        {
            if (!ValidSlot(slot))
                return false;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return false;
            int total = static_cast<int>(p.ticks.size());
            int idx = p.cursor.load(std::memory_order_relaxed) - 1;
            if (idx < 0)
                idx = 0;
            if (idx >= total)
                return false;
            AddReplayPerf(ReplayPerfCounter::ReplayTickRead);
            out = p.ticks[static_cast<size_t>(idx)];
            return true;
        }

        int CurrentReplaySubticks(int slot, SubtickMove *out, int maxOut)
        {
            if (!ValidSlot(slot) || !out || maxOut <= 0)
                return -1;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return -1;
            int total = static_cast<int>(p.ticks.size());
            int idx = p.cursor.load(std::memory_order_relaxed);
            if (idx < 0 || idx >= total)
                return -1;
            size_t begin = 0;
            size_t end = 0;
            if (!ReplaySubtickLayout::TryGetReplaySubtickRange(
                    p.ticks.data(), p.ticks.size(), p.subOffset, p.subs.size(),
                    static_cast<size_t>(idx), begin, end))
            {
                return -1;
            }
            int n = static_cast<int>(end - begin);
            if (n > maxOut)
                n = maxOut;
            for (int i = 0; i < n; ++i)
                out[i] = p.subs[begin + static_cast<size_t>(i)];
            return n;
        }

        bool CurrentReplayInputButtons(int slot, uint64_t &b0, uint64_t &b1,
                                       uint64_t &b2)
        {
            if (!ValidSlot(slot))
                return false;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return false;
            int cur = -1;
            int total = 0;
            const ReplayTick *tick = CurrentReplayTickPtr(p, cur, total);
            if (!tick)
                return false;
            const MovementSnapshot &pre = tick->pre;
            b0 = pre.buttons;
            b1 = pre.buttons1;
            b2 = pre.buttons2;
            if (b1 == 0 && b2 == 0)
            {
                // Older offline records only stored the held mask. Keep them
                // playable by synthesizing a canonical three-plane transition
                // from adjacent ticks.
                uint64_t heldPrev = (cur > 0) ? p.ticks[static_cast<size_t>(cur - 1)].pre.buttons : 0;
                const ButtonState::Planes planes =
                    ButtonState::EncodeAdjacentHeld(b0, heldPrev);
                b1 = planes.state2;
                b2 = planes.state3;
            }
            const uint64_t pressed = ButtonState::Decode(b0, b1, b2).pressed;
            b1 |= ReplayPrimeAttackButtonsForStart(p, cur, b0, pressed);
            return true;
        }

        bool SwitchBotWeaponByDef(int slot, int defIndex)
        {
            if (!ValidSlot(slot) || defIndex < 0)
                return false;
            InvalidateReplayWeaponCache(g_rep[slot]);
            if (!WeaponLockerHooks::WeaponHooksReady())
                return false;
            void *ws = WeaponLockerHooks::WsForSlot(slot);
            if (!ws)
                return false;
            void *weapon = WeaponLockerHooks::FindWeaponByDef(ws, defIndex);
            if (!weapon)
                return false;
            return WeaponLockerHooks::SelectWeaponRaw(ws, weapon);
        }

        // Def index of the bot's current active weapon
        int BotActiveWeaponDef(int slot)
        {
            if (!ValidSlot(slot) || !WeaponLockerHooks::WeaponHooksReady())
                return -1;
            void *ws = WeaponLockerHooks::WsForSlot(slot);
            if (!ws)
                return -1;
            return WeaponLockerHooks::ActiveWeaponDef(ws);
        }

        // Entity index for cmd.weaponselect this replay tick
        int CurrentReplayWeaponSelect(int slot)
        {
            if (!ValidSlot(slot))
                return -1;

            // Recorded def for the tick about to be simulated
            int recordedDef;
            {
                ReplayState &p = g_rep[slot];
                if (!p.playing.load(std::memory_order_acquire))
                    return -1;
                int cur = -1;
                int total = 0;
                const ReplayTick *tick = CurrentReplayTickPtr(p, cur, total);
                if (!tick)
                    return -1;
                recordedDef = tick->weaponDefIndex;
            }
            return ReplayWeaponSelectForDef(slot, recordedDef);
        }

        // Only simulation-local angles belong to replay. The engine updates
        // m_angEyeAngles and its dirty state by reading our getter after FinishMove.
        static void WriteLocalViewAnglesToPawn(char *p, float pitch, float yaw)
        {
            const float normalizedYaw = NormalizeDeg(yaw);
            *reinterpret_cast<float *>(p + tg::kPawn_ViewAngle + 0) = pitch;
            *reinterpret_cast<float *>(p + tg::kPawn_ViewAngle + 4) = normalizedYaw;
            *reinterpret_cast<float *>(p + tg::kPawn_ViewAngle + 8) = 0.0f;
        }

        static void WriteReplayViewHistory(void *services, char *pawn, float pitch, float yaw)
        {
            const float normalizedYaw = NormalizeDeg(yaw);
            *reinterpret_cast<float *>(pawn + tg::kPawn_ViewAnglePrevious + 0) = pitch;
            *reinterpret_cast<float *>(pawn + tg::kPawn_ViewAnglePrevious + 4) = normalizedYaw;
            *reinterpret_cast<float *>(pawn + tg::kPawn_ViewAnglePrevious + 8) = 0.0f;

            auto *sv = reinterpret_cast<char *>(services);
            *reinterpret_cast<float *>(sv + tg::kServices_OldViewAngles + 0) = pitch;
            *reinterpret_cast<float *>(sv + tg::kServices_OldViewAngles + 4) = normalizedYaw;
            *reinterpret_cast<float *>(sv + tg::kServices_OldViewAngles + 8) = 0.0f;
        }

        static bool CanWriteMemory(void *ptr, size_t len)
        {
            if (!ptr || len == 0)
                return false;

            MEMORY_BASIC_INFORMATION mbi{};
            AddReplayPerf(ReplayPerfCounter::VirtualQuery);
            if (VirtualQuery(ptr, &mbi, sizeof(mbi)) == 0)
                return false;
            if (mbi.State != MEM_COMMIT)
                return false;
            if (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS))
                return false;

            const DWORD writable =
                PAGE_READWRITE | PAGE_WRITECOPY |
                PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
            if ((mbi.Protect & writable) == 0)
                return false;

            const auto begin = reinterpret_cast<uintptr_t>(ptr);
            const auto end = begin + len;
            const auto regionEnd = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
            return end >= begin && end <= regionEnd;
        }

        static bool SyncReplayLocalView(int slot, void *services,
                                        const MovementSnapshot &s)
        {
            void *pawn = InputInjector::ResolveReplayPawn(slot, services);
            if (!pawn)
                return false;
            auto *p = reinterpret_cast<char *>(pawn);

            // Do not call SetEyeAngles: disguised BotHider controllers can
            // enter its absolute-correction path even though they are bots.
            WriteLocalViewAnglesToPawn(p, s.pitch, s.yaw);
            WriteReplayViewHistory(services, p, s.pitch, s.yaw);
            AddReplayPerf(ReplayPerfCounter::SyncReplayLocalView);
            return true;
        }

        // Write origin + velocity into CMoveData.
        static void WriteMoveData(void *moveData, const MovementSnapshot &s)
        {
            auto *m = reinterpret_cast<char *>(moveData);
            *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 0) = s.originX;
            *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 4) = s.originY;
            *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 8) = s.originZ;
            *reinterpret_cast<float *>(m + tg::kMove_Velocity + 0) = s.velX;
            *reinterpret_cast<float *>(m + tg::kMove_Velocity + 4) = s.velY;
            *reinterpret_cast<float *>(m + tg::kMove_Velocity + 8) = s.velZ;
        }

        static void WriteMovementServiceState(void *services,
                                              const MovementSnapshot &s)
        {
            auto *sv = reinterpret_cast<char *>(services);
            *reinterpret_cast<float *>(sv + tg::kServices_DuckAmount) = s.duckAmount;
            *reinterpret_cast<float *>(sv + tg::kServices_DuckSpeed) = s.duckSpeed;
            *reinterpret_cast<float *>(sv + tg::kServices_LadderNormal + 0) = s.ladderNormalX;
            *reinterpret_cast<float *>(sv + tg::kServices_LadderNormal + 4) = s.ladderNormalY;
            *reinterpret_cast<float *>(sv + tg::kServices_LadderNormal + 8) = s.ladderNormalZ;
            *reinterpret_cast<uint8_t *>(sv + tg::kServices_Ducked) = s.ducked;
            *reinterpret_cast<uint8_t *>(sv + tg::kServices_Ducking) = s.ducking;
            *reinterpret_cast<uint8_t *>(sv + tg::kServices_DesiresDuck) = s.desiresDuck;
        }

        bool OnReplayCommandPre(int slot, void *services, const ReplayTick &t,
                                const MovementSnapshot &commandView)
        {
            if (!ValidSlot(slot) || !services || !IsReplaying(slot))
                return false;
            void *pawn = InputInjector::ResolveReplayPawn(slot, services);
            if (!pawn)
                return false;

            ReplayState &p = g_rep[slot];
            auto *pp = reinterpret_cast<char *>(pawn);
            if (p.initializeMovement)
            {
                const float origin[] = {t.pre.originX, t.pre.originY, t.pre.originZ};
                const float velocity[] = {t.pre.velX, t.pre.velY, t.pre.velZ};
                if (!InputInjector::InitializeReplayPose(pawn, origin, velocity))
                    return false;
                // Boundary state only. Duck/ladder/ground processing owns all
                // subsequent transitions, including the state kept at handoff.
                WriteMovementServiceState(services, t.pre);
                *reinterpret_cast<uint8_t *>(pp + tg::kEnt_MoveType) = t.pre.moveType;
                *reinterpret_cast<uint8_t *>(pp + tg::kEnt_ActualMoveType) = t.pre.actualMoveType;
                const uint32_t mask = tg::kFL_OnGround | tg::kFL_Ducking;
                auto *flags = reinterpret_cast<uint32_t *>(pp + tg::kEnt_Flags);
                *flags = (*flags & ~mask) | (t.pre.entityFlags & mask);
                p.initializeMovement = false;
                AddReplayPerf(ReplayPerfCounter::ReplayMovementInitialization);
            }

            WriteLocalViewAnglesToPawn(pp, commandView.pitch, commandView.yaw);
            WriteReplayViewHistory(services, pp, commandView.pitch, commandView.yaw);
            return true;
        }

        void OnReplayCommandPost(int slot, void *services, bool wasReplaying)
        {
            if (!wasReplaying || !ValidSlot(slot) || !services)
                return;

            ReplayState &p = g_rep[slot];
            if (p.playing.load(std::memory_order_acquire))
                return;

            std::lock_guard<std::mutex> lk(p.mu);
            if (!p.playing.load(std::memory_order_acquire))
                FinalizeReplayStopState(slot, services);
        }

        // The demo pre snapshot is the kinematic input for this command.
        // Supply it once after SetupMove; do not reset every subtick mover or
        // write pawn origins ahead of the engine's change detection.
        void OnReplaySetupMove(int slot, void *moveData)
        {
            if (!ValidSlot(slot) || !moveData || !IsReplaying(slot))
                return;
            ReplayState &p = g_rep[slot];
            if (p.initializeMovement)
                return; // wait for the first prepared PlayerRunCommand
            int cursor = -1;
            int total = 0;
            const ReplayTick *t = CurrentReplayTickPtr(p, cursor, total);
            if (t)
            {
                WriteMoveData(moveData, t->pre);
                AddReplayPerf(ReplayPerfCounter::ReplayMovementInput);
            }
        }

        // Prepare the local post view and getter before the engine compares
        // and publishes m_angEyeAngles in the PlayerRunCommand tail. Keep this
        // before the later PhysicsSimulate cursor advance.
        void OnReplayFinalView(int slot, void *services)
        {
            if (!ValidSlot(slot) || !services)
                return;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire) || p.initializeMovement)
                return;
            int cur = -1;
            int total = 0;
            const ReplayTick *t = CurrentReplayTickPtr(p, cur, total);
            if (!t)
                return;

            SyncReplayLocalView(slot, services, t->post);
            g_lastFinalViewCursor[slot] = cur;
        }

        // PhysicsSimulate-post (or PlayerRunCommand-post fallback): advance
        // the cursor after view publication; movement is engine-owned.
        void OnReplayCommit(int slot, void *services)
        {
            if (!ValidSlot(slot) || !services)
                return;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire) || p.initializeMovement)
                return;
            int cur = p.cursor.load(std::memory_order_relaxed);
            int total = static_cast<int>(p.ticks.size());
            if (cur < 0 || cur >= total)
            {
                EndReplayExecution(slot, p, services);
                return;
            }

            if (g_lastFinalViewCursor[slot] != cur)
            {
                AddReplayPerf(ReplayPerfCounter::ReplayTickRead);
                SyncReplayLocalView(slot, services, p.ticks[static_cast<size_t>(cur)].post);
            }

            const int holdBefore = p.holdBeforeCursor.load(std::memory_order_relaxed);
            if (holdBefore > 0 && cur + 1 >= holdBefore)
            {
                p.cursor.store(holdBefore - 1, std::memory_order_relaxed);
                return;
            }
            const int next = cur + 1;
            if (next >= total && p.loop.load(std::memory_order_relaxed))
            {
                // The next command must already have a valid input frame.
                // Do not run an unowned engine command between loop passes.
                p.cursor.store(p.startCursor.load(std::memory_order_relaxed),
                               std::memory_order_relaxed);
                p.initializeMovement = true;
                InvalidateReplayWeaponCache(p);
                g_lastFinalViewCursor[slot] = -1;
                return;
            }
            p.cursor.store(next, std::memory_order_relaxed);
            if (next >= total)
            {
                EndReplayExecution(slot, p, services);
            }
        }

        void ClearAll()
        {
            for (int i = 0; i < kMaxSlots; ++i)
            {
                ClearRecordedMotion(i);
                ReleaseReplayBuffer(i);
            }
            InputInjector::ClearAllUsercmdMovementIntents();
            ReplayPawnEquipment::ClearAll();
        }
    }
}
