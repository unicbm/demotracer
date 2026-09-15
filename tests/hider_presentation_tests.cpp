#include "presentation_state.h"
#include <thread>

int main()
{
    cs2bh::SlotPublisher a, b;
    a.Init(); b.Init();
    a.PublishAdopt(3, 111, "original", "", 0);
    cs2bh::PresentationSlot old{}, current{};
    if (!a.ReadSlot(3, old) || !a.Matches(3, old.Session, old.Incarnation)) return 1;
    // Another publisher instance has independent state (no global mapping).
    if (!b.ReadSlot(3, current) || current.Managed) return 2;
    a.PublishRelease(3); a.PublishAdopt(3, 222, "replacement", "", 0);
    if (a.Matches(3, old.Session, old.Incarnation)) return 3;
    a.ReadSlot(3, current);
    if (current.BaseSteamId != 222 || current.Incarnation == old.Incarnation) return 4;
    bool foreignAccepted = true;
    std::thread worker([&] { foreignAccepted = a.Matches(3, current.Session, current.Incarnation); });
    worker.join(); if (foreignAccepted) return 5;
    a.Shutdown(); if (a.Session() || a.ReadSlot(3, current)) return 6;
    a.Init(); if (a.Session() == old.Session || a.Matches(3, old.Session, old.Incarnation)) return 7;
}
