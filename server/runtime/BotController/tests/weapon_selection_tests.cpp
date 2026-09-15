#include "WeaponSelection.h"

int main()
{
    int weapon = 0, calls = 0;
    auto rejected = [&](void *) { ++calls; return 0; };
    using namespace BotController::WeaponSelection;
    if (Apply(nullptr, &weapon, rejected) || calls != 1) return 1;
    if (!Apply(&weapon, &weapon, rejected) || calls != 1) return 2;
    if (Apply(nullptr, nullptr, rejected) || calls != 1) return 3;
    if (!Apply(nullptr, &weapon, [](void *) { return 1; })) return 4;
    if (ValidLockTarget(-1) || ValidLockTarget(0) || ValidLockTarget(6) || ValidLockTarget(9001)) return 5;
    for (int i = 1; i <= 5; ++i) if (!ValidLockTarget(i)) return 6;
}
