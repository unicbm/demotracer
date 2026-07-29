// MinHook for CCSBot EquipBestWeapon/EquipPistol + WeaponServices::SelectItem.

#pragma once

#include <cstdint>
#include <string>

#include <nlohmann/json.hpp>
#include "sig_scan.h"

namespace BotController
{
    namespace WeaponLockerHooks
    {
        // Sentinel def index meaning any knife
        constexpr int kKnifeDef = 9001;

        bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                     char *errorOut, size_t errorOutLen);

        void Remove();

        const char *Status();

        void *EquipBestWeaponAddress();
        void *EquipPistolAddress();
        void *SelectItemAddress();
        void *GetSlotAddress();

        // Invoke the engine's original CCSBot::EquipBestWeapon implementation.
        // This deliberately bypasses the replay selection detour and is only
        // used by the post-handoff one-shot owned by BotControllerHooks.
        bool EquipBestWeaponRaw(void *bot, bool mustEquip);

        // Force bot at `slot` to its locked weapon
        // Returns: 0 ok / 1 no ws / 2 no target / 3 hooks not installed.
        int SwitchToLockTarget(int slot, bool quiet = false);

        // ---- helpers for MotionRecorder ----

        // True once GetSlot + SelectItem are resolved and hooks installed.
        bool WeaponHooksReady();

        // Read a weapon's item-definition index (weapon+0x9E0). -1 if null.
        int ReadDefIndex(void *weapon);

        // Entity index of a weapon (identity ehandle low bits). -1 if null.
        // For writing cmd.weaponselect on replay (engine-native switch path).
        int WeaponEntIndex(void *weapon);

        // Active weapon's def index for a WeaponServices*, matched by entity
        // handle against GetSlot(0..4). -1 if none/unresolved (no false match).
        int ActiveWeaponDef(void *ws);

        // Active weapon entity index read directly from m_hActiveWeapon.
        // Unlike ActiveWeaponDef, this does not enumerate the inventory.
        int ActiveWeaponEntIndex(void *ws);

        // Matching weapon in slots 0..4, preferring the active entity when
        // duplicate defs exist. nullptr if absent. Optional location outputs
        // let replay cache the exact inventory cell and validate
        // give/drop/replacement changes with one GetSlot call.
        void *FindWeaponByDef(void *ws, int def,
                              int *engineSlot = nullptr,
                              unsigned int *position = nullptr);

        // Read one exact inventory cell. Non-grenade slots use position
        // 0xFFFFFFFF; grenade positions are 0..7.
        void *WeaponAtInventoryPosition(void *ws, int engineSlot,
                                        unsigned int position);

        // Switch via the original (un-hooked) SelectItem. Proven reliable path.
        bool SelectWeaponRaw(void *ws, void *weapon);

        // Cached WeaponServices* for a bot slot (populated when its AI ticks).
        void *WsForSlot(int slot);
    }
}
