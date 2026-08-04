// CCSBot Update/Upkeep/Jump detours

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
using Jump_t = char(BC_FASTCALL *)(void *bot, char mustJump);
using UpdateLookAngles_t = void(BC_FASTCALL *)(void *bot);
using SetEyeAngles_t = void(BC_FASTCALL *)(void *pawn, float *angle);
using GetEyeAngles_t = float *(BC_FASTCALL *)(void *pawn, float *out);

namespace BotController
{
    namespace BotControllerHooks
    {
        static Update_t g_origUpdate = nullptr;
        static void *g_addrUpdate = nullptr;
        static Upkeep_t g_origUpkeep = nullptr;
        static void *g_addrUpkeep = nullptr;
        static IsVisiblePos_t g_origIsVisiblePos = nullptr;
        static void *g_addrIsVisiblePos = nullptr;
        static IsVisiblePlayer_t g_origIsVisiblePlayer = nullptr;
        static void *g_addrIsVisiblePlayer = nullptr;
        static Jump_t g_origJump = nullptr;
        static void *g_addrJump = nullptr;
        static UpdateLookAngles_t g_origUpdateLookAngles = nullptr;
        static void *g_addrUpdateLookAngles = nullptr;
        static SetEyeAngles_t g_origSetEyeAngles = nullptr;
        static void *g_addrSetEyeAngles = nullptr;
        static GetEyeAngles_t g_origGetEyeAngles = nullptr;
        static void *g_addrGetEyeAngles = nullptr;
        static thread_local bool g_replayOwnedSetEyeAngles = false;
#if defined(_WIN32)
        static void **g_ppEntityIdentityChunks = nullptr;
#endif
        static bool g_installed = false;
        static std::string g_status = "not_attempted";

        static Hook g_hookUpdate;
        static Hook g_hookUpkeep;
        static Hook g_hookIsVisiblePos;
        static Hook g_hookIsVisiblePlayer;
        static Hook g_hookJump;
        static Hook g_hookUpdateLookAngles;
        static Hook g_hookSetEyeAngles;
        static Hook g_hookGetEyeAngles;
        static std::array<NativePerceptionState, 64> g_nativePerception{};
        static std::array<std::atomic<void *>, 64> g_observedBots{};
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
            if (slot < 0 || slot >= static_cast<int>(g_observedBots.size()) ||
                !WeaponLockerHooks::EquipBestWeaponAddress())
            {
                return false;
            }

            void *bot = g_observedBots[static_cast<size_t>(slot)].load(
                std::memory_order_acquire);
            if (!bot || CCSBotToSlot(bot) != slot)
                return false;
            if (InputInjector::IsSlotControllingBot(slot))
                return false;

            g_pendingBestWeaponBots[static_cast<size_t>(slot)].store(
                bot, std::memory_order_release);
            return true;
        }

        static float NormalizeDeg(float a)
        {
            a = std::fmod(a + 180.0f, 360.0f);
            if (a < 0.0f)
                a += 360.0f;
            return a - 180.0f;
        }

#if defined(_WIN32)
        // Current CCSPlayerPawn::SetEyeAngles resolves m_hController through
        // this RIP-relative identity-chunk pointer before testing FL_FAKECLIENT.
        // Resolve that same pointer from the verified function body so replay
        // publication can pass the new bot-only early-out without adding a new
        // engine global signature.
        static void ResolveSetEyeAnglesEntityChunks(void *setEyeAngles)
        {
            g_ppEntityIdentityChunks = nullptr;
            if (!setEyeAngles)
                return;

            constexpr size_t kSearchBytes = 0x120;
            uint8_t code[kSearchBytes]{};
            if (!TryReadMemory(setEyeAngles, 0, code, sizeof(code)))
                return;

            auto *functionBase = reinterpret_cast<uint8_t *>(setEyeAngles);
            for (size_t i = 0; i + 10 <= kSearchBytes; ++i)
            {
                if (code[i + 0] != 0x4C || code[i + 1] != 0x8B || code[i + 2] != 0x05 ||
                    code[i + 7] != 0x4D || code[i + 8] != 0x85 || code[i + 9] != 0xC0)
                    continue;

                int32_t rel = 0;
                std::memcpy(&rel, code + i + 3, sizeof(rel));
                g_ppEntityIdentityChunks = reinterpret_cast<void **>(functionBase + i + 7 + rel);
                return;
            }
        }

