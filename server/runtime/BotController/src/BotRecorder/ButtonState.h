#pragma once

#include <cstdint>

namespace BotController::ButtonState
{
    struct DecodedMasks
    {
        std::uint64_t held;
        std::uint64_t pressed;
        std::uint64_t released;
    };

    struct Planes
    {
        std::uint64_t state1;
        std::uint64_t state2;
        std::uint64_t state3;
    };

    constexpr DecodedMasks Decode(std::uint64_t state1,
                                  std::uint64_t state2,
                                  std::uint64_t state3) noexcept
    {
        return {
            state1,
            state3 | (state1 & state2),
            state3 | (~state1 & state2),
        };
    }

    constexpr Planes EncodeAdjacentHeld(std::uint64_t currentHeld,
                                        std::uint64_t previousHeld) noexcept
    {
        return {currentHeld, currentHeld ^ previousHeld, 0};
    }

    constexpr Planes OverrideOwnedEdges(Planes states, std::uint64_t previousHeld,
                                       std::uint64_t ownedMask) noexcept
    {
        const auto adjacent = EncodeAdjacentHeld(states.state1, previousHeld);
        return {states.state1,
                (states.state2 & ~ownedMask) | (adjacent.state2 & ownedMask),
                states.state3 & ~ownedMask};
    }
} // namespace BotController::ButtonState
