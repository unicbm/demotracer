// serversideclient_ref.h

#pragma once

#include <cstdint>
#include <cstring>

namespace cs2bh::ssc
{

    // Engine-private layout. Every value must come from the packaged gamedata;
    // retaining compiled fallbacks would turn a missing or stale file into
    // unchecked writes through an old CS2 layout.
    inline int OFFSET_m_Name = -1;          // CUtlString
    inline int OFFSET_m_nEntityIndex = -1;  // CEntityIndex (int)
    inline int OFFSET_m_NetChannel = -1;    // INetChannel*
    inline int OFFSET_m_nConnectionTypeFlags = -1; // byte, fake-client mask 0x08
    inline int OFFSET_m_bFakePlayer = -1; // bool
    inline int OFFSET_m_UserID = -1;      // short
    inline int OFFSET_m_SteamID = -1;     // CSteamID
    inline int OFFSET_m_SteamIDMirror = -1; // mirrored CSteamID
    inline int OFFSET_m_bIsHLTV = -1;     // bool

    // Read CUtlString { char* m_pString } at member offset
    inline const char *ReadName(const void *client)
    {
        if (!client)
            return nullptr;
        auto *utl = reinterpret_cast<const char *const *>(
            reinterpret_cast<const unsigned char *>(client) + OFFSET_m_Name);
        return *utl;
    }

    // sets m_bFakePlayer = 0
    inline void ClearFakePlayer(void *client)
    {
        auto *raw = reinterpret_cast<unsigned char *>(client);
        auto &connectionFlags = raw[OFFSET_m_nConnectionTypeFlags];
        connectionFlags = static_cast<unsigned char>((connectionFlags & ~0x08u) | 0x01u);
        raw[OFFSET_m_bFakePlayer] = 0;
    }

    // sets m_bFakePlayer = 1
    inline void SetFakePlayer(void *client)
    {
        auto *raw = reinterpret_cast<unsigned char *>(client);
        auto &connectionFlags = raw[OFFSET_m_nConnectionTypeFlags];
        connectionFlags = static_cast<unsigned char>((connectionFlags & ~0x01u) | 0x08u);
        raw[OFFSET_m_bFakePlayer] = 1;
    }

    // Writes both SteamID fields used by the current engine
    inline void WriteSteamId(void *client, uint64_t steamId)
    {
        auto *raw = reinterpret_cast<unsigned char *>(client);
        std::memcpy(raw + OFFSET_m_SteamID, &steamId, sizeof(steamId));
        std::memcpy(raw + OFFSET_m_SteamIDMirror, &steamId, sizeof(steamId));
    }

    inline bool SteamIdMatches(const void *client, uint64_t steamId)
    {
        if (!client)
            return false;
        uint64_t primary = 0;
        uint64_t mirror = 0;
        auto *raw = reinterpret_cast<const unsigned char *>(client);
        std::memcpy(&primary, raw + OFFSET_m_SteamID, sizeof(primary));
        std::memcpy(&mirror, raw + OFFSET_m_SteamIDMirror, sizeof(mirror));
        return primary == steamId && mirror == steamId;
    }

    // Checks whether the client has the fake-player flag
    inline bool IsFakePlayerSet(const void *client)
    {
        auto *raw = reinterpret_cast<const unsigned char *>(client);
        return raw[OFFSET_m_bFakePlayer] == 0x01;
    }

    // Checks whether the client is SourceTV
    inline bool IsHltv(const void *client)
    {
        if (!client)
            return false;
        auto *raw = reinterpret_cast<const unsigned char *>(client);
        return raw[OFFSET_m_bIsHLTV] != 0;
    }

} // namespace cs2bh::ssc
