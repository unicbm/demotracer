// Lock dispatch: routes per LockKind to the right state table.

#include "dispatch.h"
#include "WeaponLockerState.h"
#include "WeaponLocker.h"
#include "BotControllerState.h"

#include <eiface.h>
#include <playerslot.h>

namespace BotController
{
    namespace Dispatch
    {
        IVEngineServer2 *g_pEngine = nullptr;
        ISource2GameClients *g_pGameClients = nullptr;

        // Set lock; Weapon also triggers a one-shot switch.
        int Lock(int slot, LockKind kind, int arg, bool quiet)
        {
            switch (kind)
            {
            case LockKind::All:
                if (slot < 0 || slot >= BotControllerState::kMaxSlots)
                    return -2;
                BotControllerState::SetAll(slot, true);
                return 0;

            case LockKind::Aim:
                if (slot < 0 || slot >= BotControllerState::kMaxSlots)
                    return -2;
                BotControllerState::SetAim(slot, true);
                return 0;

            case LockKind::Weapon:
            {
                if (slot < 0 || slot >= WeaponLockerState::kMaxSlots)
                    return -2;
                const auto tgt = static_cast<LockTarget>(arg);
                if (tgt == LockTarget::None)
                    return -2;
                WeaponLockerState::Set(slot, tgt);
                (void)WeaponLockerHooks::SwitchToLockTarget(slot, quiet);
                return 0;
            }

            case LockKind::Jump:
                if (slot < 0 || slot >= BotControllerState::kMaxSlots)
                    return -2;
                BotControllerState::SetJump(slot, true);
                return 0;
            }
            return -2;
        }

        // Clear the per-kind lock for this slot.
        int Unlock(int slot, LockKind kind, bool /*quiet*/)
        {
            switch (kind)
            {
            case LockKind::All:
                if (slot < 0 || slot >= BotControllerState::kMaxSlots)
                    return -2;
                BotControllerState::SetAll(slot, false);
                return 0;

            case LockKind::Aim:
                if (slot < 0 || slot >= BotControllerState::kMaxSlots)
                    return -2;
                BotControllerState::SetAim(slot, false);
                return 0;

            case LockKind::Weapon:
                if (slot < 0 || slot >= WeaponLockerState::kMaxSlots)
                    return -2;
                WeaponLockerState::Clear(slot);
                return 0;

            case LockKind::Jump:
                if (slot < 0 || slot >= BotControllerState::kMaxSlots)
                    return -2;
                BotControllerState::SetJump(slot, false);
                return 0;
            }
            return -2;
        }

        // Clear every slot under kind.
        int UnlockAll(LockKind kind, bool /*quiet*/)
        {
            switch (kind)
            {
            case LockKind::All:
                BotControllerState::ClearAllAll();
                return 0;
            case LockKind::Aim:
                BotControllerState::ClearAllAim();
                return 0;
            case LockKind::Weapon:
                WeaponLockerState::ClearAll();
                return 0;
            case LockKind::Jump:
                BotControllerState::ClearAllJump();
                return 0;
            }
            return -2;
        }

        // Return lock state for this slot under kind.
        int IsLocked(int slot, LockKind kind)
        {
            switch (kind)
            {
            case LockKind::All:
                return BotControllerState::GetAll(slot) ? 1 : 0;
            case LockKind::Aim:
                return BotControllerState::GetAim(slot) ? 1 : 0;
            case LockKind::Weapon:
                return static_cast<int>(WeaponLockerState::Get(slot));
            case LockKind::Jump:
                return BotControllerState::GetJump(slot) ? 1 : 0;
            }
            return 0;
        }
    }
}
