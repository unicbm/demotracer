#include "live_entities.h"
#include "ccsbot_slot.h"
#include "schema_resolver.h"
#include "sig_scan.h"
#include "version_targets.h"

namespace BotController::LiveEntities
{
    namespace
    {
        void *service = nullptr;
        int systemOffset = -1, chunksOffset = -1, identitySize = -1;
        int pawnOffset = -1, takeoverOffset = -1, botOffset = -1;

        void *Identity(int index)
        {
            if (!service || index <= 0 || index >= 0x8000) return nullptr;
            void *system = nullptr, *chunk = nullptr;
            if (!SafeRead(service, systemOffset, system) || !system ||
                !SafeRead(system, chunksOffset + (index >> 9) * sizeof(void *), chunk) || !chunk)
                return nullptr;
            return static_cast<char *>(chunk) + (index & 0x1ff) * identitySize;
        }
    }

    bool Init(void *resourceService, const nlohmann::json &gd)
    {
        Reset();
        systemOffset = Sig::FindPlatformOffset(gd, "GameResourceServiceServer::EntitySystem", -1);
        chunksOffset = Sig::FindPlatformOffset(gd, "CEntitySystem::IdentityChunks", -1);
        identitySize = Sig::FindPlatformOffset(gd, "CEntityIdentity::Size", -1);
        pawnOffset = Schema::GetFieldOffset("CCSPlayerController", "m_hPlayerPawn");
        takeoverOffset = Schema::GetFieldOffset("CCSPlayerController", "m_bControllingBot");
        botOffset = Schema::GetFieldOffset("CCSPlayerPawn", "m_pBot");
        if (!resourceService || systemOffset < 0 || chunksOffset < 0 || identitySize <= 0 ||
            pawnOffset < 0 || takeoverOffset < 0 || botOffset < 0) return false;
        service = resourceService;
        return true;
    }

    void Reset() { service = nullptr; }

    void *FromHandle(uint32_t handle)
    {
        if (handle == 0 || handle == 0xffffffffu || handle == 0xfffffffeu) return nullptr;
        void *identity = Identity(handle & 0x7fffu), *entity = nullptr;
        uint32_t current = 0;
        return identity && SafeRead(identity, targets::kEntIdentity_EHandle, current) && current == handle &&
            SafeRead(identity, 0, entity) ? entity : nullptr;
    }

    void *PawnForSlot(int slot)
    {
        if (slot < 0 || slot >= 64) return nullptr;
        void *identity = Identity(slot + 1), *controller = nullptr;
        uint32_t pawnHandle = 0, ownerHandle = 0;
        uint8_t takeover = 1;
        if (!identity || !SafeRead(identity, 0, controller) || !controller ||
            !SafeRead(controller, takeoverOffset, takeover) || takeover ||
            !SafeRead(controller, pawnOffset, pawnHandle)) return nullptr;
        void *pawn = FromHandle(pawnHandle);
        if (!pawn || !SafeRead(pawn, targets::kPawn_Controller, ownerHandle) ||
            FromHandle(ownerHandle) != controller) return nullptr;
        return pawn;
    }

    uint32_t HandleForEntity(const void *entity)
    {
        void *identity = nullptr;
        uint32_t handle = 0;
        return entity && SafeRead(entity, targets::kEnt_Identity, identity) && identity &&
            SafeRead(identity, targets::kEntIdentity_EHandle, handle) &&
            FromHandle(handle) == entity ? handle : 0;
    }

    void *BotPawnForSlot(int slot)
    {
        void *pawn = PawnForSlot(slot), *bot = nullptr, *botPawn = nullptr;
        return pawn && SafeRead(pawn, botOffset, bot) && bot &&
            SafeRead(bot, targets::kBot_Pawn, botPawn) && botPawn == pawn ? pawn : nullptr;
    }

    void *BotForSlot(int slot)
    {
        void *pawn = BotPawnForSlot(slot), *bot = nullptr;
        return pawn && SafeRead(pawn, botOffset, bot) ? bot : nullptr;
    }
}
