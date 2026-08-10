// Replay-owned pawn equipment initialization.

#include "ReplayPawnEquipment.h"

#include "ccsbot_slot.h"
#include "version_targets.h"

#include <entity2/entityinstance.h>

#include <array>
#include <atomic>

namespace tg = BotController::targets;

namespace BotController::ReplayPawnEquipment
{
    namespace
    {
        struct SlotState
        {
            std::atomic<bool> configured{false};
            std::atomic<bool> pending{false};
            std::atomic<bool> applied{false};
            std::atomic<void *> pawn{nullptr};
            std::atomic<void *> controller{nullptr};
            std::atomic<int> armor{0};
            std::atomic<uint8_t> helmet{0};
            std::atomic<uint8_t> defuser{0};
        };

        std::array<SlotState, kMaxSlots> g_slots{};

        bool ValidSlot(int slot)
        {
            return slot >= 0 && slot < kMaxSlots;
        }

        void MarkNetworkStateChanged(void *entity, int offset)
        {
            if (!entity || offset <= 0)
                return;
            NetworkStateChangedData data(static_cast<uint32_t>(offset));
            reinterpret_cast<CEntityInstance *>(entity)->NetworkStateChanged(data);
        }

        bool ReadCurrent(const SlotState &state,
                         int &pawnArmor,
                         int &controllerArmor,
                         uint8_t &itemHelmet,
                         uint8_t &controllerHelmet,
                         uint8_t &itemDefuser,
                         uint8_t &controllerDefuser)
        {
            void *pawn = state.pawn.load(std::memory_order_acquire);
            void *controller = state.controller.load(std::memory_order_acquire);
            void *itemServices = nullptr;
            return pawn && controller &&
                   SafeRead(pawn, tg::kPawn_ItemServices, itemServices) &&
                   itemServices &&
                   SafeRead(pawn, tg::kPawn_ArmorValue, pawnArmor) &&
                   SafeRead(controller, tg::kController_PawnArmor, controllerArmor) &&
                   SafeRead(itemServices, tg::kItemServices_HasHelmet, itemHelmet) &&
                   SafeRead(controller, tg::kController_PawnHasHelmet, controllerHelmet) &&
                   SafeRead(itemServices, tg::kItemServices_HasDefuser, itemDefuser) &&
                   SafeRead(controller, tg::kController_PawnHasDefuser, controllerDefuser);
        }

        bool Apply(int slot)
        {
            if (!ValidSlot(slot))
                return false;

            SlotState &state = g_slots[slot];
            if (!state.configured.load(std::memory_order_acquire))
                return true;

            void *pawn = state.pawn.load(std::memory_order_acquire);
            void *controller = state.controller.load(std::memory_order_acquire);
            if (!pawn || !controller ||
                ControllerSlotForPawn(pawn) != slot ||
                ControllerToSlot(controller) != slot)
            {
                return false;
            }

            void *itemServices = nullptr;
            if (!SafeRead(pawn, tg::kPawn_ItemServices, itemServices) || !itemServices)
                return false;

            const int armor = state.armor.load(std::memory_order_relaxed);
            const uint8_t helmet = state.helmet.load(std::memory_order_relaxed);
            const uint8_t defuser = state.defuser.load(std::memory_order_relaxed);
            if (!WriteField(pawn, tg::kPawn_ArmorValue, armor) ||
                !WriteField(itemServices, tg::kItemServices_HasHelmet, helmet) ||
                !WriteField(itemServices, tg::kItemServices_HasDefuser, defuser) ||
                !WriteField(controller, tg::kController_PawnArmor, armor) ||
                !WriteField(controller, tg::kController_PawnHasHelmet, helmet) ||
                !WriteField(controller, tg::kController_PawnHasDefuser, defuser))
            {
                return false;
            }

            MarkNetworkStateChanged(pawn, tg::kPawn_ArmorValue);
            MarkNetworkStateChanged(pawn, tg::kPawn_ItemServices);
            MarkNetworkStateChanged(controller, tg::kController_PawnArmor);
            MarkNetworkStateChanged(controller, tg::kController_PawnHasHelmet);
            MarkNetworkStateChanged(controller, tg::kController_PawnHasDefuser);

            int livePawnArmor = -1;
            int liveControllerArmor = -1;
            uint8_t liveItemHelmet = 0;
            uint8_t liveControllerHelmet = 0;
            uint8_t liveItemDefuser = 0;
            uint8_t liveControllerDefuser = 0;
            const bool matches =
                ReadCurrent(state,
                            livePawnArmor,
                            liveControllerArmor,
                            liveItemHelmet,
                            liveControllerHelmet,
                            liveItemDefuser,
                            liveControllerDefuser) &&
                livePawnArmor == armor &&
                liveControllerArmor == armor &&
                liveItemHelmet == helmet &&
                liveControllerHelmet == helmet &&
                liveItemDefuser == defuser &&
                liveControllerDefuser == defuser;
            state.applied.store(matches, std::memory_order_release);
            return matches;
        }
    }

