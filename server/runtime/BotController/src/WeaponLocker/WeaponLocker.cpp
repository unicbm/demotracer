// Detours for CCSBot::EquipBestWeapon, EquipPistol, and
// CCSPlayer_WeaponServices::SelectItem

#include "WeaponLocker.h"
#include "sig_scan.h"
#include "WeaponLockerState.h"
#include "ccsbot_slot.h"
#include "MotionRecorder.h"
#include "version_targets.h"
#include "hook.h"
#include "platform.h"

#include <cstdio>
#include <array>
#include <atomic>
#include <vector>
#include <mutex>
#include <unordered_map>

namespace tg = BotController::targets;

using EquipBestWeapon_t = void(BC_FASTCALL *)(void *self, char mustEquip);
using EquipPistol_t = void(BC_FASTCALL *)(void *self, char mustEquip);
using SelectItem_t = char(BC_FASTCALL *)(void *ws, void *weapon, int flag);
using GetSlot_t = void *(BC_FASTCALL *)(void *ws, int slot, unsigned int mask);

namespace BotController
{
    namespace WeaponLockerHooks
    {
        static EquipBestWeapon_t g_origEquipBestWeapon = nullptr;
        static EquipPistol_t g_origEquipPistol = nullptr;
        static SelectItem_t g_origSelectItem = nullptr;
        static GetSlot_t g_pGetSlot = nullptr;

        static void *g_addrEquipBestWeapon = nullptr;
        static void *g_addrEquipPistol = nullptr;
        static void *g_addrSelectItem = nullptr;
        static void *g_addrGetSlot = nullptr;

        static Hook g_hookEquipBestWeapon;
        static Hook g_hookEquipPistol;
        static Hook g_hookSelectItem;

        static std::string g_status = "not_attempted";
        static bool g_installed = false;

        // WeaponServices* -> (bot slot, pawn)
        struct WsBinding
        {
            int slot;
            void *pawn;
        };
        static std::unordered_map<void *, WsBinding> g_wsToBinding;
        // Inverse: slot -> WeaponServices*. Replay reads this every usercmd,
        // so keep it lock-free while the forward map remains mutex-guarded.
        static std::array<std::atomic<void *>, 64> g_slotToWs{};
        static std::mutex g_wsToSlotMu;

        static void RememberWsForBot(void *bot, int slot)
        {
            if (!bot || slot < 0 || slot >= 64)
                return;
            void *pawn = nullptr;
            if (!SafeRead(bot, tg::kBot_Pawn, pawn))
                return;
            if (!pawn)
                return;
            void *ws = nullptr;
            if (!SafeRead(pawn, tg::kPawn_WeaponServices, ws))
                return;
            if (!ws)
                return;
            std::lock_guard<std::mutex> lk(g_wsToSlotMu);
            g_wsToBinding[ws] = {slot, pawn};
            g_slotToWs[slot].store(ws, std::memory_order_release);
        }

        static WsBinding LookupBindingForWs(void *ws)
        {
            if (!ws)
                return {-1, nullptr};
            std::lock_guard<std::mutex> lk(g_wsToSlotMu);
            auto it = g_wsToBinding.find(ws);
            return it == g_wsToBinding.end() ? WsBinding{-1, nullptr} : it->second;
        }

        // LockTarget -> engine weapon-slot index
        static int LockTargetToEngineSlot(LockTarget t)
        {
            int v = static_cast<int>(t);
            if (v < 1 || v > 5)
                return -1;
            return v - 1;
        }

        int ReadDefIndex(void *weapon);

        static bool IsKnifeDefIndex(int def)
        {
            return def == kKnifeDef || def == 41 || def == 42 || def == 59 ||
                   (def >= 500 && def < 600);
        }

