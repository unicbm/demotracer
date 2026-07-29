// Detour for BuyState::OnUpdate to force a bot's per-round buy plan.

#pragma once

#include "sig_scan.h"

#include <nlohmann/json.hpp>

namespace BotController
{
    namespace BuyControllerHooks
    {
        bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                     char *errorOut, size_t errorOutLen);
        void Remove();
        void ResetInitialDelayLatch(int slot);
        void ResetAllInitialDelayLatches();

        const char *Status();
        void *OnUpdateAddress();
    }
}
