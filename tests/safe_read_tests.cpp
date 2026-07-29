#include "ccsbot_slot.h"

#include <cstdint>
#include <cstring>

namespace BotController
{
    static bool g_failAfterPartialRead = false;

    bool TryReadMemory(const void *base, int offset, void *out, size_t size)
    {
        if (!base || !out || offset < 0 || size == 0)
            return false;

        const auto *source = static_cast<const std::byte *>(base) + offset;
        if (g_failAfterPartialRead)
        {
            std::memcpy(out, source, size / 2);
            return false;
        }

        std::memcpy(out, source, size);
        return true;
    }
}

int main()
{
    constexpr uint64_t source = 0x1122334455667788ULL;
    constexpr uint64_t sentinel = 0xA5A5A5A5A5A5A5A5ULL;

    uint64_t out = sentinel;
    BotController::g_failAfterPartialRead = true;
    if (BotController::SafeRead(&source, 0, out) || out != sentinel)
        return 1;

    BotController::g_failAfterPartialRead = false;
    if (!BotController::SafeRead(&source, 0, out) || out != source)
        return 2;

    return 0;
}
