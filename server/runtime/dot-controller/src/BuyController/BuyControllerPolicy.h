#pragma once

#include <cstdint>

namespace BotController::BuyControllerHooks
{
    enum class BuyUpdateAction : uint8_t
    {
        None,
        ForceSkip,
        ApplyPlan,
    };

    constexpr BuyUpdateAction DecideBuyUpdate(
        bool skip,
        uint8_t initialDelay,
        uint8_t previousInitialDelay) noexcept
    {
        if (skip)
            return BuyUpdateAction::ForceSkip;
        return initialDelay != 0 && previousInitialDelay == 0
                   ? BuyUpdateAction::ApplyPlan
                   : BuyUpdateAction::None;
    }
}
