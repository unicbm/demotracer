// CCSBot update, replay view, and native perception detours.

#include "BotController.h"
#include "BotControllerState.h"
#include "ccsbot_slot.h"
#include "sig_scan.h"
#include "MotionRecorder.h"
#include "InputInjector.h"
#include "WeaponLocker.h"
#include "WeaponLockerState.h"
#include "version_targets.h"
#include "hook.h"
#include "platform.h"
#include "live_entities.h"

#include <tier0/dbg.h>

#include <cstdint>
#include <cstdio>
#include <array>
#include <atomic>
#include <cmath>
#include <cstring>
#include <vector>

namespace tg = BotController::targets;

using Update_t = void(BC_FASTCALL *)(void *bot);
using Upkeep_t = void(BC_FASTCALL *)(void *bot);
using IsVisiblePos_t = bool(BC_FASTCALL *)(void *bot, const void *pos,
                                           bool testFov, void *traceContext);
using IsVisiblePlayer_t = bool(BC_FASTCALL *)(void *bot, void *playerPawn,
                                              bool testFov, uint8_t *visibleParts);
using UpdateLookAngles_t = void(BC_FASTCALL *)(void *bot);
using SetEyeAngles_t = void(BC_FASTCALL *)(void *pawn, float *angle);
using GetEyeAngles_t = float *(BC_FASTCALL *)(void *pawn, float *out);

namespace BotController
{
    namespace BotControllerHooks
    {
        using LadderUpdate_t = bool(BC_FASTCALL *)(void *ladderState);
        static LadderUpdate_t g_origLadderUpdate = nullptr;
        static Update_t g_invalidatePath = nullptr;
        static Hook<LadderUpdate_t> g_hookLadderUpdate;
        static std::array<std::atomic<bool>, 64> g_replayLadderSuppressed{};
        static Update_t g_origUpdate = nullptr;
        static void *g_addrUpdate = nullptr;
        static Upkeep_t g_origUpkeep = nullptr;
        static void *g_addrUpkeep = nullptr;
        static IsVisiblePos_t g_origIsVisiblePos = nullptr;
        static void *g_addrIsVisiblePos = nullptr;
        static IsVisiblePlayer_t g_origIsVisiblePlayer = nullptr;
        static void *g_addrIsVisiblePlayer = nullptr;
        static UpdateLookAngles_t g_origUpdateLookAngles = nullptr;
        static void *g_addrUpdateLookAngles = nullptr;
        static SetEyeAngles_t g_origSetEyeAngles = nullptr;
        static void *g_addrSetEyeAngles = nullptr;
        static GetEyeAngles_t g_origGetEyeAngles = nullptr;
        static void *g_addrGetEyeAngles = nullptr;
        static std::string g_status = "not_attempted";

        static Hook<Update_t> g_hookUpdate;
        static Hook<Upkeep_t> g_hookUpkeep;
        static Hook<IsVisiblePos_t> g_hookIsVisiblePos;
        static Hook<IsVisiblePlayer_t> g_hookIsVisiblePlayer;
        static Hook<UpdateLookAngles_t> g_hookUpdateLookAngles;
        static Hook<SetEyeAngles_t> g_hookSetEyeAngles;
        static Hook<GetEyeAngles_t> g_hookGetEyeAngles;
        static std::array<NativePerceptionState, 64> g_nativePerception{};
        static std::array<std::atomic<void *>, 64> g_pendingBestWeaponBots{};
        static uint32_t g_nativePerceptionSerial = 0;
        static bool g_replayNativeFovOverride = true;

        static bool IsValidEnemyHandle(uint32_t handle)
        {
            return handle != 0u && handle != 0xFFFFFFFFu && handle != 0xFFFFFFFEu;
        }