        static void *ReplayControllerForPawn(void *pawn)
        {
            if (!pawn || !g_ppEntityIdentityChunks)
                return nullptr;

            uint32_t handle = 0;
            if (!SafeRead(pawn, tg::kPawn_Controller, handle) ||
                handle == 0xFFFFFFFFu || handle == 0xFFFFFFFEu)
                return nullptr;

            void *chunks = nullptr;
            if (!SafeRead(g_ppEntityIdentityChunks, 0, chunks) || !chunks)
                return nullptr;

            const uint32_t entityIndex = handle & 0x7FFFu;
            void *chunk = nullptr;
            if (!SafeRead(chunks,
                          static_cast<int>((entityIndex >> 9) * sizeof(void *)),
                          chunk) ||
                !chunk)
                return nullptr;

            constexpr int kIdentitySize = 0x70;
            auto *identity = reinterpret_cast<uint8_t *>(chunk) +
                             static_cast<size_t>(entityIndex & 0x1FFu) * kIdentitySize;
            uint32_t liveHandle = 0;
            void *controller = nullptr;
            if (!SafeRead(identity, 0x10, liveHandle) || liveHandle != handle ||
                !SafeRead(identity, 0x00, controller))
                return nullptr;
            return controller;
        }
#endif

        // All is an explicit full-brain lock. Replay itself keeps Update alive
        // so native perception and decision state can shadow the injected
        // command stream and be ready when replay control is released.
        static void BC_FASTCALL HookedUpdate(void *bot)
        {
            int slot = CCSBotToSlot(bot);
            if (slot >= 0 && slot < static_cast<int>(g_observedBots.size()))
            {
                g_observedBots[static_cast<size_t>(slot)].store(
                    bot, std::memory_order_release);
            }
            if (slot >= 0 && BotControllerState::GetAll(slot))
            {
                const uint8_t ticked = 1;
                WriteField(bot, tg::kBot_AiTickedFlag, ticked);
                return;
            }
            g_origUpdate(bot);
            CaptureNativePerception(bot, slot);

            if (slot < 0 || slot >= static_cast<int>(g_pendingBestWeaponBots.size()))
                return;

            void *requestedBot = g_pendingBestWeaponBots[static_cast<size_t>(slot)].exchange(
                nullptr, std::memory_order_acq_rel);
            if (requestedBot != bot ||
                MotionRecorder::IsReplaying(slot) ||
                InputInjector::IsSlotControllingBot(slot) ||
                BotControllerState::GetAll(slot) ||
                WeaponLockerState::Get(slot) != LockTarget::None)
            {
                return;
            }

            // The native AI has just refreshed its combat state. Give it one
            // authoritative weapon choice now that DTR no longer owns output.
            WeaponLockerHooks::EquipBestWeaponRaw(bot, /*mustEquip=*/true);
        }

        // CS:GO botmimic keeps native vision running and disables only the FOV
        // cone. Do the same while a DTR owns output: native LOS, smoke and
        // target-state logic still run, but rear threats can enter the native
        // perception/reaction pipeline before handoff.
        static bool BC_FASTCALL HookedIsVisiblePos(void *bot, const void *pos,
                                                   bool testFov, void *traceContext)
        {
            int slot = CCSBotToSlot(bot);
            if (g_replayNativeFovOverride && slot >= 0 && MotionRecorder::IsReplaying(slot))
                testFov = false;
            return g_origIsVisiblePos(bot, pos, testFov, traceContext);
        }

        static bool BC_FASTCALL HookedIsVisiblePlayer(void *bot, void *playerPawn,
                                                      bool testFov, uint8_t *visibleParts)
        {
            int slot = CCSBotToSlot(bot);
            if (g_replayNativeFovOverride && slot >= 0 && MotionRecorder::IsReplaying(slot))
                testFov = false;
            return g_origIsVisiblePlayer(bot, playerPawn, testFov, visibleParts);
        }

