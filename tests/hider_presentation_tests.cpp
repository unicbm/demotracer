#include "presentation_state.h"
#include <thread>

namespace
{
    unsigned rosterChanges = 0, pingChanges = 0;
    int lastChangedSlot = -1;
    void OnChanged(uint32_t reason, int slot)
    {
        lastChangedSlot = slot;
        if (reason == cs2bh::kPresentationRosterChanged) ++rosterChanges;
        if (reason == cs2bh::kPresentationPingChanged) ++pingChanges;
    }
}

int main()
{
    cs2bh::SlotPublisher a, b;
    a.Init(); b.Init();
    if (a.Listen(a.Session() + 1, OnChanged) || !a.Listen(a.Session(), OnChanged)) return 8;
    a.PublishAdopt(3, 111, "original", "", 0);
    cs2bh::PresentationSlot old{}, current{};
    if (!a.ReadSlot(3, old) || !a.Matches(3, old.Session, old.Incarnation)) return 1;
    // Another publisher instance has independent state (no global mapping).
    if (!b.ReadSlot(3, current) || current.Managed) return 2;
    a.PublishRelease(3); a.PublishAdopt(3, 222, "replacement", "", 0);
    if (a.Matches(3, old.Session, old.Incarnation)) return 3;
    a.ReadSlot(3, current);
    if (current.BaseSteamId != 222 || current.Incarnation == old.Incarnation) return 4;
    if (rosterChanges != 3) return 9;
    a.UpdatePing(3, 42); a.UpdatePing(3, 42);
    a.UpdateSyntheticSid(3, 333); a.UpdatePersonaName(3, "published");
    if (pingChanges != 1 || rosterChanges != 3 || lastChangedSlot != 3) return 10;
    bool foreignAccepted = true;
    std::thread worker([&] { foreignAccepted = a.Matches(3, current.Session, current.Incarnation); });
    worker.join(); if (foreignAccepted) return 5;
    a.Shutdown(); if (a.Session() || a.ReadSlot(3, current)) return 6;
    a.Shutdown(); if (rosterChanges != 4 || lastChangedSlot != -1) return 11;
    a.Init(); if (a.Session() == old.Session || a.Matches(3, old.Session, old.Incarnation)) return 7;
    if (a.Listen(old.Session, OnChanged)) return 12;
    a.PublishAdopt(3, 444, "new session", "", 0);
    if (rosterChanges != 4) return 13; // No listener leaks across native lifetimes.
    if (!a.Listen(a.Session(), OnChanged) || !a.Listen(a.Session(), nullptr)) return 14;
    a.PublishRelease(3);
    if (rosterChanges != 4) return 15;
}