        static void CaptureNativePerception(void *bot, int slot)
        {
            if (!bot || slot < 0 || slot >= static_cast<int>(g_nativePerception.size()))
                return;

            NativePerceptionState state{};
            uint8_t enemyVisible = 0;
            uint8_t visibleEnemyParts = 0;
            uint8_t lastEnemyDead = 0;
            bool ok =
                SafeRead(bot, tg::kBot_Enemy, state.enemyHandle) &&
                SafeRead(bot, tg::kBot_IsEnemyVisible, enemyVisible) &&
                SafeRead(bot, tg::kBot_VisibleEnemyParts, visibleEnemyParts) &&
                SafeRead(bot, tg::kBot_NearbyEnemyCount, state.nearbyEnemyCount) &&
                SafeRead(bot, tg::kBot_IsLastEnemyDead, lastEnemyDead) &&
                SafeRead(bot, tg::kBot_LastSawEnemyTimestamp, state.lastSawEnemyTimestamp) &&
                SafeRead(bot, tg::kBot_FirstSawEnemyTimestamp, state.firstSawEnemyTimestamp) &&
                SafeRead(bot, tg::kBot_CurrentEnemyAcquireTimestamp,
                          state.currentEnemyAcquireTimestamp);

            state.valid = ok ? 1 : 0;
            state.hasEnemy = ok && IsValidEnemyHandle(state.enemyHandle) ? 1 : 0;
            state.enemyVisible = enemyVisible != 0 ? 1 : 0;
            state.visibleEnemyParts = static_cast<int32_t>(visibleEnemyParts);
            state.lastEnemyDead = lastEnemyDead != 0 ? 1 : 0;
            state.updateSerial = ++g_nativePerceptionSerial;
            g_nativePerception[static_cast<size_t>(slot)] = state;
        }

        bool GetNativePerceptionState(int slot, NativePerceptionState &out)
        {
            if (slot < 0 || slot >= static_cast<int>(g_nativePerception.size()))
                return false;
            out = g_nativePerception[static_cast<size_t>(slot)];
            return out.valid != 0;
        }

        void SetReplayNativeFovOverride(bool enabled)
        {
            g_replayNativeFovOverride = enabled;
        }

        bool RequestEquipBestWeapon(int slot)
        {
            if (slot < 0 || slot >= static_cast<int>(g_pendingBestWeaponBots.size()) ||
                !WeaponLockerHooks::EquipBestWeaponAddress())
            {
                return false;
            }

            void *bot = BotForSlot(slot);
            if (!bot || CCSBotToSlot(bot) != slot)
                return false;
            if (InputInjector::IsSlotControllingBot(slot))
                return false;

            g_pendingBestWeaponBots[static_cast<size_t>(slot)].store(
                bot, std::memory_order_release);
            return true;
        }

        void *BotForSlot(int slot)
        {
            return LiveEntities::BotForSlot(slot);
        }

        static KHook::Return<bool> BC_FASTCALL HookedLadderUpdate(void *ladderState)
        {
            // This navigation state machine owns the bot's mount Teleport.
            // Its first member is the CCSBot; this is not movement services.
            void *bot = nullptr;
            if (SafeRead(ladderState, 0, bot) && bot)
            {
                const int slot = CCSBotToSlot(bot);
                if (slot >= 0 && slot < 64 && MotionRecorder::IsReplaying(slot) &&
                    !InputInjector::IsSlotControllingBot(slot))
                {
                    g_replayLadderSuppressed[slot].store(true, std::memory_order_relaxed);
                    return {KHook::Action::Supersede, true}; // path traversal is being handled by replay
                }
            }
            return g_hookLadderUpdate.Continue(ladderState);
        }

        void ReleaseReplayNavigation(int slot)
        {
            if (slot < 0 || slot >= 64 || !g_replayLadderSuppressed[slot].exchange(false)) return;
            void *bot = BotForSlot(slot);
            if (bot && g_invalidatePath && !InputInjector::IsSlotControllingBot(slot))
                g_invalidatePath(bot); // retire obsolete AI path; never alter Pawn physics
        }

        static float NormalizeDeg(float a)
        {
            a = std::fmod(a + 180.0f, 360.0f);
            if (a < 0.0f)
                a += 360.0f;
            return a - 180.0f;
        }