        // Skip the per-frame view tick under replay, All, or Aim lock.
        static void BC_FASTCALL HookedUpdateLookAngles(void *bot); // fwd decl

        static void BC_FASTCALL HookedUpkeep(void *bot)
        {
            int slot = CCSBotContextToSlot(bot);
            if (slot >= 0 &&
                (BotControllerState::GetAll(slot) || BotControllerState::GetAim(slot)))
            {
                return;
            }
            g_origUpkeep(bot);
        }

        static void BC_FASTCALL HookedUpdateLookAngles(void *bot)
        {
            int slot = CCSBotContextToSlot(bot);
            // Keep replay POV authoritative. The rest of Upkeep still runs so
            // non-view native state remains warm for handoff.
            if (slot >= 0 &&
                (MotionRecorder::IsReplaying(slot) ||
                 BotControllerState::GetAll(slot) ||
                 BotControllerState::GetAim(slot)))
            {
                return;
            }
            g_origUpdateLookAngles(bot);
        }

        // Engine eye-angle
        static void BC_FASTCALL HookedSetEyeAngles(void *pawn, float *angle)
        {
            int slot = pawn ? ControllerSlotForPawn(pawn) : -1;
            if (slot >= 0 && MotionRecorder::IsReplaying(slot) && !g_replayOwnedSetEyeAngles &&
                !MotionRecorder::ReplayViewAllowsEngineSetEyeAngles())
            {
                return;
            }
            g_origSetEyeAngles(pawn, angle);
        }

        bool ApplyReplayEyeAngles(void *pawn, float pitch, float yaw)
        {
            if (!pawn || !g_origSetEyeAngles)
                return false;

            float angle[3] = {pitch, NormalizeDeg(yaw), 0.0f};
#if defined(_WIN32)
            // July 2026 SetEyeAngles skips its server/spectator publication
            // block when the owning controller has FL_FAKECLIENT. Bypass that
            // gate only for this replay-owned call and restore it immediately.
            void *controller = ReplayControllerForPawn(pawn);
            uint32_t controllerFlags = 0;
            bool restoreFakeClient =
                controller && SafeRead(controller, tg::kEnt_Flags, controllerFlags) &&
                (controllerFlags & 0x100u) != 0;
            if (restoreFakeClient)
            {
                const uint32_t publishedFlags = controllerFlags & ~0x100u;
                restoreFakeClient = WriteField(controller, tg::kEnt_Flags, publishedFlags);
            }
#endif
            bool oldGuard = g_replayOwnedSetEyeAngles;
            g_replayOwnedSetEyeAngles = true;
            g_origSetEyeAngles(pawn, angle);
            g_replayOwnedSetEyeAngles = oldGuard;
#if defined(_WIN32)
            if (restoreFakeClient)
                WriteField(controller, tg::kEnt_Flags, controllerFlags);
#endif
            return true;
        }

