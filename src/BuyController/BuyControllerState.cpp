// Per-slot bot buy plan table.

#include "BuyControllerState.h"
#include "BuyController.h"

#include <array>
#include <mutex>

namespace BotController
{
    namespace BuyControllerState
    {
        struct Entry
        {
            bool present = false;
            BuyPlan plan;
        };

        static std::array<Entry, kMaxSlots> g_plans{};
        static std::mutex g_mu;

        bool HasPlan(int slot)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return false;
            std::lock_guard<std::mutex> lk(g_mu);
            return g_plans[slot].present;
        }

        void Set(int slot, const std::vector<std::string> &items, bool skip)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return;
            {
                std::lock_guard<std::mutex> lk(g_mu);
                g_plans[slot].present = true;
                g_plans[slot].plan.skip = skip;
                g_plans[slot].plan.items = items;
            }
            // A plan can be replaced while BuyState is already inside its
            // initial-delay phase. Treat the new plan as unobserved instead
            // of inheriting the previous round's edge latch.
            BuyControllerHooks::ResetInitialDelayLatch(slot);
        }

        bool Copy(int slot, BuyPlan &out)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return false;
            std::lock_guard<std::mutex> lk(g_mu);
            if (!g_plans[slot].present)
                return false;
            out = g_plans[slot].plan;
            return true;
        }

        void Clear(int slot)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return;
            {
                std::lock_guard<std::mutex> lk(g_mu);
                g_plans[slot] = Entry{};
            }
            BuyControllerHooks::ResetInitialDelayLatch(slot);
        }

        void ClearAll()
        {
            {
                std::lock_guard<std::mutex> lk(g_mu);
                for (auto &e : g_plans)
                    e = Entry{};
            }
            BuyControllerHooks::ResetAllInitialDelayLatches();
        }

        int ItemCount(int slot)
        {
            if (slot < 0 || slot >= kMaxSlots)
                return -1;
            std::lock_guard<std::mutex> lk(g_mu);
            if (!g_plans[slot].present)
                return -1;
            return static_cast<int>(g_plans[slot].plan.items.size());
        }

        int CountPlans()
        {
            std::lock_guard<std::mutex> lk(g_mu);
            int n = 0;
            for (auto &e : g_plans)
                if (e.present)
                    ++n;
            return n;
        }
    }
}