        // All is an explicit full-brain lock. Replay itself keeps Update alive
        // so native perception and decision state can shadow the injected
        // command stream and be ready when replay control is released.
        static KHook::Return<void> BC_FASTCALL HookedUpdate(void *bot)
        {
            int slot = CCSBotToSlot(bot);
            if (slot >= 0 && BotControllerState::GetAll(slot))
            {
                const uint8_t ticked = 1;
                WriteField(bot, tg::kBot_AiTickedFlag, ticked);
                return {KHook::Action::Supersede};
            }
            g_hookUpdate.Continue(bot);
            CaptureNativePerception(bot, slot);

            if (slot < 0 || slot >= static_cast<int>(g_pendingBestWeaponBots.size()))
                return {KHook::Action::Ignore};

            void *requestedBot = g_pendingBestWeaponBots[static_cast<size_t>(slot)].exchange(
                nullptr, std::memory_order_acq_rel);
            if (requestedBot != bot ||
                MotionRecorder::IsReplaying(slot) ||
                InputInjector::IsSlotControllingBot(slot) ||
                BotControllerState::GetAll(slot) ||
                WeaponLockerState::Get(slot) != LockTarget::None)
            {
                return {KHook::Action::Ignore};
            }

            // The native AI has just refreshed its combat state. Give it one
            // authoritative weapon choice now that DTR no longer owns output.
            WeaponLockerHooks::EquipBestWeaponRaw(bot, /*mustEquip=*/true);
            return {KHook::Action::Ignore};
        }

        // CS:GO botmimic keeps native vision running and disables only the FOV
        // cone. Do the same while a DTR owns output: native LOS, smoke and
        // target-state logic still run, but rear threats can enter the native
        // perception/reaction pipeline before handoff.
        static KHook::Return<bool> BC_FASTCALL HookedIsVisiblePos(void *bot, const void *pos,
                                                   bool testFov, void *traceContext)
        {
            int slot = CCSBotToSlot(bot);
            if (g_replayNativeFovOverride && slot >= 0 && MotionRecorder::IsReplaying(slot))
                testFov = false;
            return g_hookIsVisiblePos.Continue(bot, pos, testFov, traceContext);
        }

        static KHook::Return<bool> BC_FASTCALL HookedIsVisiblePlayer(void *bot, void *playerPawn,
                                                      bool testFov, uint8_t *visibleParts)
        {
            int slot = CCSBotToSlot(bot);
            if (g_replayNativeFovOverride && slot >= 0 && MotionRecorder::IsReplaying(slot))
                testFov = false;
            return g_hookIsVisiblePlayer.Continue(bot, playerPawn, testFov, visibleParts);
        }

        // Skip the per-frame view tick under replay, All, or Aim lock.
        static KHook::Return<void> BC_FASTCALL HookedUpdateLookAngles(void *bot); // fwd decl

        static KHook::Return<void> BC_FASTCALL HookedUpkeep(void *bot)
        {
            int slot = CCSBotContextToSlot(bot);
            if (slot >= 0 &&
                (BotControllerState::GetAll(slot) || BotControllerState::GetAim(slot)))
            {
                return {KHook::Action::Supersede};
            }
            g_hookUpkeep.Continue(bot);
            return {KHook::Action::Ignore};
        }

        static KHook::Return<void> BC_FASTCALL HookedUpdateLookAngles(void *bot)
        {
            int slot = CCSBotContextToSlot(bot);
            // Keep replay POV authoritative. The rest of Upkeep still runs so
            // non-view native state remains warm for handoff.
            if (slot >= 0 &&
                (MotionRecorder::IsReplaying(slot) ||
                 BotControllerState::GetAll(slot) ||
                 BotControllerState::GetAim(slot)))
            {
                return {KHook::Action::Supersede};
            }
            g_hookUpdateLookAngles.Continue(bot);
            return {KHook::Action::Ignore};
        }

        // Engine eye-angle
        static KHook::Return<void> BC_FASTCALL HookedSetEyeAngles(void *pawn, float *angle)
        {
            int slot = pawn ? ControllerSlotForPawn(pawn) : -1;
            if (slot >= 0 && MotionRecorder::IsReplaying(slot))
            {
                return {KHook::Action::Supersede};
            }
            g_hookSetEyeAngles.Continue(pawn, angle);
            return {KHook::Action::Ignore};
        }

