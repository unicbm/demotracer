// MinHook install/remove for CCSBot Update/Upkeep/Jump.

#pragma once

#include <cstdint>
#include <string>

#include <nlohmann/json.hpp>
#include "sig_scan.h"

namespace BotController
{
    // Game-thread snapshot of the native CCSBot perception state. Keep this
    // POD layout in sync with the C# P/Invoke definitions.
#pragma pack(push, 4)
    struct NativePerceptionState
    {
        int32_t valid;
        uint32_t enemyHandle;
        int32_t hasEnemy;
        int32_t enemyVisible;
        int32_t visibleEnemyParts;
        int32_t nearbyEnemyCount;
        int32_t lastEnemyDead;
        float lastSawEnemyTimestamp;
        float firstSawEnemyTimestamp;
        float currentEnemyAcquireTimestamp;
        uint32_t updateSerial;
    };
#pragma pack(pop)

    static_assert(sizeof(NativePerceptionState) == 44);

    namespace BotControllerHooks
    {
        // Resolve sigs and install detours.
        bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                     char *errorOut, size_t errorOutLen);

        // Disable + remove detours.
        void Remove();

        const char *Status();

        // Apply replay-owned eye angles through the native engine path so
        // derived third-person/body orientation state stays synchronized.
        bool ApplyReplayEyeAngles(void *pawn, float pitch, float yaw);

        // Latest snapshot captured after CCSBot::Update for this player slot.
        bool GetNativePerceptionState(int slot, NativePerceptionState &out);
        void SetReplayNativeFovOverride(bool enabled);

        // Queue one native EquipBestWeapon(true) for the current bot
        // incarnation. It is consumed after that bot's next Update, and is
        // discarded if replay/locks resumed or the slot recycled first.
        bool RequestEquipBestWeapon(int slot);

        void *UpdateAddress();
        void *UpkeepAddress();
        void *JumpAddress();
        void *UpdateLookAnglesAddress();
        void *SetEyeAnglesAddress();
        void *GetEyeAnglesAddress();
    }
}
