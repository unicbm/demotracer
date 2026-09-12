#include "ButtonState.h"

#include <array>
#include <cstdint>
#include <cstdio>
#include <cstdlib>

namespace
{
    using BotController::ButtonState::Decode;
    using BotController::ButtonState::EncodeAdjacentHeld;

    [[noreturn]] void Fail(const char *message)
    {
        std::fprintf(stderr, "FAIL: %s\n", message);
        std::exit(1);
    }

    void Check(bool condition, const char *message)
    {
        if (!condition)
            Fail(message);
    }

    void TestAllButtonStateCodes()
    {
        struct Expected
        {
            bool held;
            bool pressed;
            bool released;
        };
        constexpr std::array<Expected, 8> expected{{
            {false, false, false},
            {true, false, false},
            {false, false, true},
            {true, true, false},
            {false, true, true},
            {true, true, true},
            {false, true, true},
            {true, true, true},
        }};
        constexpr std::uint64_t bit = 1ULL << 5;

        for (std::size_t code = 0; code < expected.size(); ++code)
        {
            const std::uint64_t state1 = (code & 1) != 0 ? bit : 0;
            const std::uint64_t state2 = (code & 2) != 0 ? bit : 0;
            const std::uint64_t state3 = (code & 4) != 0 ? bit : 0;
            const auto decoded = Decode(state1, state2, state3);
            Check(((decoded.held & bit) != 0) == expected[code].held,
                  "held mask decoded incorrectly");
            Check(((decoded.pressed & bit) != 0) == expected[code].pressed,
                  "pressed mask decoded incorrectly");
            Check(((decoded.released & bit) != 0) == expected[code].released,
                  "released mask decoded incorrectly");
        }
    }

    void TestAdjacentHeldEncoding()
    {
        constexpr std::uint64_t pressedBit = 1ULL << 1;
        constexpr std::uint64_t releasedBit = 1ULL << 2;
        constexpr std::uint64_t stableBit = 1ULL << 3;
        const auto planes = EncodeAdjacentHeld(
            pressedBit | stableBit, releasedBit | stableBit);
        const auto decoded = Decode(
            planes.state1, planes.state2, planes.state3);

        Check(planes.state1 == (pressedBit | stableBit),
              "encoded held plane is wrong");
        Check(planes.state2 == (pressedBit | releasedBit),
              "encoded transition plane is wrong");
        Check(planes.state3 == 0,
              "adjacent held encoding unexpectedly used state3");
        Check(decoded.held == (pressedBit | stableBit),
              "encoded held mask did not round-trip");
        Check(decoded.pressed == pressedBit,
              "encoded press edge did not round-trip");
        Check(decoded.released == releasedBit,
              "encoded release edge did not round-trip");
    }
} // namespace

int main()
{
    TestAllButtonStateCodes();
    TestAdjacentHeldEncoding();
    std::puts("BotController button-state tests passed");
    return 0;
}