        static KHook::Return<float *> BC_FASTCALL HookedGetEyeAngles(void *pawn, float *out)
        {
            int slot = pawn ? ControllerSlotForPawn(pawn) : -1;

            if (slot >= 0 && out && MotionRecorder::IsReplaying(slot))
            {
                MovementSnapshot view{};
                if (MotionRecorder::ReplaySpectatorView(slot, view))
                {
                    out[0] = view.pitch;
                    out[1] = NormalizeDeg(view.yaw);
                    out[2] = 0.0f;
                    return {KHook::Action::Supersede, out};
                }
            }

            return g_hookGetEyeAngles.Continue(pawn, out);
        }

        static bool ResolveAiTickedFlagFromUpdate(
            void *updateAddress,
            char *errorOut,
            size_t errorOutLen)
        {
#if defined(_WIN32)
            // The byte cleared by CCSBot::Update is not the similarly named
            // public Schema field on current builds. Decode the private target
            // from the required `C6 81 <disp32> 00` instruction itself.
            const auto *code = static_cast<const uint8_t *>(updateAddress);
            constexpr std::size_t kInstructionOffset = 28;
            if (code[kInstructionOffset] != 0xC6 ||
                code[kInstructionOffset + 1] != 0x81 ||
                code[kInstructionOffset + 6] != 0x00)
            {
                std::snprintf(errorOut, errorOutLen,
                              "CCSBot::Update AI-ticked store shape changed");
                return false;
            }

            int32_t offset = 0;
            std::memcpy(&offset, code + kInstructionOffset + 2, sizeof(offset));
            if (offset <= 0 || offset > 0x10000)
            {
                std::snprintf(errorOut, errorOutLen,
                              "CCSBot::Update AI-ticked offset invalid: 0x%X",
                              static_cast<unsigned>(offset));
                return false;
            }
            tg::kBot_AiTickedFlag = offset;
#else
            (void)updateAddress;
            (void)errorOut;
            (void)errorOutLen;
#endif
            return true;
        }

