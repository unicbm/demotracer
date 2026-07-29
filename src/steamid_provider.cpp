// steamid_provider.cpp
//
// SteamID pool sourced from bot_info.json

#include "steamid_provider.h"

#include "bot_info.h"

namespace cs2bh
{

    // Pool size reflects the loaded bot_info.json entries.
    size_t SteamIdProvider::PoolSize() noexcept { return BotInfo().Count(); }

    // FNV-1a 64-bit over the session id string.
    SteamIdProvider::SteamIdProvider(const char *sessionId)
    {
        uint64_t h = 0xCBF29CE484222325ULL;
        if (sessionId)
        {
            for (const char *p = sessionId; *p; ++p)
            {
                h = (h ^ static_cast<unsigned char>(*p)) * 0x100000001B3ULL;
            }
        }
        m_SessionSeed = h;
    }

    // Splitmix64-style avalanche on (seed XOR slot*phi64), reduced modulo
    // the bot_info.json pool size to pick an entry's SteamID64. Stable per
    // slot within a session; shuffles across sessions via the boot-time seed.
    uint64_t SteamIdProvider::Generate(int botSlot) const noexcept
    {
        const auto &entries = BotInfo().All();
        if (entries.empty())
            return 0;
        uint64_t x = m_SessionSeed ^ (static_cast<uint64_t>(botSlot) * 0x9E3779B97F4A7C15ULL);
        x ^= x >> 33;
        x *= 0xFF51AFD7ED558CCDULL;
        x ^= x >> 33;
        x *= 0xC4CEB9FE1A85EC53ULL;
        x ^= x >> 33;
        return entries[x % entries.size()].SteamId64;
    }

} // namespace cs2bh
