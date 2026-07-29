// Per-slot bot buy plan table.

#pragma once

#include <string>
#include <vector>

namespace BotController
{
    struct BuyPlan
    {
        bool skip = false;
        std::vector<std::string> items;
    };

    namespace BuyControllerState
    {
        constexpr int kMaxSlots = 64;

        bool HasPlan(int slot);
        void Set(int slot, const std::vector<std::string> &items, bool skip);
        bool Copy(int slot, BuyPlan &out);
        void Clear(int slot);
        void ClearAll();
        int ItemCount(int slot);
        int CountPlans();
    }
}
