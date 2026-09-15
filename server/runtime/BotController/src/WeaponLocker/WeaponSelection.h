#pragma once

namespace BotController::WeaponSelection
{
    template <typename Select>
    bool Apply(void *active, void *target, Select select)
    {
        if (!target) return false;
        if (active == target) return true;
        return select(target) != 0;
    }
    constexpr bool ValidLockTarget(int target) { return target >= 1 && target <= 5; }
}
