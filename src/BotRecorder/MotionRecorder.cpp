// Motion recording & replay implementation

#include "MotionRecorder.h"
#include "BotController.h"
#include "InputInjector.h"
#include "ReplaySubtickLayout.h"
#include "WeaponLocker.h"
#include "ccsbot_slot.h"
#include "version_targets.h"

#include <entity2/entityinstance.h>

#include <array>
#include <atomic>
#include <cmath>
#include <mutex>
#include <vector>

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

        static std::atomic<int> g_replaySnapMode{static_cast<int>(ReplaySnapMode::Hard)};
        static std::atomic<int> g_replayViewMode{static_cast<int>(ReplayViewMode::PostOnly)};
        static std::atomic<int> g_replayCmdViewMode{static_cast<int>(ReplayCommandViewMode::Pre)};
        static std::atomic<int> g_replayPovMode{static_cast<int>(ReplayPovMode::Spectated)};
        static std::atomic<uint64_t> g_replayPovMask{0};
        static std::array<uint32_t, kMaxSlots> g_serverViewChangeIndex = [] {
            std::array<uint32_t, kMaxSlots> values{};
            values.fill(0);
            return values;
        }();
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
            std::atomic<uint64_t> syncReplayViewCalls{0};
            std::atomic<uint64_t> serverViewWrites{0};
            std::atomic<uint64_t> virtualQueryCalls{0};
            std::atomic<uint64_t> replayTickReads{0};
            std::atomic<uint64_t> subtickRebuilds{0};
            std::atomic<uint64_t> subticksAdded{0};
            std::atomic<uint64_t> replayCommandFrameReads{0};
            std::atomic<uint64_t> subtickClears{0};
            std::atomic<uint64_t> subtickNoopSkips{0};
        };

        static ReplayPerfState g_perf;

        static constexpr float kSoftSnapDistance = 64.0f;
        static constexpr float kSoftSnapVerticalDistance = 48.0f;
        static constexpr float kFinishMoveResyncNudgeZ = 0.03125f;
        static constexpr float kReplayMinEngineVelZ = -500.0f;
        static constexpr uint8_t kMoveTypeWalk = 2;
        static constexpr uint8_t kMoveTypeLadder = 9;
        static constexpr float kLadderNormalResidueSq = 0.0001f;

        static bool ValidSlot(int s) { return s >= 0 && s < kMaxSlots; }
        static bool CanWriteMemory(void *ptr, size_t len);

        static bool HasLadderNormalResidue(float x, float y, float z)
        {
            return x * x + y * y + z * z > kLadderNormalResidueSq;
        }

        static bool HasLadderResidue(const MovementSnapshot &s)
        {
            return s.moveType == kMoveTypeLadder ||
                   s.actualMoveType == kMoveTypeLadder ||
                   HasLadderNormalResidue(s.ladderNormalX, s.ladderNormalY,
                                          s.ladderNormalZ);
        }

        static bool HasLadderResidue(const ReplayTick &t)
        {
            return HasLadderResidue(t.pre) || HasLadderResidue(t.post);
        }

        static bool ReplayStopPointHasLadderResidue(ReplayState &p)
        {
            const int total = static_cast<int>(p.ticks.size());
            if (total <= 0)
                return false;

            const int cur = p.cursor.load(std::memory_order_relaxed);
            if (cur >= 0 && cur < total &&
                HasLadderResidue(p.ticks[static_cast<size_t>(cur)]))
                return true;

            const int prev = cur - 1;
            return prev >= 0 && prev < total &&
                   HasLadderResidue(p.ticks[static_cast<size_t>(prev)]);
        }

        static bool LiveMovementHasLadderResidue(int slot, void *services)
        {
            if (!services)
                return false;
            void *pawn = InputInjector::ResolveReplayPawn(slot, services);
            if (!pawn)
                return false;

            uint8_t moveType = 0;
            uint8_t actualMoveType = 0;
            std::array<float, 3> ladderNormal{};
            if (!SafeRead(pawn, tg::kEnt_MoveType, moveType) ||
                !SafeRead(pawn, tg::kEnt_ActualMoveType, actualMoveType) ||
                !SafeRead(services, tg::kServices_LadderNormal, ladderNormal))
                return false;

            return moveType == kMoveTypeLadder ||
                   actualMoveType == kMoveTypeLadder ||
                   HasLadderNormalResidue(ladderNormal[0], ladderNormal[1],
                                          ladderNormal[2]);
        }

        static void ClearReplayStopButtonResidue(void *services)
        {
            if (!services)
                return;

            WriteField(services, tg::kServices_Buttons, uint64_t{0});
            WriteField(services, tg::kServices_Buttons1, uint64_t{0});
            WriteField(services, tg::kServices_Buttons2, uint64_t{0});
            WriteField(services, tg::kServices_DesiresDuck, uint8_t{0});
        }

        static void FinalizeReplayStopState(int slot, ReplayState &p,
                                            void *services = nullptr)
        {
            if (!services)
                services = InputInjector::LiveMovementServices(slot);
            if (!services)
                return;

            // Buttons and desire flags are replay-owned input state. Do not let
            // their down/changed edges leak into the first AI-controlled tick.
            ClearReplayStopButtonResidue(services);

            auto *sv = reinterpret_cast<char *>(services);
            void *pawn = InputInjector::ResolveReplayPawn(slot, services);
            if (!pawn)
                return;

            auto *pp = reinterpret_cast<char *>(pawn);
            const bool ladderResidue = ReplayStopPointHasLadderResidue(p) ||
                                       LiveMovementHasLadderResidue(slot, services);
            if (!ladderResidue)
            {
                uint32_t flags = 0;
                if (SafeRead(pawn, tg::kEnt_Flags, flags) &&
                    (flags & tg::kFL_OnGround) != 0)
                {
                    // Preserve horizontal momentum and genuine airborne motion,
                    // but never hand off an impossible grounded vertical impulse.
                    WriteField(pawn, tg::kEnt_AbsVelocity + 8, 0.0f);
                }
                return;
            }

            if (!CanWriteMemory(pp + tg::kEnt_MoveType, sizeof(uint8_t)) ||
                !CanWriteMemory(pp + tg::kEnt_ActualMoveType, sizeof(uint8_t)) ||
                !CanWriteMemory(pp + tg::kEnt_AbsVelocity, sizeof(float) * 3) ||
                !CanWriteMemory(pp + tg::kEnt_Flags, sizeof(uint32_t)) ||
                !CanWriteMemory(sv + tg::kServices_LadderNormal, sizeof(float) * 3) ||
                !CanWriteMemory(sv + tg::kServices_Ducked, sizeof(uint8_t)) ||
                !CanWriteMemory(sv + tg::kServices_DesiresDuck, sizeof(uint8_t) * 2) ||
                !CanWriteMemory(sv + tg::kServices_DuckAmount, sizeof(float) * 2))
                return;

            uint32_t flags = 0;
            if (!SafeRead(pawn, tg::kEnt_Flags, flags))
                return;

            *reinterpret_cast<uint8_t *>(pp + tg::kEnt_MoveType) = kMoveTypeWalk;
            *reinterpret_cast<uint8_t *>(pp + tg::kEnt_ActualMoveType) = kMoveTypeWalk;
            *reinterpret_cast<float *>(pp + tg::kEnt_AbsVelocity + 0) = 0.0f;
            *reinterpret_cast<float *>(pp + tg::kEnt_AbsVelocity + 4) = 0.0f;
            *reinterpret_cast<float *>(pp + tg::kEnt_AbsVelocity + 8) = 0.0f;
            flags &= ~tg::kFL_Ducking;
            *reinterpret_cast<uint32_t *>(pp + tg::kEnt_Flags) = flags;

            *reinterpret_cast<float *>(sv + tg::kServices_LadderNormal + 0) = 0.0f;
            *reinterpret_cast<float *>(sv + tg::kServices_LadderNormal + 4) = 0.0f;
            *reinterpret_cast<float *>(sv + tg::kServices_LadderNormal + 8) = 0.0f;
            *reinterpret_cast<uint8_t *>(sv + tg::kServices_Ducked) = 0;
            *reinterpret_cast<uint8_t *>(sv + tg::kServices_Ducking) = 0;
            *reinterpret_cast<uint8_t *>(sv + tg::kServices_DesiresDuck) = 0;
            *reinterpret_cast<float *>(sv + tg::kServices_DuckAmount) = 0.0f;
            *reinterpret_cast<float *>(sv + tg::kServices_DuckSpeed) = 0.0f;
        }

        static void MarkNetworkStateChanged(void *entity, uint32_t offset,
                                            int arrayIndex = -1)
        {
            if (!entity || offset == 0)
                return;

            NetworkStateChangedData data(offset, arrayIndex);
            reinterpret_cast<CEntityInstance *>(entity)->NetworkStateChanged(data);
        }

        static void MarkReplayViewNetworkChanged(void *pawn, int serverViewElement)
        {
            if (!pawn || serverViewElement < 0)
                return;

            MarkNetworkStateChanged(
                pawn,
                static_cast<uint32_t>(tg::kPawn_ServerViewAngleChanges),
                serverViewElement);
        }

        static float NormalizeDeg(float a)
        {
            a = std::fmod(a + 180.0f, 360.0f);
            if (a < 0.0f)
                a += 360.0f;
            return a - 180.0f;
        }

        static ReplaySnapMode ActiveReplaySnapMode()
        {
            switch (g_replaySnapMode.load(std::memory_order_relaxed))
            {
            case static_cast<int>(ReplaySnapMode::Soft):
                return ReplaySnapMode::Soft;
            case static_cast<int>(ReplaySnapMode::Off):
                return ReplaySnapMode::Off;
            default:
                return ReplaySnapMode::Hard;
            }
        }

        static ReplayViewMode ActiveReplayViewMode()
        {
            switch (g_replayViewMode.load(std::memory_order_relaxed))
            {
            case static_cast<int>(ReplayViewMode::PostOnly):
                return ReplayViewMode::PostOnly;
            case static_cast<int>(ReplayViewMode::Cmd):
                return ReplayViewMode::Cmd;
            default:
                return ReplayViewMode::PrePost;
            }
        }

        static ReplayCommandViewMode ActiveReplayCommandViewMode()
        {
            switch (g_replayCmdViewMode.load(std::memory_order_relaxed))
            {
            case static_cast<int>(ReplayCommandViewMode::Post):
                return ReplayCommandViewMode::Post;
            case static_cast<int>(ReplayCommandViewMode::NextPre):
                return ReplayCommandViewMode::NextPre;
            default:
                return ReplayCommandViewMode::Pre;
            }
        }

        static ReplayPovMode ActiveReplayPovMode()
        {
            switch (g_replayPovMode.load(std::memory_order_relaxed))
            {
            case static_cast<int>(ReplayPovMode::Off):
                return ReplayPovMode::Off;
            case static_cast<int>(ReplayPovMode::Always):
                return ReplayPovMode::Always;
            default:
                return ReplayPovMode::Spectated;
            }
        }

        static bool ShouldPublishReplayPov(int slot)
        {
            const ReplayPovMode mode = ActiveReplayPovMode();
            if (mode == ReplayPovMode::Always)
                return true;
            if (mode == ReplayPovMode::Off || !ValidSlot(slot))
                return false;
            return (g_replayPovMask.load(std::memory_order_relaxed) &
                    (uint64_t{1} << static_cast<unsigned>(slot))) != 0;
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
            case ReplayPerfCounter::SyncReplayView:
                g_perf.syncReplayViewCalls.fetch_add(amount, std::memory_order_relaxed);
                break;
            case ReplayPerfCounter::ServerViewWrite:
                g_perf.serverViewWrites.fetch_add(amount, std::memory_order_relaxed);
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
            g_perf.syncReplayViewCalls.store(0, std::memory_order_relaxed);
            g_perf.serverViewWrites.store(0, std::memory_order_relaxed);
            g_perf.virtualQueryCalls.store(0, std::memory_order_relaxed);
            g_perf.replayTickReads.store(0, std::memory_order_relaxed);
            g_perf.subtickRebuilds.store(0, std::memory_order_relaxed);
            g_perf.subticksAdded.store(0, std::memory_order_relaxed);
            g_perf.replayCommandFrameReads.store(0, std::memory_order_relaxed);
            g_perf.subtickClears.store(0, std::memory_order_relaxed);
            g_perf.subtickNoopSkips.store(0, std::memory_order_relaxed);
        }

        ReplayPerfCounters GetReplayPerfCounters()
        {
            return ReplayPerfCounters{
                g_perf.processMovementHooks.load(std::memory_order_relaxed),
                g_perf.finishMoveHooks.load(std::memory_order_relaxed),
                g_perf.playerRunCommandHooks.load(std::memory_order_relaxed),
                g_perf.physicsSimulateHooks.load(std::memory_order_relaxed),
                g_perf.syncReplayViewCalls.load(std::memory_order_relaxed),
                g_perf.serverViewWrites.load(std::memory_order_relaxed),
                g_perf.virtualQueryCalls.load(std::memory_order_relaxed),
                g_perf.replayTickReads.load(std::memory_order_relaxed),
                g_perf.subtickRebuilds.load(std::memory_order_relaxed),
                g_perf.subticksAdded.load(std::memory_order_relaxed),
                g_perf.replayCommandFrameReads.load(std::memory_order_relaxed),
                g_perf.subtickClears.load(std::memory_order_relaxed),
                g_perf.subtickNoopSkips.load(std::memory_order_relaxed),
            };
        }

        const char *ReplaySnapModeName(ReplaySnapMode mode)
        {
            switch (mode)
            {
            case ReplaySnapMode::Hard:
                return "hard";
            case ReplaySnapMode::Soft:
                return "soft";
            case ReplaySnapMode::Off:
                return "off";
            }
            return "hard";
        }

        void SetReplaySnapMode(ReplaySnapMode mode)
        {
            g_replaySnapMode.store(static_cast<int>(mode), std::memory_order_relaxed);
        }

        ReplaySnapMode GetReplaySnapMode()
        {
            return ActiveReplaySnapMode();
        }

        const char *ReplayViewModeName(ReplayViewMode mode)
        {
            switch (mode)
            {
            case ReplayViewMode::PrePost:
                return "prepost";
            case ReplayViewMode::PostOnly:
                return "post";
            case ReplayViewMode::Cmd:
                return "cmd";
            }
            return "prepost";
        }

        void SetReplayViewMode(ReplayViewMode mode)
        {
            g_replayViewMode.store(static_cast<int>(mode), std::memory_order_relaxed);
        }

        ReplayViewMode GetReplayViewMode()
        {
            return ActiveReplayViewMode();
        }

        const char *ReplayCommandViewModeName(ReplayCommandViewMode mode)
        {
            switch (mode)
            {
            case ReplayCommandViewMode::Pre:
                return "pre";
            case ReplayCommandViewMode::Post:
                return "post";
            case ReplayCommandViewMode::NextPre:
                return "nextpre";
            }
            return "pre";
        }

        void SetReplayCommandViewMode(ReplayCommandViewMode mode)
        {
            g_replayCmdViewMode.store(static_cast<int>(mode), std::memory_order_relaxed);
        }

        ReplayCommandViewMode GetReplayCommandViewMode()
        {
            return ActiveReplayCommandViewMode();
        }

        const char *ReplayPovModeName(ReplayPovMode mode)
        {
            switch (mode)
            {
            case ReplayPovMode::Off:
                return "off";
            case ReplayPovMode::Spectated:
                return "spectated";
            case ReplayPovMode::Always:
                return "always";
            }
            return "spectated";
        }

        void SetReplayPovMode(ReplayPovMode mode)
        {
            g_replayPovMode.store(static_cast<int>(mode), std::memory_order_relaxed);
        }

        ReplayPovMode GetReplayPovMode()
        {
            return ActiveReplayPovMode();
        }

        void SetReplayPovMask(uint64_t mask)
        {
            g_replayPovMask.store(mask, std::memory_order_relaxed);
        }

        bool ReplayViewAllowsEngineSetEyeAngles()
        {
            return ActiveReplayViewMode() == ReplayViewMode::Cmd;
        }

        static bool ShouldDirectWritePreView()
        {
            return ActiveReplayViewMode() == ReplayViewMode::PrePost;
        }

        static bool ShouldDirectWritePostView()
        {
            ReplayViewMode mode = ActiveReplayViewMode();
            return mode == ReplayViewMode::PrePost || mode == ReplayViewMode::PostOnly;
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

        static void WriteSceneNodeOrigin(char *entity, float x, float y, float z)
        {
            void *node = ResolveSceneNode(entity);
            if (!node)
                return;
#if defined(_WIN32)
            const float origin[3] = {x, y, z};
            TryWriteMemory(node, tg::kNode_AbsOrigin, origin, sizeof(origin));
#else
            auto *n = reinterpret_cast<char *>(node);
            if (!CanWriteMemory(n + tg::kNode_AbsOrigin, sizeof(float) * 3))
                return;
            *reinterpret_cast<float *>(n + tg::kNode_AbsOrigin + 0) = x;
            *reinterpret_cast<float *>(n + tg::kNode_AbsOrigin + 4) = y;
            *reinterpret_cast<float *>(n + tg::kNode_AbsOrigin + 8) = z;
#endif
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

        static bool SnapshotPositionIsFinite(const MovementSnapshot &s)
        {
            return std::isfinite(s.originX) && std::isfinite(s.originY) &&
                   std::isfinite(s.originZ);
        }

        static bool ShouldApplyMovementSnap(ReplaySnapMode mode, int slot, int cursor,
                                            void *services, const MovementSnapshot &target)
        {
            if (mode == ReplaySnapMode::Hard)
                return true;
            if (mode == ReplaySnapMode::Off)
                return false;

            if (cursor <= 0)
                return true;
            if (!SnapshotPositionIsFinite(target))
                return false;

            MovementSnapshot live{};
            if (!ReadSnapshot(slot, services, live) || !SnapshotPositionIsFinite(live))
                return true;

            const float dx = live.originX - target.originX;
            const float dy = live.originY - target.originY;
            const float dz = live.originZ - target.originZ;
            const float dist2 = dx * dx + dy * dy;
            return dist2 > (kSoftSnapDistance * kSoftSnapDistance) ||
                   std::fabs(dz) > kSoftSnapVerticalDistance;
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
                g_rec[slot].liveWs.store(ws, std::memory_order_relaxed);
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

        static MovementSnapshot ReplayCommandViewForTick(ReplayState &p,
                                                         int cur,
                                                         int total,
                                                         const ReplayTick &tick)
        {
            const ReplayCommandViewMode mode = ActiveReplayCommandViewMode();
            if (mode == ReplayCommandViewMode::Post)
                return tick.post;
            if (mode == ReplayCommandViewMode::NextPre)
                return (cur + 1 < total) ? p.ticks[static_cast<size_t>(cur + 1)].pre : tick.post;
            return tick.pre;
        }

        static uint64_t ReplayPressEdgesForPreStartTick(const ReplayState &p, int index)
        {
            if (index < 0 || index >= static_cast<int>(p.ticks.size()))
                return 0;

            const MovementSnapshot &pre = p.ticks[static_cast<size_t>(index)].pre;
            if (pre.buttons1 != 0 || pre.buttons2 != 0)
                return pre.buttons1;

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
                found |= ReplayPressEdgesForPreStartTick(p, i) & candidates;
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
                g_serverViewChangeIndex[slot] = 0;
                InputInjector::ClearUsercmdMovementIntent(slot);
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
            const bool resumesHeldReplay =
                p.playing.load(std::memory_order_acquire) &&
                p.holdBeforeCursor.load(std::memory_order_relaxed) == startIndex;
            p.cursor.store(startIndex, std::memory_order_relaxed);
            // Freeze pre-roll is one continuous replay. Keep its original
            // start cursor when releasing the hold so start-only input
            // synthesis cannot replay an old attack edge at live start.
            if (!resumesHeldReplay)
                p.startCursor.store(startIndex, std::memory_order_relaxed);
            p.holdBeforeCursor.store(-1, std::memory_order_relaxed);
            InvalidateReplayWeaponCache(p);
            g_lastFinalViewCursor[slot] = -1;
            g_serverViewChangeIndex[slot] = 0;
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
            p.cursor.store(startIndex, std::memory_order_relaxed);
            p.startCursor.store(startIndex, std::memory_order_relaxed);
            p.holdBeforeCursor.store(holdBeforeIndex, std::memory_order_relaxed);
            InvalidateReplayWeaponCache(p);
            g_lastFinalViewCursor[slot] = -1;
            g_serverViewChangeIndex[slot] = 0;
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
            {
                std::lock_guard<std::mutex> lk(p.mu);
                const bool wasPlaying =
                    p.playing.exchange(false, std::memory_order_acq_rel);
                if (wasPlaying)
                    FinalizeReplayStopState(slot, p);
                p.holdBeforeCursor.store(-1, std::memory_order_relaxed);
                InvalidateReplayWeaponCache(p);
                g_lastFinalViewCursor[slot] = -1;
                g_serverViewChangeIndex[slot] = 0;
                InputInjector::ClearUsercmdMovementIntent(slot);
            }
            return true;
        }

        bool ReleaseReplayBuffer(int slot)
        {
            if (!ValidSlot(slot))
                return false;

            ReplayState &p = g_rep[slot];
            {
                std::lock_guard<std::mutex> lk(p.mu);
                const bool wasPlaying =
                    p.playing.exchange(false, std::memory_order_acq_rel);
                if (wasPlaying)
                    FinalizeReplayStopState(slot, p);
                p.holdBeforeCursor.store(-1, std::memory_order_relaxed);
                InvalidateReplayWeaponCache(p);
                g_lastFinalViewCursor[slot] = -1;
                g_serverViewChangeIndex[slot] = 0;
                InputInjector::ClearUsercmdMovementIntent(slot);
                ReleaseReplayVectors(p);
                p.cursor.store(0, std::memory_order_relaxed);
                p.startCursor.store(0, std::memory_order_relaxed);
                p.loop.store(false, std::memory_order_relaxed);
                InputInjector::ClearReplayPawn(slot);
            }
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
                b1 = b0 & ~heldPrev;
                b2 = heldPrev & ~b0;
            }
            if (!hasCommandButtons)
                b1 |= ReplayPrimeAttackButtonsForStart(p, cur, b0, b1);

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
            out.commandView = ReplayCommandViewForTick(p, cur, total, *tick);
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

        bool ReplayCommandViewSnapshot(int slot, MovementSnapshot &out)
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
            out = ReplayCommandViewForTick(p, cur, total, *tick);
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
                // playable by synthesizing edge masks from adjacent ticks.
                uint64_t heldPrev = (cur > 0) ? p.ticks[static_cast<size_t>(cur - 1)].pre.buttons : 0;
                b1 = b0 & ~heldPrev;
                b2 = heldPrev & ~b0;
            }
            b1 |= ReplayPrimeAttackButtonsForStart(p, cur, b0, b1);
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

        static void WriteRawViewAnglesToPawn(char *p, float pitch, float yaw)
        {
            const float normalizedYaw = NormalizeDeg(yaw);
            *reinterpret_cast<float *>(p + tg::kPawn_ViewAngle + 0) = pitch;
            *reinterpret_cast<float *>(p + tg::kPawn_ViewAngle + 4) = normalizedYaw;
            *reinterpret_cast<float *>(p + tg::kPawn_ViewAngle + 8) = 0.0f;
            *reinterpret_cast<float *>(p + tg::kPawn_EyeAngles + 0) = pitch;
            *reinterpret_cast<float *>(p + tg::kPawn_EyeAngles + 4) = normalizedYaw;
            *reinterpret_cast<float *>(p + tg::kPawn_EyeAngles + 8) = 0.0f;
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

        struct ServerViewVectorCandidate
        {
            const char *layout;
            char *elements;
            int *sizePtr;
            int size;
            int alloc;
        };

        static bool PlausibleServerViewVector(const ServerViewVectorCandidate &c)
        {
            if (!c.elements || !c.sizePtr)
                return false;
            if (c.size < 0 || c.alloc <= 0 || c.size > c.alloc)
                return false;
            if (c.alloc > 64)
                return false;

            constexpr size_t kViewChangeSize = 0x48;
            const int elementIndex = (c.size > 0) ? (c.size - 1) : 0;
            char *element = c.elements + static_cast<size_t>(elementIndex) * kViewChangeSize;
            return CanWriteMemory(c.sizePtr, sizeof(int)) &&
                   CanWriteMemory(element + 0x40, sizeof(uint32_t));
        }

        static bool ResolveServerViewVector(char *pawn, ServerViewVectorCandidate &out)
        {
            char *vec = pawn + tg::kPawn_ServerViewAngleChanges;

            // Current hl2sdk-cs2 CUtlVector layout: int size at +0,
            // padding, then CUtlMemory at +8 (pointer, alloc, grow).
            ServerViewVectorCandidate sdk{};
            sdk.layout = "sdk";
            sdk.sizePtr = reinterpret_cast<int *>(vec + 0x00);
            if (SafeRead(vec, 0x00, sdk.size) &&
                SafeRead(vec, 0x08, sdk.elements) &&
                SafeRead(vec, 0x10, sdk.alloc) &&
                PlausibleServerViewVector(sdk))
            {
                out = sdk;
                return true;
            }

            // Older Source-style vectors put memory first and size later.
            ServerViewVectorCandidate legacy{};
            legacy.layout = "legacy";
            legacy.sizePtr = reinterpret_cast<int *>(vec + 0x10);
            if (SafeRead(vec, 0x00, legacy.elements) &&
                SafeRead(vec, 0x08, legacy.alloc) &&
                SafeRead(vec, 0x10, legacy.size) &&
                PlausibleServerViewVector(legacy))
            {
                out = legacy;
                return true;
            }

            return false;
        }

        static bool WriteServerViewAngleChange(char *pawn, int slot,
                                               const MovementSnapshot &s,
                                               int *elementOut)
        {
            if (elementOut)
                *elementOut = -1;

            ServerViewVectorCandidate vec{};
            if (!ResolveServerViewVector(pawn, vec))
                return false;

            constexpr size_t kViewChangeSize = 0x48;
            constexpr uint32_t kFixAngleAbsolute = 1;
            const int elementIndex = (vec.size > 0) ? (vec.size - 1) : 0;
            char *element = vec.elements + static_cast<size_t>(elementIndex) * kViewChangeSize;
            const float normalizedYaw = NormalizeDeg(s.yaw);
            auto *type = reinterpret_cast<uint32_t *>(element + 0x30);
            auto *pitch = reinterpret_cast<float *>(element + 0x34);
            auto *yaw = reinterpret_cast<float *>(element + 0x38);
            auto *roll = reinterpret_cast<float *>(element + 0x3C);
            auto *index = reinterpret_cast<uint32_t *>(element + 0x40);

            uint32_t next = g_serverViewChangeIndex[slot] + 1;
            if (*index >= next)
                next = *index + 1;
            if (next == 0)
                next = 1;

            *type = kFixAngleAbsolute;
            *pitch = s.pitch;
            *yaw = normalizedYaw;
            *roll = 0.0f;
            *index = next;
            g_serverViewChangeIndex[slot] = next;
            if (vec.size == 0)
                *vec.sizePtr = 1;
            if (elementOut)
                *elementOut = elementIndex;
            return true;
        }

        static bool SyncReplayView(int slot, void *services,
                                   const MovementSnapshot &s)
        {
            auto *sv = reinterpret_cast<char *>(services);
            void *pawn = InputInjector::ResolveReplayPawn(slot, services);
            if (!pawn)
                return false;
            auto *p = reinterpret_cast<char *>(pawn);

            BotControllerHooks::ApplyReplayEyeAngles(pawn, s.pitch, s.yaw);
            WriteRawViewAnglesToPawn(p, s.pitch, s.yaw);
            WriteReplayViewHistory(services, p, s.pitch, s.yaw);
            AddReplayPerf(ReplayPerfCounter::SyncReplayView);
            if (ShouldPublishReplayPov(slot))
            {
                int serverViewElement = -1;
                if (WriteServerViewAngleChange(p, slot, s, &serverViewElement))
                {
                    AddReplayPerf(ReplayPerfCounter::ServerViewWrite);
                    MarkReplayViewNetworkChanged(pawn, serverViewElement);
                }
            }
            return true;
        }

        // Write velocity onto the pawn. View is controlled separately so
        // we can A/B direct view writes without changing movement replay.
        static float ReplayEngineVelZ(float velZ)
        {
            if (!std::isfinite(velZ))
                return 0.0f;
            return velZ < kReplayMinEngineVelZ ? kReplayMinEngineVelZ : velZ;
        }

        static void WriteVelocityToPawn(int slot, void *services, const MovementSnapshot &s)
        {
            void *pawn = InputInjector::ResolveReplayPawn(slot, services);
            if (!pawn)
                return;
            auto *p = reinterpret_cast<char *>(pawn);

            *reinterpret_cast<float *>(p + tg::kEnt_AbsVelocity + 0) = s.velX;
            *reinterpret_cast<float *>(p + tg::kEnt_AbsVelocity + 4) = s.velY;
            *reinterpret_cast<float *>(p + tg::kEnt_AbsVelocity + 8) = ReplayEngineVelZ(s.velZ);
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
            *reinterpret_cast<float *>(m + tg::kMove_Velocity + 8) = ReplayEngineVelZ(s.velZ);
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

        // PlayerRunCommand (pre): weapon firing and grenade throws can consume
        // pawn state before ProcessMovement runs, so seed the live pawn here too.
        void OnReplayCommandPre(int slot, void *services)
        {
            if (!ValidSlot(slot) || !services)
                return;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return;

            int cur = -1;
            int total = 0;
            const ReplayTick *t = CurrentReplayTickPtr(p, cur, total);
            if (!t)
                return;
            const MovementSnapshot commandView =
                ReplayCommandViewForTick(p, cur, total, *t);

            OnReplayCommandPre(slot, services, *t, commandView);
        }

        void OnReplayCommandPre(int slot, void *services, const ReplayTick &t,
                                const MovementSnapshot &commandView)
        {
            if (!ValidSlot(slot) || !services)
                return;
            auto *sv = reinterpret_cast<char *>(services);
            WriteVelocityToPawn(slot, services, t.pre);
            WriteMovementServiceState(services, t.pre);

            void *pawn = InputInjector::ResolveReplayPawn(slot, services);
            if (pawn)
            {
                auto *pp = reinterpret_cast<char *>(pawn);
                *reinterpret_cast<uint8_t *>(pp + tg::kEnt_MoveType) = t.pre.moveType;
                *reinterpret_cast<uint8_t *>(pp + tg::kEnt_ActualMoveType) = t.pre.actualMoveType;
                WriteSceneNodeOrigin(pp, t.pre.originX, t.pre.originY, t.pre.originZ);

                BotControllerHooks::ApplyReplayEyeAngles(
                    pawn, commandView.pitch, commandView.yaw);
                WriteRawViewAnglesToPawn(pp, commandView.pitch, commandView.yaw);
                WriteReplayViewHistory(
                    services, pp, commandView.pitch, commandView.yaw);
            }
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
                FinalizeReplayStopState(slot, p, services);
        }

        // ProcessMovement (pre): seed CMoveData + pawn + moveType with pre state.
        void OnReplayPre(int slot, void *services, void *moveData)
        {
            if (!ValidSlot(slot) || !services || !moveData)
                return;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return;
            int cursor = -1;
            int total = 0;
            const ReplayTick *t = CurrentReplayTickPtr(p, cursor, total);
            if (!t)
                return; // commit handler will stop/loop
            const ReplaySnapMode snapMode = ActiveReplaySnapMode();
            const bool snapMovement =
                ShouldApplyMovementSnap(snapMode, slot, cursor, services, t->pre);

            if (snapMovement)
            {
                WriteMoveData(moveData, t->pre);
                WriteVelocityToPawn(slot, services, t->pre);
                WriteMovementServiceState(services, t->pre);
            }
            if (ShouldDirectWritePreView())
                SyncReplayView(slot, services, t->pre);
            auto *sv = reinterpret_cast<char *>(services);
            // Feed recorded buttons so the engine's Duck()/ladder logic runs
            *reinterpret_cast<uint64_t *>(sv + tg::kServices_Buttons) = t->pre.buttons;
            *reinterpret_cast<uint64_t *>(sv + tg::kServices_Buttons1) = t->pre.buttons1;
            *reinterpret_cast<uint64_t *>(sv + tg::kServices_Buttons2) = t->pre.buttons2;
            if (snapMovement)
            {
                void *pawn = InputInjector::ResolveReplayPawn(slot, services);
                if (pawn)
                {
                    auto *pp = reinterpret_cast<char *>(pawn);
                    *reinterpret_cast<uint8_t *>(pp + tg::kEnt_MoveType) = t->pre.moveType;
                    WriteSceneNodeOrigin(pp, t->pre.originX, t->pre.originY, t->pre.originZ);
                }
            }
        }

        // FinishMove (pre): write post snapshot into CMoveData and force a
        // small scene-node origin mismatch so FinishMove resyncs from MoveData.
        void OnReplayFinishMove(int slot, void *services, void *moveData)
        {
            if (!ValidSlot(slot) || !services || !moveData)
                return;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return;
            if (ActiveReplaySnapMode() != ReplaySnapMode::Hard)
                return;
            int cur = -1;
            int total = 0;
            const ReplayTick *t = CurrentReplayTickPtr(p, cur, total);
            if (!t)
                return;
            auto *m = reinterpret_cast<char *>(moveData);
            *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 0) = t->post.originX;
            *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 4) = t->post.originY;
            *reinterpret_cast<float *>(m + tg::kMove_AbsOrigin + 8) = t->post.originZ;
            *reinterpret_cast<float *>(m + tg::kMove_Velocity + 0) = t->post.velX;
            *reinterpret_cast<float *>(m + tg::kMove_Velocity + 4) = t->post.velY;
            *reinterpret_cast<float *>(m + tg::kMove_Velocity + 8) = ReplayEngineVelZ(t->post.velZ);

            // Force engine to resync the entity origin from MoveData
            auto *sv = reinterpret_cast<char *>(services);
            void *pawn = InputInjector::ResolveReplayPawn(slot, services);
            if (pawn)
            {
                WriteSceneNodeOrigin(reinterpret_cast<char *>(pawn),
                                     t->post.originX,
                                     t->post.originY,
                                     t->post.originZ + kFinishMoveResyncNudgeZ);
            }
        }

        // FinishMove (post): publish the final post view before replay commit
        // advances the cursor. This is intentionally separate from movement
        // commit because first-person spectator state can sample before the
        // later PhysicsSimulate boundary.
        void OnReplayFinalView(int slot, void *services)
        {
            if (!ValidSlot(slot) || !services)
                return;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return;
            if (!ShouldDirectWritePostView())
                return;
            int cur = -1;
            int total = 0;
            const ReplayTick *t = CurrentReplayTickPtr(p, cur, total);
            if (!t)
                return;

            SyncReplayView(slot, services, t->post);
            g_lastFinalViewCursor[slot] = cur;
        }

        // FinishMove (post): commit post moveType/flags + advance cursor.
        void OnReplayCommit(int slot, void *services)
        {
            if (!ValidSlot(slot) || !services)
                return;
            ReplayState &p = g_rep[slot];
            if (!p.playing.load(std::memory_order_acquire))
                return;
            int cur = p.cursor.load(std::memory_order_relaxed);
            int total = static_cast<int>(p.ticks.size());
            const ReplayTick *t = nullptr;
            if (cur >= 0 && cur < total)
            {
                AddReplayPerf(ReplayPerfCounter::ReplayTickRead);
                t = &p.ticks[static_cast<size_t>(cur)];
            }
            else
            {
                if (cur >= total)
                {
                    if (p.loop.load(std::memory_order_relaxed) && total > 0)
                    {
                        p.cursor.store(
                            p.startCursor.load(std::memory_order_relaxed),
                            std::memory_order_relaxed);
                        InvalidateReplayWeaponCache(p);
                        g_lastFinalViewCursor[slot] = -1;
                        g_serverViewChangeIndex[slot] = 0;
                        return;
                    }
                    const bool wasPlaying =
                        p.playing.exchange(false, std::memory_order_acq_rel);
                    if (wasPlaying)
                        FinalizeReplayStopState(slot, p, services);
                    InvalidateReplayWeaponCache(p);
                    InputInjector::ClearUsercmdMovementIntent(slot);
                    return;
                }
                return;
            }

            auto *sv = reinterpret_cast<char *>(services);
            const ReplaySnapMode snapMode = ActiveReplaySnapMode();
            const bool hardSnap = snapMode == ReplaySnapMode::Hard;
            if (hardSnap)
            {
                void *pawn = InputInjector::ResolveReplayPawn(slot, services);
                if (pawn)
                {
                    auto *pp = reinterpret_cast<char *>(pawn);
                    *reinterpret_cast<uint8_t *>(pp + tg::kEnt_MoveType) = t->post.moveType;
                    *reinterpret_cast<uint8_t *>(pp + tg::kEnt_ActualMoveType) = t->post.actualMoveType;
                    // Merge ground + ducking bits from the recording, keep the rest live.
                    uint32_t live = 0;
                    if (!SafeRead(pawn, tg::kEnt_Flags, live))
                        return;
                    uint32_t mask = tg::kFL_OnGround | tg::kFL_Ducking;
                    live = (live & ~mask) | (t->post.entityFlags & mask);
                    *reinterpret_cast<uint32_t *>(pp + tg::kEnt_Flags) = live;
                }
            }
            if (ShouldDirectWritePostView() && g_lastFinalViewCursor[slot] != cur)
                SyncReplayView(slot, services, t->post);

            if (hardSnap)
            {
                WriteMovementServiceState(services, t->post);
            }

            const int holdBefore = p.holdBeforeCursor.load(std::memory_order_relaxed);
            if (holdBefore > 0 && cur + 1 >= holdBefore)
            {
                p.cursor.store(holdBefore - 1, std::memory_order_relaxed);
                return;
            }
            const int next = cur + 1;
            p.cursor.store(next, std::memory_order_relaxed);
            if (next >= total && !p.loop.load(std::memory_order_relaxed))
            {
                const bool wasPlaying =
                    p.playing.exchange(false, std::memory_order_acq_rel);
                if (wasPlaying)
                    FinalizeReplayStopState(slot, p, services);
                InvalidateReplayWeaponCache(p);
                InputInjector::ClearUsercmdMovementIntent(slot);
            }
        }

        void ClearAll()
        {
            for (int i = 0; i < kMaxSlots; ++i)
            {
                g_rec[i].recording.store(false, std::memory_order_release);
                {
                    std::lock_guard<std::mutex> lk(g_rep[i].mu);
                    const bool wasPlaying = g_rep[i].playing.exchange(
                        false, std::memory_order_acq_rel);
                    if (wasPlaying)
                        FinalizeReplayStopState(i, g_rep[i]);
                    ReleaseReplayVectors(g_rep[i]);
                }
                {
                    std::lock_guard<std::mutex> lk(g_rec[i].mu);
                    g_rec[i].ticks.clear();
                    g_rec[i].subs.clear();
                    g_rec[i].pendingSubs.clear();
                    g_rec[i].havePre = false;
                }
                g_rec[i].currentDef.store(-1, std::memory_order_relaxed);
                g_rec[i].liveWs.store(nullptr, std::memory_order_relaxed);
                g_rep[i].cursor.store(0, std::memory_order_relaxed);
                g_rep[i].startCursor.store(0, std::memory_order_relaxed);
                g_rep[i].holdBeforeCursor.store(-1, std::memory_order_relaxed);
                g_rep[i].loop.store(false, std::memory_order_relaxed);
                InvalidateReplayWeaponCache(g_rep[i]);
                g_lastFinalViewCursor[i] = -1;
                g_serverViewChangeIndex[i] = 0;
                InputInjector::ClearReplayPawn(i);
            }
            InputInjector::ClearAllUsercmdMovementIntents();
            g_replayPovMask.store(0, std::memory_order_relaxed);
        }
    }
}