        static bool ReplayAllowsWeaponSelection(int slot, void *weapon)
        {
            ReplayTick tick{};
            if (!MotionRecorder::ReplayTickForSimulation(slot, tick))
                return false;

            const int desiredDef = tick.weaponDefIndex;
            const int selectedDef = ReadDefIndex(weapon);
            if (desiredDef < 0 || selectedDef < 0)
                return false;

            return selectedDef == desiredDef ||
                   (IsKnifeDefIndex(selectedDef) && IsKnifeDefIndex(desiredDef));
        }

        // ---- detours ----

        static void BC_FASTCALL HookedEquipBestWeapon(void *bot, char mustEquip)
        {
            auto sr = ResolveSlot(bot);
            if (sr.slot >= 0)
                RememberWsForBot(bot, sr.slot);
            LockTarget lt = (sr.slot >= 0) ? WeaponLockerState::Get(sr.slot) : LockTarget::None;
            if (sr.slot >= 0 &&
                (MotionRecorder::IsReplaying(sr.slot) || lt != LockTarget::None))
                return;
            g_origEquipBestWeapon(bot, mustEquip);
        }

        static void BC_FASTCALL HookedEquipPistol(void *bot, char mustEquip)
        {
            auto sr = ResolveSlot(bot);
            if (sr.slot >= 0)
                RememberWsForBot(bot, sr.slot);
            LockTarget lt = (sr.slot >= 0) ? WeaponLockerState::Get(sr.slot) : LockTarget::None;
            if (sr.slot >= 0 &&
                (MotionRecorder::IsReplaying(sr.slot) || lt != LockTarget::None))
                return;
            g_origEquipPistol(bot, mustEquip);
        }

        static char BC_FASTCALL HookedSelectItem(void *ws, void *weapon, int flag)
        {
            // Recording : a human switching weapons calls SelectItem
            if (weapon)
            {
                int def = ReadDefIndex(weapon);
                if (def >= 0)
                    for (int s = 0; s < MotionRecorder::kMaxSlots; ++s)
                    {
                        if (MotionRecorder::IsRecording(s) &&
                            MotionRecorder::LiveWs(s) == ws)
                            MotionRecorder::SetCurrentDef(s, def);
                    }
            }

            WsBinding bind = LookupBindingForWs(ws);
            if (bind.slot < 0)
                return g_origSelectItem(ws, weapon, flag);

            // Human took over this pawn -> current m_hController != bot slot
            // we cached; don't block player's weapon switches.
            int curSlot = ControllerSlotForPawn(bind.pawn);
            if (curSlot != bind.slot)
                return g_origSelectItem(ws, weapon, flag);

            // Native Update/Upkeep may shadow-run during replay for warm
            // perception, but replay remains the sole weapon-action owner.
            // The DTR raw switch helper calls g_origSelectItem directly and
            // therefore bypasses this detour. Engine handling of the injected
            // cmd.weaponselect is allowed only for the exact replay weapon.
            if (MotionRecorder::IsReplaying(bind.slot))
            {
                if (!ReplayAllowsWeaponSelection(bind.slot, weapon))
                    return 0;
                return g_origSelectItem(ws, weapon, flag);
            }

            LockTarget lt = WeaponLockerState::Get(bind.slot);
            if (lt == LockTarget::None)
                return g_origSelectItem(ws, weapon, flag);

            int engineSlot = LockTargetToEngineSlot(lt);
            if (engineSlot < 0 || !g_pGetSlot)
                return g_origSelectItem(ws, weapon, flag);

            void *targetWeapon = g_pGetSlot(ws, engineSlot, 0xFFFFFFFFu);
            // No weapon in the locked slot -> can't enforce, let it through.
            if (!targetWeapon)
                return g_origSelectItem(ws, weapon, flag);

            // Switch is to the lock target -> allow.
            if (weapon == targetWeapon)
                return g_origSelectItem(ws, weapon, flag);

            // Switch is to something else -> block.
            return 0;
        }

        // ---- install / remove ----

        bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                     char *errorOut, size_t errorOutLen)
        {
            g_addrEquipBestWeapon = Sig::ResolveSig(gd, serverModule,
                                                    "CCSBot::EquipBestWeapon",
                                                    errorOut, errorOutLen);
            if (!g_addrEquipBestWeapon)
            {
                g_status = "failed: EquipBestWeapon sig";
                return false;
            }

            g_addrEquipPistol = Sig::ResolveSig(gd, serverModule,
                                                "CCSBot::EquipPistol",
                                                errorOut, errorOutLen);
            if (!g_addrEquipPistol)
            {
                g_status = "failed: EquipPistol sig";
                return false;
            }

            g_addrSelectItem = Sig::ResolveSig(gd, serverModule,
                                               "CCSPlayer_WeaponServices::SelectItem",
                                               errorOut, errorOutLen);
            if (!g_addrSelectItem)
            {
                g_status = "failed: SelectItem sig";
                return false;
            }

            g_addrGetSlot = Sig::ResolveSig(gd, serverModule,
                                            "CCSPlayer_WeaponServices::GetSlot",
                                            errorOut, errorOutLen);
            if (!g_addrGetSlot)
            {
                g_status = "failed: GetSlot sig";
                return false;
            }
            g_pGetSlot = reinterpret_cast<GetSlot_t>(g_addrGetSlot);

            auto failCleanup = [&](const char *what) -> bool
            {
                std::snprintf(errorOut, errorOutLen, "%s failed", what);
                g_hookEquipBestWeapon.Remove();
                g_hookEquipPistol.Remove();
                g_hookSelectItem.Remove();
                g_origEquipBestWeapon = nullptr;
                g_origEquipPistol = nullptr;
                g_origSelectItem = nullptr;
                return false;
            };

            if (!g_hookEquipBestWeapon.Create(g_addrEquipBestWeapon,
                                              reinterpret_cast<void *>(&HookedEquipBestWeapon),
                                              reinterpret_cast<void **>(&g_origEquipBestWeapon)))
            {
                g_status = "failed: Create EquipBestWeapon";
                return failCleanup("Create EquipBestWeapon");
            }

            if (!g_hookEquipPistol.Create(g_addrEquipPistol,
                                          reinterpret_cast<void *>(&HookedEquipPistol),
                                          reinterpret_cast<void **>(&g_origEquipPistol)))
            {
                g_status = "failed: Create EquipPistol";
                return failCleanup("Create EquipPistol");
            }

            if (!g_hookSelectItem.Create(g_addrSelectItem,
                                         reinterpret_cast<void *>(&HookedSelectItem),
                                         reinterpret_cast<void **>(&g_origSelectItem)))
            {
                g_status = "failed: Create SelectItem";
                return failCleanup("Create SelectItem");
            }

            if (!g_hookEquipBestWeapon.Enable() || !g_hookEquipPistol.Enable() ||
                !g_hookSelectItem.Enable())
            {
                g_status = "failed: Enable";
                return failCleanup("Enable");
            }

            g_installed = true;
            g_status = "ok";

            char dbg[384];
            std::snprintf(dbg, sizeof(dbg),
                          "[BotWeaponLock] hooks installed: EquipBestWeapon=%p EquipPistol=%p SelectItem=%p GetSlot=%p\n",
                          g_addrEquipBestWeapon, g_addrEquipPistol,
                          g_addrSelectItem, g_addrGetSlot);
            DebugOut(dbg);
            return true;
        }

        void Remove()
        {
            if (!g_installed)
                return;
            g_hookSelectItem.Remove();
            g_hookEquipPistol.Remove();
            g_hookEquipBestWeapon.Remove();
            g_origEquipBestWeapon = nullptr;
            g_origEquipPistol = nullptr;
            g_origSelectItem = nullptr;
            g_installed = false;
            g_status = "not_attempted";
            {
                std::lock_guard<std::mutex> lk(g_wsToSlotMu);
                g_wsToBinding.clear();
                for (int i = 0; i < 64; ++i)
                    g_slotToWs[i].store(nullptr, std::memory_order_release);
            }
        }

