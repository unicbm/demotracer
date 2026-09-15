#include "avatar_publication.h"
#include <cstdio>
#include <cstdlib>

using namespace BotController::Avatars;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "line %d: %s\n", __LINE__, #x); std::exit(1); } } while (0)

int main()
{
    Publications p;
    const Bytes original{1}, a{2}, b{3};
    CHECK(!p.Observe(7, a, 1)); // No evidence, including another table's PNG.
    p.Prepare(7, a, original);
    CHECK(!p.Observe(7, original, 1)); // Publication is not client arrival.
    CHECK(p.Observe(7, a, 1));
    CHECK(!p.Observe(7, a, 1));
    p.Prepare(7, a, a);
    CHECK(!p.Observe(7, a, 1)); // Identical publication is idempotent.
    p.Prepare(7, b, a);
    CHECK(!p.Observe(7, a, 1)); // Old data arriving after replacement.
    CHECK(p.Observe(7, b, 1));
    CHECK(p.entries.at(7).restore == original); // Do not restore an intermediate DTR image.
    CHECK(p.Observe(7, b, 2)); // Client table was recreated.
    CHECK(!p.Observe(7, b, 2));
    p.Retire(7, original);
    CHECK(p.entries.at(7).retired == b);
    CHECK(!p.Observe(7, b, 2));
    CHECK(p.Observe(7, original, 2));
    CHECK(!p.Observe(7, original, 2));
    p.RetryObservation(7);
    CHECK(p.Observe(7, original, 2)); // A rejected dispatch has not completed.
    p.Prepare(7, a, b); // Later owner establishes a new restoration value.
    CHECK(p.entries.at(7).restore == b);
    CHECK(p.Observe(7, a, 2));
    p.Retire(7, {});
    CHECK(p.Observe(7, {}, 2)); // Clearing data also invalidates the HUD.
    CHECK(!p.Observe(7, {}, 2));
    std::puts("avatar publication lifecycle passed");
}
