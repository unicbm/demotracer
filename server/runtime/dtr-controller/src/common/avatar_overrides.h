#pragma once

#include <cstdint>

class INetworkStringTableContainer;

namespace BotController::Avatars
{
    // Only server string tables are accessed. The game owns client caching.
    void Init(INetworkStringTableContainer *server);
    void OnLevelShutdown();
    void Shutdown();
    int Publish(uint64_t steamId, const unsigned char *png, int length);
    int Clear(uint64_t steamId);
    void ClearAll();
    const char *Status();
}
