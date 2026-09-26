/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

#include "bot_info.h"
#include "fake_client_manager.h"
#include "presentation_state.h"
#include <thread>

int main()
{
    // Exercise the production base resolver and manager without an engine or
    // server-local bot_info.json. A missing roster must preserve the bot SID.
    cs2bh::BotInfoStore emptyRoster;
    bool queriedZero = false;
    const auto engineSid = emptyRoster.ResolveBaseSteamId(0, [&](uint64_t)
    {
        queriedZero = true;
        return true;
    });
    if (engineSid != 0 || queriedZero) return 1;

    auto &publisher = cs2bh::Publisher();
    publisher.Init();
    cs2bh::FakeClientManager manager;
    if (manager.HasManagedSlots()) return 2;
    if (!manager.AdoptSlot(0, "engine bot", engineSid, "", 0)) return 3;
    cs2bh::PresentationSlot zero{};
    if (!publisher.ReadSlot(0, zero) || !zero.Managed || zero.BaseSteamId || zero.SteamId) return 4;
    if (!publisher.CanPublishSteamId(0, zero.Session, zero.Incarnation, 0)) return 5;
    // A lease may publish an exact nonzero identity, then release to zero.
    publisher.UpdateSyntheticSid(0, 700);
    if (!publisher.CanPublishSteamId(0, zero.Session, zero.Incarnation, 0)) return 6;
    publisher.UpdateSyntheticSid(0, 0);
    publisher.ReadSlot(0, zero);
    if (zero.BaseSteamId || zero.SteamId) return 7;

    // Two bots request the same configured identity. Resolve the collision
    // before adoption so controller reconciliation and release use the same base.
    constexpr uint64_t requested = 76561197960265729ULL;
    const auto first = emptyRoster.ResolveBaseSteamId(requested, [](uint64_t) { return false; });
    const auto second = emptyRoster.ResolveBaseSteamId(requested, [first](uint64_t sid) { return sid == first; });
    if (first != requested || second == first || !second) return 8;
    if (!manager.AdoptSlot(1, "first", first, "", 0) ||
        !manager.AdoptSlot(2, "second", second, "", 0)) return 9;
    cs2bh::PresentationSlot adopted{};
    if (!publisher.ReadSlot(2, adopted) || adopted.BaseSteamId != second || adopted.SteamId != second) return 10;
    if (publisher.CanPublishSteamId(2, adopted.Session, adopted.Incarnation, 0)) return 11;
    publisher.UpdateSyntheticSid(2, 800);
    publisher.ReadSlot(2, adopted);
    if (adopted.BaseSteamId != second || adopted.SteamId != 800) return 12;
    publisher.UpdateSyntheticSid(2, adopted.BaseSteamId);
    publisher.ReadSlot(2, adopted);
    if (adopted.SteamId != second) return 13;

    // Packing threads keep their synchronized managed-set view, without
    // copying identity into a second ledger.
    bool workerManaged = false;
    std::thread worker([&] { workerManaged = manager.IsManaged(2); });
    worker.join();
    if (!workerManaged || !manager.HasManagedSlots()) return 14;
    manager.ReleaseSlot(0);
    if (publisher.CanPublishSteamId(0, zero.Session, zero.Incarnation, 0)) return 15;
    manager.ReleaseAll();
    if (manager.HasManagedSlots()) return 16;
    if (emptyRoster.ResolveBaseSteamId(UINT64_MAX, [](uint64_t) { return true; }) != 0) return 17;
    publisher.Shutdown();
}
