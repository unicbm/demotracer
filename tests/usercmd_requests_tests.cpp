#include "UsercmdRequests.h"
#include <cstdlib>
#include <iostream>
#include <limits>

using BotController::UsercmdRequests;
using Kind = UsercmdRequests::Kind;
static void Check(bool value, const char *message)
{
    if (!value) { std::cerr << message << '\n'; std::exit(1); }
}
int main()
{
    UsercmdRequests requests;
    constexpr uint64_t drop = 1ULL << 35, use = 1ULL << 5;
    const auto first = requests.Add(1, Kind::Injection, drop, 0, 10);
    auto frame = requests.Advance(1, 500, {});
    Check(frame.buttons.state1 == drop && frame.buttons.state2 == drop, "one-shot press starts at command consumption");
    frame = requests.Advance(1, 501, {});
    Check(frame.buttons.state1 == 0 && frame.buttons.state2 == drop, "one-shot emits release on following command");
    Check(!requests.Pending(1), "one-shot releases all transient state");

    const auto a = requests.Add(1, Kind::Injection, use, 100, 510);
    const auto b = requests.Add(1, Kind::Injection, use, 200, 510);
    requests.Advance(1, 600, {});
    Check(requests.Cancel(1, Kind::Injection, a), "cancel first overlapping token");
    frame = requests.Advance(1, 610, {});
    Check(frame.buttons.state1 == use && frame.buttons.state2 == 0, "other token retains held input without another press");
    frame = requests.Advance(1, 800, {});
    Check(frame.buttons.state1 == 0 && frame.buttons.state2 == use, "duration measured from first consumed command");
    Check(!requests.Cancel(2, Kind::Injection, b), "token cannot cancel another slot");

    auto movement = requests.Add(2, Kind::Movement, 0, 0, 0, 1, 0);
    auto newer = requests.Add(2, Kind::Movement, 0, 0, 0, -1, 1);
    frame = requests.Advance(2, 1, {});
    Check(frame.movement && frame.forward == -1 && frame.left == 1, "newest movement wins");
    requests.Cancel(2, Kind::Movement, newer);
    frame = requests.Advance(2, 2, {});
    Check(frame.forward == 1 && frame.left == 0, "cancelling movement restores remaining request");
    Check(!requests.Cancel(2, Kind::Suppression, movement), "wrong token kind does not cancel movement");
    Check(requests.UpdateMovement(2, movement, 10, -10), "update existing movement");
    frame = requests.Advance(2, 3, {});
    Check(frame.forward == 1 && frame.left == -1, "movement axes are bounded");

    auto suppression = requests.Add(3, Kind::Suppression, use, 0, 0);
    requests.Add(3, Kind::Injection, use | drop, 100, 0);
    frame = requests.Advance(3, 0, {use, use, use});
    auto decoded = BotController::ButtonState::Decode(frame.buttons.state1, frame.buttons.state2, frame.buttons.state3);
    Check(decoded.held == drop && decoded.pressed == drop && (decoded.released & use), "suppression wins and button planes preserve press/release semantics");
    requests.Cancel(3, Kind::Suppression, suppression);
    frame = requests.Advance(3, 1, {});
    Check(frame.buttons.state1 == (drop | use), "cancellation restores the independent injection");
    requests.Clear(3);
    Check(!requests.Pending(3) && !requests.Cancel(3, Kind::Suppression, suppression), "replay ownership drops old tokens");
    auto afterReset = requests.Add(3, Kind::Suppression, use, 0, 2);
    Check(afterReset > first && afterReset != suppression, "slot reuse never recycles token IDs");
    requests.ClearAll();
    Check(!requests.Pending(1) && !requests.Pending(2) && !requests.Pending(3), "map and unload cleanup");
    Check(requests.Add(-1, Kind::Injection, use, 0, 0) < 0 &&
          requests.Add(64, Kind::Movement, 0, 0, 0) < 0 &&
          requests.Add(1, Kind::Movement, 0, 0, 0, std::numeric_limits<float>::quiet_NaN()) < 0,
          "invalid requests rejected");
    std::cout << "Usercmd request tests passed\n";
}