        // Resolve a sig from gamedata against the loaded server.dll.
        bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                     char *errorOut, size_t errorOutLen)
        {
            g_addrUpdate = Sig::ResolveSig(gd, serverModule, "CCSBot::Update",
                                           errorOut, errorOutLen);
            if (!g_addrUpdate)
            {
                g_status = "failed: Update sig";
                return false;
            }
            if (!ResolveAiTickedFlagFromUpdate(
                    g_addrUpdate,
                    errorOut,
                    errorOutLen))
            {
                g_status = "failed: AI-ticked offset";
                return false;
            }

            g_addrUpkeep = Sig::ResolveSig(gd, serverModule, "CCSBot::Upkeep",
                                           errorOut, errorOutLen);
            if (!g_addrUpkeep)
            {
                g_status = "failed: Upkeep sig";
                return false;
            }

            // Native 360-degree replay perception is optional. Failure keeps
            // ordinary native FOV behavior and the managed fallback detector.
            char ivpErr[256] = {0};
            g_addrIsVisiblePos = Sig::ResolveSig(gd, serverModule,
                                                 "CCSBot::IsVisiblePos",
                                                 ivpErr, sizeof(ivpErr));
            if (!g_addrIsVisiblePos)
            {
                char dbg[320];
                std::snprintf(dbg, sizeof(dbg),
                              "[BotController] WARN: CCSBot::IsVisible(pos) sig not resolved (%s); native replay 360 partial/disabled\n",
                              ivpErr);
                DebugOut(dbg);
            }

            char ivplErr[256] = {0};
            g_addrIsVisiblePlayer = Sig::ResolveSig(gd, serverModule,
                                                    "CCSBot::IsVisiblePlayer",
                                                    ivplErr, sizeof(ivplErr));
            if (!g_addrIsVisiblePlayer)
            {
                char dbg[320];
                std::snprintf(dbg, sizeof(dbg),
                              "[BotController] WARN: CCSBot::IsVisible(player) sig not resolved (%s); native replay 360 partial/disabled\n",
                              ivplErr);
                DebugOut(dbg);
            }

            // UpdateLookAngles is optional
            char ulaErr[256] = {0};
            g_addrUpdateLookAngles = Sig::ResolveSig(gd, serverModule,
                                                     "CCSBot::UpdateLookAngles",
                                                     ulaErr, sizeof(ulaErr));
            if (!g_addrUpdateLookAngles)
            {
                char dbg[320];
                std::snprintf(dbg, sizeof(dbg),
                              "[BotController] WARN: CCSBot::UpdateLookAngles sig not resolved (%s); replay view-drive disabled\n",
                              ulaErr);
                DebugOut(dbg);
            }

            // Both hooks are required: replay owns local angle writes and the
            // final getter consumed by the engine's normal network publisher.
            g_addrSetEyeAngles = Sig::ResolveSig(
                gd, serverModule, "CCSPlayerPawn::SetEyeAngles", errorOut, errorOutLen);
            if (!g_addrSetEyeAngles)
            {
                g_status = "failed: SetEyeAngles sig";
                return false;
            }
            g_addrGetEyeAngles = Sig::ResolveSig(
                gd, serverModule, "CBasePlayerPawn::GetEyeAngles", errorOut, errorOutLen);
            if (!g_addrGetEyeAngles)
            {
                g_status = "failed: GetEyeAngles sig";
                return false;
            }

            void *ladderUpdate = Sig::ResolveSig(gd, serverModule, "CCSBot::LadderStateUpdate", errorOut, errorOutLen);
            g_invalidatePath = reinterpret_cast<Update_t>(Sig::ResolveSig(gd, serverModule, "CCSBot::InvalidatePath", errorOut, errorOutLen));
            if (!ladderUpdate || !g_invalidatePath ||
                !g_hookLadderUpdate.Create(ladderUpdate, &HookedLadderUpdate, &g_origLadderUpdate) ||
                !g_hookLadderUpdate.Enable())
            {
                g_hookLadderUpdate.Remove();
                g_origLadderUpdate = nullptr;
                g_status = "failed: replay ladder navigation hook";
                return false;
            }

            // required: Update
            if (!g_hookUpdate.Create(g_addrUpdate,
                                     &HookedUpdate,
                                     &g_origUpdate) ||
                !g_hookUpdate.Enable())
            {
                std::snprintf(errorOut, errorOutLen, "hook CCSBot::Update failed");
                Remove();
                g_status = "failed: hook Update";
                return false;
            }

            // required: Upkeep
            if (!g_hookUpkeep.Create(g_addrUpkeep,
                                     &HookedUpkeep,
                                     &g_origUpkeep) ||
                !g_hookUpkeep.Enable())
            {
                std::snprintf(errorOut, errorOutLen, "hook CCSBot::Upkeep failed");
                Remove();
                g_status = "failed: hook Upkeep";
                return false;
            }

            // optional: native replay vision
            if (g_addrIsVisiblePos)
            {
                if (!g_hookIsVisiblePos.Create(g_addrIsVisiblePos,
                                                &HookedIsVisiblePos,
                                                &g_origIsVisiblePos) ||
                    !g_hookIsVisiblePos.Enable())
                {
                    DebugOut("[BotController] WARN: hook CCSBot::IsVisible(pos) failed; native replay 360 partial/disabled\n");
                    g_hookIsVisiblePos.Remove();
                    g_origIsVisiblePos = nullptr;
                    g_addrIsVisiblePos = nullptr;
                }
            }

            if (g_addrIsVisiblePlayer)
            {
                if (!g_hookIsVisiblePlayer.Create(g_addrIsVisiblePlayer,
                                                   &HookedIsVisiblePlayer,
                                                   &g_origIsVisiblePlayer) ||
                    !g_hookIsVisiblePlayer.Enable())
                {
                    DebugOut("[BotController] WARN: hook CCSBot::IsVisible(player) failed; native replay 360 partial/disabled\n");
                    g_hookIsVisiblePlayer.Remove();
                    g_origIsVisiblePlayer = nullptr;
                    g_addrIsVisiblePlayer = nullptr;
                }
            }

            // optional: UpdateLookAngles
            if (g_addrUpdateLookAngles)
            {
                if (!g_hookUpdateLookAngles.Create(g_addrUpdateLookAngles,
                                                   &HookedUpdateLookAngles,
                                                   &g_origUpdateLookAngles) ||
                    !g_hookUpdateLookAngles.Enable())
                {
                    DebugOut("[BotController] WARN: hook UpdateLookAngles failed; replay view-drive disabled\n");
                    g_hookUpdateLookAngles.Remove();
                    g_origUpdateLookAngles = nullptr;
                    g_addrUpdateLookAngles = nullptr;
                }
            }

            if (!g_hookSetEyeAngles.Create(g_addrSetEyeAngles,
                                           &HookedSetEyeAngles,
                                           &g_origSetEyeAngles) ||
                !g_hookSetEyeAngles.Enable())
            {
                std::snprintf(errorOut, errorOutLen, "hook SetEyeAngles failed");
                Remove();
                g_status = "failed: hook SetEyeAngles";
                return false;
            }
            if (!g_hookGetEyeAngles.Create(g_addrGetEyeAngles,
                                           &HookedGetEyeAngles,
                                           &g_origGetEyeAngles) ||
                !g_hookGetEyeAngles.Enable())
            {
                std::snprintf(errorOut, errorOutLen, "hook GetEyeAngles failed");
                Remove();
                g_status = "failed: hook GetEyeAngles";
                return false;
            }

            g_status = "ok";

            char dbg[400];
            std::snprintf(dbg, sizeof(dbg),
                          "[BotController] Update@%p Upkeep@%p IVPos@%p IVPlayer@%p ULA@%p SEA@%p GEA@%p\n",
                          g_addrUpdate, g_addrUpkeep, g_addrIsVisiblePos,
                          g_addrIsVisiblePlayer,
                          g_addrUpdateLookAngles, g_addrSetEyeAngles,
                          g_addrGetEyeAngles);
            DebugOut(dbg);
            return true;
        }