    bool Set(int slot, void *pawn, void *controller,
             int armor, bool helmet, bool defuser)
    {
        if (!ValidSlot(slot) || !pawn || !controller ||
            armor < 0 || armor > 100 ||
            ControllerSlotForPawn(pawn) != slot ||
            ControllerToSlot(controller) != slot)
        {
            return false;
        }

        SlotState &state = g_slots[slot];
        state.configured.store(false, std::memory_order_release);
        state.pawn.store(pawn, std::memory_order_relaxed);
        state.controller.store(controller, std::memory_order_relaxed);
        state.armor.store(armor, std::memory_order_relaxed);
        state.helmet.store(helmet ? 1 : 0, std::memory_order_relaxed);
        state.defuser.store(defuser ? 1 : 0, std::memory_order_relaxed);
        state.applied.store(false, std::memory_order_relaxed);
        state.pending.store(true, std::memory_order_relaxed);
        state.configured.store(true, std::memory_order_release);

        // Keep one hook-side pass armed even when this immediate application
        // succeeds. It settles any engine write that occurs later in the spawn
        // frame without enforcing equipment throughout combat.
        return Apply(slot);
    }

    bool PrepareForReplayStart(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        if (!g_slots[slot].configured.load(std::memory_order_acquire))
            return true;
        return Apply(slot);
    }

    bool ApplyPendingForPawn(int slot, void *pawn)
    {
        if (!ValidSlot(slot))
            return false;
        SlotState &state = g_slots[slot];
        if (!state.configured.load(std::memory_order_acquire))
        {
            return true;
        }
        // Consume this generation's hook-side pass before writing. Even a
        // failed readback must not turn round-start initialization into a
        // combat-time armor refill loop.
        if (!state.pending.exchange(false, std::memory_order_acq_rel))
            return true;
        if (!pawn || state.pawn.load(std::memory_order_acquire) != pawn)
        {
            state.applied.store(false, std::memory_order_release);
            return false;
        }
        return Apply(slot);
    }

    bool Clear(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        SlotState &state = g_slots[slot];
        state.configured.store(false, std::memory_order_release);
        state.pending.store(false, std::memory_order_relaxed);
        state.applied.store(false, std::memory_order_relaxed);
        state.pawn.store(nullptr, std::memory_order_relaxed);
        state.controller.store(nullptr, std::memory_order_relaxed);
        state.armor.store(0, std::memory_order_relaxed);
        state.helmet.store(0, std::memory_order_relaxed);
        state.defuser.store(0, std::memory_order_relaxed);
        return true;
    }

    void ClearAll()
    {
        for (int slot = 0; slot < kMaxSlots; ++slot)
            Clear(slot);
    }

    bool GetState(int slot, State &out)
    {
        if (!ValidSlot(slot))
            return false;

        SlotState &state = g_slots[slot];
        out = {};
        out.configured = state.configured.load(std::memory_order_acquire) ? 1 : 0;
        out.pending = state.pending.load(std::memory_order_acquire) ? 1 : 0;
        out.applied = state.applied.load(std::memory_order_acquire) ? 1 : 0;
        out.expectedArmor = state.armor.load(std::memory_order_relaxed);
        out.expectedHelmet = state.helmet.load(std::memory_order_relaxed);
        out.expectedDefuser = state.defuser.load(std::memory_order_relaxed);
        out.pawnArmor = -1;
        out.controllerArmor = -1;
        out.itemServicesHelmet = -1;
        out.controllerHelmet = -1;
        out.itemServicesDefuser = -1;
        out.controllerDefuser = -1;

        int pawnArmor = -1;
        int controllerArmor = -1;
        uint8_t itemHelmet = 0;
        uint8_t controllerHelmet = 0;
        uint8_t itemDefuser = 0;
        uint8_t controllerDefuser = 0;
        if (out.configured &&
            ReadCurrent(state,
                        pawnArmor,
                        controllerArmor,
                        itemHelmet,
                        controllerHelmet,
                        itemDefuser,
                        controllerDefuser))
        {
            out.pawnArmor = pawnArmor;
            out.controllerArmor = controllerArmor;
            out.itemServicesHelmet = itemHelmet;
            out.controllerHelmet = controllerHelmet;
            out.itemServicesDefuser = itemDefuser;
            out.controllerDefuser = controllerDefuser;
        }
        return true;
    }
}