        const char *Status() { return g_status.c_str(); }
        void *EquipBestWeaponAddress() { return g_addrEquipBestWeapon; }
        void *EquipPistolAddress() { return g_addrEquipPistol; }
        void *SelectItemAddress() { return g_addrSelectItem; }
        void *GetSlotAddress() { return g_addrGetSlot; }

        bool EquipBestWeaponRaw(void *bot, bool mustEquip)
        {
            if (!bot || !g_installed || !g_origEquipBestWeapon)
                return false;
            g_origEquipBestWeapon(bot, mustEquip ? 1 : 0);
            return true;
        }

        // ---- MotionRecorder helpers ----

        bool WeaponHooksReady()
        {
            return g_installed && g_pGetSlot && g_origSelectItem;
        }

        int ReadDefIndex(void *weapon)
        {
            if (!weapon)
                return -1;
            uint16_t def = 0;
            if (!SafeRead(weapon, tg::kWeapon_ItemDefIndex, def))
                return -1;
            return def;
        }

        // entity -> identity(0x10) -> m_EHandle(0x10), low 15 bits = index.
        static int EntIndexOf(void *entity)
        {
            if (!entity)
                return -1;
            void *identity = nullptr;
            if (!SafeRead(entity, tg::kEnt_Identity, identity))
                return -1;
            if (!identity)
                return -1;
            uint32_t h = 0;
            if (!SafeRead(identity, tg::kEntIdentity_EHandle, h))
                return -1;
            if (h == 0u || h == 0xFFFFFFFFu)
                return -1;
            return static_cast<int>(h & 0x7FFFu);
        }

        // entity index of a weapon, for cmd.weaponselect on replay.
        int WeaponEntIndex(void *weapon)
        {
            return EntIndexOf(weapon);
        }

        int ActiveWeaponDef(void *ws)
        {
            if (!ws || !g_pGetSlot)
                return -1;
            // m_hActiveWeapon is a handle; resolve it by matching its entity
            // index against the pointers GetSlot returns
            const int activeIdx = ActiveWeaponEntIndex(ws);
            if (activeIdx < 0)
                return -1;
            for (int slot = 0; slot <= 4; ++slot)
            {
                // GEAR_SLOT_GRENADES (3) holds every grenade type at once
                unsigned int maxPos = (slot == 3) ? 8u : 1u;
                for (unsigned int pos = 0; pos < maxPos; ++pos)
                {
                    unsigned int posArg = (slot == 3) ? pos : 0xFFFFFFFFu;
                    void *w = g_pGetSlot(ws, slot, posArg);
                    if (w && EntIndexOf(w) == activeIdx)
                    {
                        int def = ReadDefIndex(w);
                        // Engine slot 2 holds knife AND taser. Normalize any
                        // knife skin to kKnifeDef; keep the taser (31) as-is.
                        if (slot == 2 && def != 31)
                            return kKnifeDef;
                        return def;
                    }
                }
            }
            return -1;
        }

        int ActiveWeaponEntIndex(void *ws)
        {
            if (!ws)
                return -1;
            uint32_t activeH = 0;
            if (!SafeRead(ws, tg::kWs_ActiveWeapon, activeH) ||
                activeH == 0u || activeH == 0xFFFFFFFFu)
                return -1;
            return static_cast<int>(activeH & 0x7FFFu);
        }