        void Remove()
        {
            for (int slot = 0; slot < 64; ++slot) ReleaseReplayNavigation(slot);
            g_hookLadderUpdate.Remove();
            g_origLadderUpdate = nullptr;
            g_invalidatePath = nullptr;
            // Also roll back partially installed required view hooks.
            g_hookGetEyeAngles.Remove();
            g_origGetEyeAngles = nullptr;
            g_hookSetEyeAngles.Remove();
            g_origSetEyeAngles = nullptr;
            g_hookUpdateLookAngles.Remove();
            g_origUpdateLookAngles = nullptr;
            g_hookIsVisiblePlayer.Remove();
            g_origIsVisiblePlayer = nullptr;
            g_addrIsVisiblePlayer = nullptr;
            g_hookIsVisiblePos.Remove();
            g_origIsVisiblePos = nullptr;
            g_addrIsVisiblePos = nullptr;
            g_hookUpkeep.Remove();
            g_origUpkeep = nullptr;
            g_hookUpdate.Remove();
            g_origUpdate = nullptr;
            g_nativePerception.fill({});
            for (size_t slot = 0; slot < g_pendingBestWeaponBots.size(); ++slot)
            {
                g_pendingBestWeaponBots[slot].store(nullptr, std::memory_order_release);
            }
            g_nativePerceptionSerial = 0;
            g_status = "not_attempted";
        }

        const char *Status() { return g_status.c_str(); }
        void *UpdateAddress() { return g_addrUpdate; }
        void *UpkeepAddress() { return g_addrUpkeep; }
        void *UpdateLookAnglesAddress() { return g_addrUpdateLookAngles; }
        void *SetEyeAnglesAddress() { return g_addrSetEyeAngles; }
        void *GetEyeAnglesAddress() { return g_addrGetEyeAngles; }
    }
}
