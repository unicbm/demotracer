#include "ReplayPawnEquipment.h"
#include "ReplayNetworkState.h"
#include "ccsbot_slot.h"
#include "live_entities.h"
#include "version_targets.h"

#include <array>
#include <cstdio>
#include <cstdlib>

namespace equip = BotController::ReplayPawnEquipment;
namespace tg = BotController::targets;
namespace
{
    std::array<std::byte, 32> pawn{}, controller{}, items{};
    int writes = 0, entityChanges = 0, itemChanges = 0;
    bool isBot = true;
    void Check(bool condition, const char *message)
    {
        if (condition) return;
        std::fprintf(stderr, "%s\n", message);
        std::exit(1);
    }
}

namespace BotController
{
    bool TryReadMemory(const void *base, int offset, void *out, size_t size)
    {
        std::memcpy(out, static_cast<const std::byte *>(base) + offset, size);
        return true;
    }
    bool TryWriteMemory(void *base, int offset, const void *value, size_t size)
    {
        std::memcpy(static_cast<std::byte *>(base) + offset, value, size);
        ++writes;
        return true;
    }
    int ControllerToSlot(void *p) { return p == controller.data() ? 0 : -1; }
    namespace LiveEntities
    {
        void *BotPawnForSlot(int slot) { return isBot && slot == 0 ? pawn.data() : nullptr; }
        void *FromHandle(uint32_t h) { return h == 1 ? pawn.data() : h == 2 ? controller.data() : nullptr; }
        uint32_t HandleForEntity(const void *p) { return p == pawn.data() ? 1 : p == controller.data() ? 2 : 0; }
    }
    namespace ReplayNetworkState
    {
        void EntityFieldChanged(void *p, int offset)
        {
            Check((p == pawn.data() && offset == tg::kPawn_ArmorValue) ||
                  (p == controller.data() && (offset == tg::kController_PawnArmor ||
                   offset == tg::kController_PawnHasHelmet || offset == tg::kController_PawnHasDefuser)),
                  "unexpected entity notification");
            ++entityChanges;
        }
        void PawnComponentFieldChanged(void *p, int offset)
        {
            Check(p == items.data() && (offset == tg::kItemServices_HasHelmet ||
                  offset == tg::kItemServices_HasDefuser), "unexpected item component notification");
            ++itemChanges;
        }
    }
}

int main()
{
    tg::kPawn_ItemServices = 8;
    tg::kPawn_ArmorValue = tg::kController_PawnArmor = 4;
    tg::kItemServices_HasHelmet = tg::kController_PawnHasHelmet = 16;
    tg::kItemServices_HasDefuser = tg::kController_PawnHasDefuser = 17;
    void *itemServices = items.data();
    std::memcpy(pawn.data() + tg::kPawn_ItemServices, &itemServices, sizeof(itemServices));
    Check(equip::Set(0, pawn.data(), controller.data(), 0, false, false) &&
          equip::PrepareForReplayStart(0) && equip::ApplyPendingForPawn(0, pawn.data()) &&
          writes == 0 && entityChanges == 0 && itemChanges == 0, "unchanged equipment was published");
    Check(equip::Set(0, pawn.data(), controller.data(), 100, true, true) &&
          writes == 6 && entityChanges == 4 && itemChanges == 2, "six changed equipment fields");
    Check(equip::PrepareForReplayStart(0) && writes == 6, "repeat initialization");
    items[tg::kItemServices_HasHelmet] = std::byte{0};
    Check(equip::ApplyPendingForPawn(0, pawn.data()) && writes == 7 &&
          itemChanges == 3 && entityChanges == 4, "spawn overwrote only the item helmet");
    int damaged = 70;
    std::memcpy(pawn.data() + tg::kPawn_ArmorValue, &damaged, sizeof(damaged));
    equip::State live{};
    Check(equip::ApplyPendingForPawn(0, pawn.data()) && equip::PrepareForReplayStart(0, true) &&
          writes == 7 && equip::GetState(0, live) &&
          live.pawnArmor == 70 && live.controllerArmor == 100 && live.itemServicesHelmet == 1 &&
          live.applied && !live.pending, "first hook refilled combat damage");
    isBot = false;
    Check(!equip::PrepareForReplayStart(0) && writes == 7 && equip::GetState(0, live) &&
          !live.applied, "stale pawn accepted");
    isBot = true;
    void *missing = nullptr;
    std::memcpy(pawn.data() + tg::kPawn_ItemServices, &missing, sizeof(missing));
    Check(!equip::Set(0, pawn.data(), controller.data(), 60, true, true) &&
          !equip::ApplyPendingForPawn(0, pawn.data()), "missing item services accepted");
    std::memcpy(pawn.data() + tg::kPawn_ItemServices, &itemServices, sizeof(itemServices));
    Check(equip::PrepareForReplayStart(0, true) && equip::GetState(0, live) &&
          live.applied && live.pawnArmor == 60 && live.controllerArmor == 60,
          "live start did not finish a failed pre-roll equipment initialization");
}
