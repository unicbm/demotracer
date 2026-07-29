// steamid_provider.h
//
// Pool of pre-defined SteamID64 values used to brand fake clients

#pragma once

#include <cstddef>
#include <cstdint>

namespace cs2bh
{

    class SteamIdProvider
    {
    public:
        explicit SteamIdProvider(const char *sessionId);

        // Returns a SteamID64
        uint64_t Generate(int botSlot) const noexcept;

        static constexpr const char *kMode = "real_pool";

        static size_t PoolSize() noexcept;

    private:
        uint64_t m_SessionSeed = 0;
    };

} // namespace cs2bh
