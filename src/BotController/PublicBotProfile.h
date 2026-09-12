#pragma once
#include <cstdint>
#include <nlohmann/json.hpp>

namespace BotController::PublicBotProfile
{
#pragma pack(push, 4)
    struct Data
    {
        float aggression, skill, teamwork, reactionTime, attackDelay;
        float lookAccelAtk, lookStiffAtk, lookDampAtk;
        int32_t cost, difficulty, weaponPrefCount;
        uint16_t weaponPref[16];
    };
#pragma pack(pop)
    static_assert(sizeof(Data) == 76);
    void Configure(const nlohmann::json &gamedata);
    bool Read(int slot, Data &out);
}
