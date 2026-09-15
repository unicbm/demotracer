#pragma once

#include <cstddef>
#include <cstdint>
#include <nlohmann/json.hpp>

class INetworkStringTableContainer;

namespace BotController::Avatars
{
    // The publisher works on servers; the native HUD bridge requires a local
    // Windows client in the same process. No remote client commands are sent.
    void Init(INetworkStringTableContainer *server, INetworkStringTableContainer *client,
              const nlohmann::json &gamedata);
    bool Shutdown();
    int Publish(uint64_t steamId, const unsigned char *png, int length);
    int Clear(uint64_t steamId);
    void ClearAll();
    const char *Status();
}
