// Replay-owned pawn equipment initialization.

#include "ReplayPawnEquipment.h"
#include "ReplayNetworkState.h"

#include "ccsbot_slot.h"
#include "version_targets.h"
#include "live_entities.h"

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
            std::atomic<uint32_t> pawnHandle{0};
            std::atomic<uint32_t> controllerHandle{0};
            std::atomic<int> armor{0};
            std::atomic<uint8_t> helmet{0};
            std::atomic<uint8_t> defuser{0};
        };

        std::array<SlotState, kMaxSlots> g_slots{};

        bool ValidSlot(int slot)
        {
            return slot >= 0 && slot < kMaxSlots;
        }

        template <typename T>
        bool SetIfChanged(void *owner, int offset, T expected, void (*notify)(void *, int))
        {
            T current;
            if (!SafeRead(owner, offset, current))
                return false;
            if (current == expected)
                return true;
            if (!WriteField(owner, offset, expected))
                return false;
            notify(owner, offset);
            return true;
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
                   LiveEntities::FromHandle(state.pawnHandle.load(std::memory_order_relaxed)) == pawn &&
                   LiveEntities::FromHandle(state.controllerHandle.load(std::memory_order_relaxed)) == controller &&
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

            state.applied.store(false, std::memory_order_release);
            void *pawn = state.pawn.load(std::memory_order_acquire);
            void *controller = state.controller.load(std::memory_order_acquire);
            if (!pawn || !controller ||
                LiveEntities::BotPawnForSlot(slot) != pawn ||
                LiveEntities::FromHandle(state.pawnHandle.load(std::memory_order_relaxed)) != pawn ||
                LiveEntities::FromHandle(state.controllerHandle.load(std::memory_order_relaxed)) != controller ||
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
            using namespace ReplayNetworkState;
            const bool matches =
                SetIfChanged(pawn, tg::kPawn_ArmorValue, armor, EntityFieldChanged) &&
                SetIfChanged(itemServices, tg::kItemServices_HasHelmet, helmet, PawnComponentFieldChanged) &&
                SetIfChanged(itemServices, tg::kItemServices_HasDefuser, defuser, PawnComponentFieldChanged) &&
                SetIfChanged(controller, tg::kController_PawnArmor, armor, EntityFieldChanged) &&
                SetIfChanged(controller, tg::kController_PawnHasHelmet, helmet, EntityFieldChanged) &&
                SetIfChanged(controller, tg::kController_PawnHasDefuser, defuser, EntityFieldChanged);
            state.applied.store(matches, std::memory_order_release);
            return matches;
        }
    }

    bool Set(int slot, void *pawn, void *controller,
             int armor, bool helmet, bool defuser)
    {
        if (!ValidSlot(slot) || !pawn || !controller ||
            armor < 0 || armor > 100 ||
            LiveEntities::BotPawnForSlot(slot) != pawn ||
            ControllerToSlot(controller) != slot)
        {
            return false;
        }
        const uint32_t pawnHandle = LiveEntities::HandleForEntity(pawn);
        const uint32_t controllerHandle = LiveEntities::HandleForEntity(controller);
        if (!pawnHandle || !controllerHandle) return false;

        SlotState &state = g_slots[slot];
        state.configured.store(false, std::memory_order_release);
        state.pawn.store(pawn, std::memory_order_relaxed);
        state.controller.store(controller, std::memory_order_relaxed);
        state.pawnHandle.store(pawnHandle, std::memory_order_relaxed);
        state.controllerHandle.store(controllerHandle, std::memory_order_relaxed);
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

    bool PrepareForReplayStart(int slot, bool resumesHeldReplay)
    {
        if (!ValidSlot(slot))
            return false;
        const auto &state = g_slots[slot];
        if (!state.configured.load(std::memory_order_acquire))
            return true;
        // Pre-roll and live start share a pawn. Only retry a failed spawn
        // initialization here (e.g. ItemServices was still unavailable).
        if (resumesHeldReplay && state.applied.load(std::memory_order_acquire))
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
        // Both command and movement hooks poll this one-shot pass. Keep the
        // completed path read-only, then claim pending work with the exchange.
        if (!state.pending.load(std::memory_order_acquire))
            return true;
        // Consume this generation's hook-side pass before writing. Even a
        // failed application must not turn round-start initialization into a
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
        state.pawnHandle.store(0, std::memory_order_relaxed);
        state.controllerHandle.store(0, std::memory_order_relaxed);
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