        static float *BC_FASTCALL HookedGetEyeAngles(void *pawn, float *out)
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
                    return out;
                }
            }

            return g_origGetEyeAngles ? g_origGetEyeAngles(pawn, out) : out;
        }

        // Skip Jump under Jump lock; return 0 mimics its own gate-fail.
        static char BC_FASTCALL HookedJump(void *bot, char mustJump)
        {
            int slot = CCSBotToSlot(bot);
            if (slot >= 0 && BotControllerState::GetJump(slot))
                return 0;
            return g_origJump(bot, mustJump);
        }

        static bool ResolveAiTickedFlagFromUpdate(
            void *updateAddress,
            char *errorOut,
            size_t errorOutLen)
        {
#if defined(_WIN32)
            // The required Windows Update signature ends in
            // `C6 81 <disp32> 00` (mov byte ptr [rcx+disp32], 0). Decode the
            // private CCSBot flag from the matched function instead of
            // trusting a version-pinned gamedata offset.
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

            // Jump is optional; failure leaves all/aim working, only jump dies.
            char jumpErr[256] = {0};
            g_addrJump = Sig::ResolveSig(gd, serverModule, "CCSBot::Jump",
                                         jumpErr, sizeof(jumpErr));
            if (!g_addrJump)
            {
                char dbg[320];
                std::snprintf(dbg, sizeof(dbg),
                              "[BotController] WARN: CCSBot::Jump sig not resolved (%s); jump-lock disabled\n",
                              jumpErr);
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

            // SetEyeAngles is optional; without it replay view falls back to
            // the (smoothing) UpdateLookAngles hook only.
            char seaErr[256] = {0};
            g_addrSetEyeAngles = Sig::ResolveSig(gd, serverModule,
                                                 "CCSPlayerPawn::SetEyeAngles",
                                                 seaErr, sizeof(seaErr));
            if (!g_addrSetEyeAngles)
            {
                char dbg[320];
                std::snprintf(dbg, sizeof(dbg),
                              "[BotController] WARN: CCSPlayerPawn::SetEyeAngles sig not resolved (%s); replay 1:1 view disabled\n",
                              seaErr);
                DebugOut(dbg);
            }
#if defined(_WIN32)
            else
            {
                ResolveSetEyeAnglesEntityChunks(g_addrSetEyeAngles);
            }
#endif

            // GetEyeAngles is optional; replay can still drive server state
            // without it, but first-person spectator camera may read through
            // this getter instead of raw pawn fields.
            char geaErr[256] = {0};
            g_addrGetEyeAngles = Sig::ResolveSig(gd, serverModule,
                                                 "CBasePlayerPawn::GetEyeAngles",
                                                 geaErr, sizeof(geaErr));
            if (!g_addrGetEyeAngles)
            {
                char dbg[320];
                std::snprintf(dbg, sizeof(dbg),
                              "[BotController] WARN: CBasePlayerPawn::GetEyeAngles sig not resolved (%s); replay spectator view override disabled\n",
                              geaErr);
                DebugOut(dbg);
            }

            // required: Update
            if (!g_hookUpdate.Create(g_addrUpdate,
                                     reinterpret_cast<void *>(&HookedUpdate),
                                     reinterpret_cast<void **>(&g_origUpdate)) ||
                !g_hookUpdate.Enable())
            {
                std::snprintf(errorOut, errorOutLen, "hook CCSBot::Update failed");
                g_hookUpdate.Remove();
                g_origUpdate = nullptr;
                g_status = "failed: hook Update";
                return false;
            }

            // required: Upkeep
            if (!g_hookUpkeep.Create(g_addrUpkeep,
                                     reinterpret_cast<void *>(&HookedUpkeep),
                                     reinterpret_cast<void **>(&g_origUpkeep)) ||
                !g_hookUpkeep.Enable())
            {
                std::snprintf(errorOut, errorOutLen, "hook CCSBot::Upkeep failed");
                g_hookUpkeep.Remove();
                g_origUpkeep = nullptr;
                g_hookUpdate.Remove();
                g_origUpdate = nullptr;
                g_status = "failed: hook Upkeep";
                return false;
            }

            // optional: native replay vision
            if (g_addrIsVisiblePos)
            {
                if (!g_hookIsVisiblePos.Create(g_addrIsVisiblePos,
                                                reinterpret_cast<void *>(&HookedIsVisiblePos),
                                                reinterpret_cast<void **>(&g_origIsVisiblePos)) ||
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
                                                   reinterpret_cast<void *>(&HookedIsVisiblePlayer),
                                                   reinterpret_cast<void **>(&g_origIsVisiblePlayer)) ||
                    !g_hookIsVisiblePlayer.Enable())
                {
                    DebugOut("[BotController] WARN: hook CCSBot::IsVisible(player) failed; native replay 360 partial/disabled\n");
                    g_hookIsVisiblePlayer.Remove();
                    g_origIsVisiblePlayer = nullptr;
                    g_addrIsVisiblePlayer = nullptr;
                }
            }

            // optional: Jump
            if (g_addrJump)
            {
                if (!g_hookJump.Create(g_addrJump,
                                       reinterpret_cast<void *>(&HookedJump),
                                       reinterpret_cast<void **>(&g_origJump)) ||
                    !g_hookJump.Enable())
                {
                    DebugOut("[BotController] WARN: hook CCSBot::Jump failed; jump-lock disabled\n");
                    g_hookJump.Remove();
                    g_origJump = nullptr;
                    g_addrJump = nullptr;
                }
            }

            // optional: UpdateLookAngles
            if (g_addrUpdateLookAngles)
            {
                if (!g_hookUpdateLookAngles.Create(g_addrUpdateLookAngles,
                                                   reinterpret_cast<void *>(&HookedUpdateLookAngles),
                                                   reinterpret_cast<void **>(&g_origUpdateLookAngles)) ||
                    !g_hookUpdateLookAngles.Enable())
                {
                    DebugOut("[BotController] WARN: hook UpdateLookAngles failed; replay view-drive disabled\n");
                    g_hookUpdateLookAngles.Remove();
                    g_origUpdateLookAngles = nullptr;
                    g_addrUpdateLookAngles = nullptr;
                }
            }

            // optional: SetEyeAngles
            if (g_addrSetEyeAngles)
            {
                if (!g_hookSetEyeAngles.Create(g_addrSetEyeAngles,
                                               reinterpret_cast<void *>(&HookedSetEyeAngles),
                                               reinterpret_cast<void **>(&g_origSetEyeAngles)) ||
                    !g_hookSetEyeAngles.Enable())
                {
                    DebugOut("[BotController] WARN: hook SetEyeAngles failed; replay 1:1 view disabled\n");
                    g_hookSetEyeAngles.Remove();
                    g_origSetEyeAngles = nullptr;
                    g_addrSetEyeAngles = nullptr;
                }
            }

            // optional: GetEyeAngles
            if (g_addrGetEyeAngles)
            {
                if (!g_hookGetEyeAngles.Create(g_addrGetEyeAngles,
                                               reinterpret_cast<void *>(&HookedGetEyeAngles),
                                               reinterpret_cast<void **>(&g_origGetEyeAngles)) ||
                    !g_hookGetEyeAngles.Enable())
                {
                    DebugOut("[BotController] WARN: hook GetEyeAngles failed; replay spectator view override disabled\n");
                    g_hookGetEyeAngles.Remove();
                    g_origGetEyeAngles = nullptr;
                    g_addrGetEyeAngles = nullptr;
                }
            }

            g_installed = true;
            g_status = "ok";

            char dbg[400];
            std::snprintf(dbg, sizeof(dbg),
                          "[BotController] Update@%p Upkeep@%p IVPos@%p IVPlayer@%p Jump@%p ULA@%p SEA@%p GEA@%p\n",
                          g_addrUpdate, g_addrUpkeep, g_addrIsVisiblePos,
                          g_addrIsVisiblePlayer, g_addrJump,
                          g_addrUpdateLookAngles, g_addrSetEyeAngles,
                          g_addrGetEyeAngles);
            DebugOut(dbg);
            return true;
        }

        void Remove()
        {
            if (!g_installed)
                return;
            g_hookGetEyeAngles.Remove();
            g_origGetEyeAngles = nullptr;
            g_hookSetEyeAngles.Remove();
            g_origSetEyeAngles = nullptr;
#if defined(_WIN32)
            g_ppEntityIdentityChunks = nullptr;
#endif
            g_hookUpdateLookAngles.Remove();
            g_origUpdateLookAngles = nullptr;
            g_hookJump.Remove();
            g_origJump = nullptr;
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
            for (size_t slot = 0; slot < g_observedBots.size(); ++slot)
            {
                g_observedBots[slot].store(nullptr, std::memory_order_release);
                g_pendingBestWeaponBots[slot].store(nullptr, std::memory_order_release);
            }
            g_nativePerceptionSerial = 0;
            g_installed = false;
            g_status = "not_attempted";
        }

        const char *Status() { return g_status.c_str(); }
        void *UpdateAddress() { return g_addrUpdate; }
        void *UpkeepAddress() { return g_addrUpkeep; }
        void *JumpAddress() { return g_addrJump; }
        void *UpdateLookAnglesAddress() { return g_addrUpdateLookAngles; }
        void *SetEyeAnglesAddress() { return g_addrSetEyeAngles; }
        void *GetEyeAnglesAddress() { return g_addrGetEyeAngles; }
    }
}