        void *FindWeaponByDef(void *ws, int def,
                              int *engineSlot,
                              unsigned int *position)
        {
            if (engineSlot)
                *engineSlot = -1;
            if (position)
                *position = 0xFFFFFFFFu;
            if (!ws || def < 0 || !g_pGetSlot)
                return nullptr;
            // kKnifeDef means "the bot's own slot-2 knife", whatever skin it is.
            if (def == kKnifeDef)
            {
                void *weapon = g_pGetSlot(ws, 2, 0xFFFFFFFFu);
                const int knifeDef = ReadDefIndex(weapon);
                if (knifeDef < 0 || knifeDef == 31)
                    return nullptr;
                if (weapon && engineSlot)
                    *engineSlot = 2;
                return weapon;
            }
            const int activeEntIndex = ActiveWeaponEntIndex(ws);
            void *firstMatch = nullptr;
            int firstMatchSlot = -1;
            unsigned int firstMatchPosition = 0xFFFFFFFFu;
            // Non-grenade gear slots hold one weapon each
            for (int slot = 0; slot <= 4; ++slot)
            {
                if (slot == 3)
                    continue;
                void *w = g_pGetSlot(ws, slot, 0xFFFFFFFFu);
                if (w && ReadDefIndex(w) == def)
                {
                    if (WeaponEntIndex(w) == activeEntIndex)
                    {
                        if (engineSlot)
                            *engineSlot = slot;
                        return w;
                    }
                    if (!firstMatch)
                    {
                        firstMatch = w;
                        firstMatchSlot = slot;
                    }
                }
            }
            // GEAR_SLOT_GRENADES (3) holds every grenade type at once
            for (unsigned int pos = 0; pos < 8; ++pos)
            {
                void *w = g_pGetSlot(ws, 3, pos);
                if (w && ReadDefIndex(w) == def)
                {
                    if (WeaponEntIndex(w) == activeEntIndex)
                    {
                        if (engineSlot)
                            *engineSlot = 3;
                        if (position)
                            *position = pos;
                        return w;
                    }
                    if (!firstMatch)
                    {
                        firstMatch = w;
                        firstMatchSlot = 3;
                        firstMatchPosition = pos;
                    }
                }
            }
            if (firstMatch && engineSlot)
                *engineSlot = firstMatchSlot;
            if (firstMatch && position)
                *position = firstMatchPosition;
            return firstMatch;
        }

        void *WeaponAtInventoryPosition(void *ws, int engineSlot,
                                        unsigned int position)
        {
            if (!ws || !g_pGetSlot || engineSlot < 0 || engineSlot > 4)
                return nullptr;
            if (engineSlot == 3)
            {
                if (position >= 8u)
                    return nullptr;
                return g_pGetSlot(ws, engineSlot, position);
            }
            return g_pGetSlot(ws, engineSlot, 0xFFFFFFFFu);
        }

        bool SelectWeaponRaw(void *ws, void *weapon)
        {
            if (!ws || !weapon || !g_origSelectItem)
                return false;
            g_origSelectItem(ws, weapon, 0);
            return true;
        }

        void *WsForSlot(int slot)
        {
            if (slot < 0 || slot >= 64)
                return nullptr;
            return g_slotToWs[slot].load(std::memory_order_acquire);
        }

        int SwitchToLockTarget(int slot)
        {
            if (!g_installed || !g_origSelectItem || !g_pGetSlot)
                return 3;
            if (slot < 0 || slot >= 64)
                return 3;

            LockTarget lt = WeaponLockerState::Get(slot);
            if (lt == LockTarget::None)
                return 3;
            int engineSlot = LockTargetToEngineSlot(lt);
            if (engineSlot < 0)
                return 3;

            void *ws = nullptr;
            ws = g_slotToWs[slot].load(std::memory_order_acquire);
            if (!ws)
                return 1; // bot hasn't ticked yet; lock will still take effect once AI runs.

            void *target = g_pGetSlot(ws, engineSlot, 0xFFFFFFFFu);
            if (!target)
                return 2;

            // Route through the original (un-hooked) function so we don't
            // ping-pong through HookedSelectItem.
            g_origSelectItem(ws, target, 0);
            return 0;
        }
    }
}
