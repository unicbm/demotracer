#include "live_entities.h"
#include "ccsbot_slot.h"
#include "schema_resolver.h"
#include "sig_scan.h"
#include "version_targets.h"
#include <array>
#include <cstring>

namespace BotController
{
    bool TryReadMemory(const void *base, int offset, void *out, size_t size)
    {
        if (!base || offset < 0) return false;
        std::memcpy(out, static_cast<const char *>(base) + offset, size); return true;
    }
    int Schema::GetFieldOffset(const char *, const char *field)
    {
        if (std::strcmp(field, "m_pBot") == 0) return 32;
        return std::strcmp(field, "m_hPlayerPawn") == 0 ? 8 : 12;
    }
    int Sig::FindPlatformOffset(const nlohmann::json &, const std::string &name, int)
    {
        if (name == "GameResourceServiceServer::EntitySystem") return 8;
        return name == "CEntitySystem::IdentityChunks" ? 16 : 112;
    }
}
template <typename T> void Put(void *ptr, int offset, T value)
{
    std::memcpy(static_cast<char *>(ptr) + offset, &value, sizeof(value));
}
int main()
{
    using namespace BotController;
    std::array<char, 64> service{}, system{}, controller{}, oldPawn{}, newPawn{}, oldBot{}, newBot{};
    std::array<char, 112 * 512> identities{};
    constexpr uint32_t owner = 0x8001, oldHandle = 0x8002, replacement = 0x10002;
    Put(service.data(), 8, system.data()); Put(system.data(), 16, identities.data());
    Put(identities.data(), 112, controller.data()); Put(identities.data(), 112 + 16, owner);
    Put(identities.data(), 224, oldPawn.data()); Put(identities.data(), 224 + 16, oldHandle);
    Put(controller.data(), 8, oldHandle);
    targets::kPawn_Controller = 24;
    Put(oldPawn.data(), 24, owner); Put(newPawn.data(), 24, owner);
    Put(oldPawn.data(), 32, oldBot.data()); Put(newPawn.data(), 32, newBot.data());
    Put(oldBot.data(), targets::kBot_Pawn, oldPawn.data());
    Put(newBot.data(), targets::kBot_Pawn, newPawn.data());
    if (!LiveEntities::Init(service.data(), {})) return 1;
    if (LiveEntities::PawnForSlot(0) != oldPawn.data()) return 2;
    if (LiveEntities::BotForSlot(0) != oldBot.data()) return 8;
    Put(identities.data(), 224, newPawn.data()); Put(identities.data(), 224 + 16, replacement);
    if (LiveEntities::FromHandle(oldHandle) || LiveEntities::PawnForSlot(0)) return 3;
    Put(controller.data(), 8, replacement);
    if (LiveEntities::PawnForSlot(0) != newPawn.data()) return 4;
    if (LiveEntities::BotForSlot(0) != newBot.data()) return 9;
    Put(newPawn.data(), 32, oldBot.data());
    if (LiveEntities::BotForSlot(0)) return 10;
    Put(newPawn.data(), 32, newBot.data());
    Put(controller.data(), 12, uint8_t{1});
    if (LiveEntities::PawnForSlot(0)) return 5;
    Put(controller.data(), 12, uint8_t{0}); Put(newPawn.data(), 24, uint32_t{0x10001});
    if (LiveEntities::PawnForSlot(0)) return 6;
    LiveEntities::Reset();
    if (LiveEntities::FromHandle(replacement) || LiveEntities::PawnForSlot(0)) return 7;
}
