#include "BuyControllerPolicy.h"

#include <cstdio>
#include <cstdlib>

using BotController::BuyControllerHooks::BuyUpdateAction;
using BotController::BuyControllerHooks::DecideBuyUpdate;

static void Check(bool condition)
{
    if (!condition)
    {
        std::fputs("Buy policy test failed\n", stderr);
        std::exit(1);
    }
}

int main()
{
    Check(DecideBuyUpdate(true, 0, 0) == BuyUpdateAction::ForceSkip);
    Check(DecideBuyUpdate(true, 1, 0) == BuyUpdateAction::ForceSkip);
    Check(DecideBuyUpdate(true, 1, 1) == BuyUpdateAction::ForceSkip);
    Check(DecideBuyUpdate(false, 1, 0) == BuyUpdateAction::ApplyPlan);
    Check(DecideBuyUpdate(false, 1, 1) == BuyUpdateAction::None);
    Check(DecideBuyUpdate(false, 0, 0) == BuyUpdateAction::None);
    return 0;
}
