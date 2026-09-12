// Public BotProfile layout from XBribo/CS2-Bot-Controller 64f676c (AGPL-3.0-only).
#include "PublicBotProfile.h"
#include "BotController.h"
#include "ccsbot_slot.h"
#include "sig_scan.h"
#include <algorithm>
#include <array>

namespace BotController::PublicBotProfile
{
    static constexpr const char *Fields[] = {
        "CCSBot::Profile", "BotProfile::Aggression", "BotProfile::Skill", "BotProfile::Teamwork",
        "BotProfile::ReactionTime", "BotProfile::AttackDelay", "BotProfile::LookAngleMaxAccelAttacking",
        "BotProfile::LookAngleStiffnessAttacking", "BotProfile::LookAngleDampingAttacking",
        "BotProfile::Cost", "BotProfile::Difficulty", "BotProfile::WeaponPrefCount", "BotProfile::WeaponPref"};
    static std::array<int, 13> offsets;
    static bool ready = false;
    void Configure(const nlohmann::json &gd)
    {
        ready = true;
        for (size_t i = 0; i < offsets.size(); ++i)
        {
            offsets[i] = Sig::FindPlatformOffset(gd, Fields[i], -1);
            ready &= offsets[i] >= 0;
        }
    }
    bool Read(int slot, Data &out)
    {
        out = {};
        void *bot = BotControllerHooks::BotForSlot(slot), *profile = nullptr;
        if (!ready || !bot || !SafeRead(bot, offsets[0], profile) || !profile) return false;
        if (!SafeRead(profile, offsets[1], out.aggression) || !SafeRead(profile, offsets[2], out.skill) ||
            !SafeRead(profile, offsets[3], out.teamwork) || !SafeRead(profile, offsets[4], out.reactionTime) ||
            !SafeRead(profile, offsets[5], out.attackDelay) || !SafeRead(profile, offsets[6], out.lookAccelAtk) ||
            !SafeRead(profile, offsets[7], out.lookStiffAtk) || !SafeRead(profile, offsets[8], out.lookDampAtk) ||
            !SafeRead(profile, offsets[9], out.cost) || !SafeRead(profile, offsets[11], out.weaponPrefCount)) return false;
        uint8_t difficulty = 0;
        if (!SafeRead(profile, offsets[10], difficulty)) return false;
        out.difficulty = difficulty;
        out.weaponPrefCount = std::clamp(out.weaponPrefCount, 0, 16);
        for (int i = 0; i < out.weaponPrefCount; ++i)
            if (!SafeRead(profile, offsets[12] + i * 2, out.weaponPref[i])) return false;
        return true;
    }
}
