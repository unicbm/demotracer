#include "BuyControllerPolicy.h"

#include <cassert>

using BotController::BuyControllerHooks::BuyUpdateAction;
using BotController::BuyControllerHooks::DecideBuyUpdate;

int main()
{
    assert(DecideBuyUpdate(true, 0, 0) == BuyUpdateAction::ForceSkip);
    assert(DecideBuyUpdate(true, 1, 0) == BuyUpdateAction::ForceSkip);
    assert(DecideBuyUpdate(true, 1, 1) == BuyUpdateAction::ForceSkip);
    assert(DecideBuyUpdate(false, 1, 0) == BuyUpdateAction::ApplyPlan);
    assert(DecideBuyUpdate(false, 1, 1) == BuyUpdateAction::None);
    assert(DecideBuyUpdate(false, 0, 0) == BuyUpdateAction::None);
    return 0;
}
