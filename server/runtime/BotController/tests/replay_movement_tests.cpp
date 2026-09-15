// Exercise the production recorder with fake engine storage and engine calls.
// No CS2 process or game-module addresses are needed.
#include "MotionRecorder.h"
#include "InputInjector.h"
#include "ReplayPawnEquipment.h"
#include "WeaponLocker.h"
#include "ccsbot_slot.h"
#include "version_targets.h"

#include <array>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace mr = BotController::MotionRecorder;
namespace tg = BotController::targets;
using BotController::MovementSnapshot;
using BotController::ReplayTick;

namespace
{
    constexpr int slot = 3;
    alignas(16) std::array<std::byte, 0x3000> pawn{};
    alignas(16) std::array<std::byte, 0x1000> services{};
    alignas(16) std::array<std::byte, 0x400> node{};
    alignas(16) std::array<std::byte, 0x1000> aim{};
    int initializations = 0;
    int inputReleases = 0;
    int equipmentReleases = 0;
    bool allowInitialization = true;
    bool humanOwnsPawn = false;
    bool weaponsAvailable = false, weaponActive = false;
    int publishes = 0, deploys = 0;
    alignas(16) std::array<std::byte, 0x1000> weapon{}, weaponServices{};
    alignas(16) std::array<std::byte, 0x100> weaponIdentity{};

    void Check(bool ok, const char *message)
    {
        if (!ok)
        {
            std::fprintf(stderr, "%s\n", message);
            std::exit(1);
        }
    }

    template <typename T, size_t N>
    void Put(std::array<std::byte, N> &storage, int offset, T value)
    {
        Check(offset >= 0 && offset + sizeof(T) <= N, "test offset outside storage");
        std::memcpy(storage.data() + offset, &value, sizeof(value));
    }

    template <typename T, size_t N>
    T Get(const std::array<std::byte, N> &storage, int offset)
    {
        T value{};
        std::memcpy(&value, storage.data() + offset, sizeof(value));
        return value;
    }

    std::array<ReplayTick, 3> Ticks()
    {
        std::array<ReplayTick, 3> ticks{};
        for (int i = 0; i < 3; ++i)
        {
            auto &pre = ticks[i].pre;
            pre.originX = 100.0f + i * 10.0f;
            pre.originZ = 20.0f;
            pre.velX = 200.0f;
            pre.velZ = -650.0f;
            pre.moveType = pre.actualMoveType = 2;
            pre.yaw = 30.0f + i;
            pre.buttons = 4;
            pre.duckAmount = 1.0f;
            pre.duckSpeed = 8.0f;
            pre.ducked = 1;
            pre.desiresDuck = 1;
            pre.entityFlags = tg::kFL_Ducking;
            ticks[i].post = pre;
            // Deliberately unlike the simulated output: catches post rewrites.
            ticks[i].post.originX = 900.0f + i;
            ticks[i].post.velX = 999.0f;
            ticks[i].post.pitch = 45.0f;
            ticks[i].weaponDefIndex = -1;
        }
        return ticks;
    }

    void Reset()
    {
        mr::ReleaseReplayBuffer(slot);
        pawn.fill(std::byte{});
        services.fill(std::byte{});
        node.fill(std::byte{});
        aim.fill(std::byte{});
        Put(pawn, 0x2000, static_cast<void *>(aim.data()));
        Put(pawn, tg::kEnt_GameSceneNode, static_cast<void *>(node.data()));
        Put(pawn, tg::kPawn_Controller, uint32_t{slot + 1});
        Put(pawn, tg::kEnt_MoveType, uint8_t{2});
        Put(pawn, tg::kEnt_ActualMoveType, uint8_t{2});
        initializations = inputReleases = equipmentReleases = 0;
        allowInitialization = true;
        humanOwnsPawn = false;
        weaponsAvailable = weaponActive = false;
        publishes = deploys = 0;
        weapon.fill(std::byte{}); weaponServices.fill(std::byte{}); weaponIdentity.fill(std::byte{});
        Put(weapon, tg::kEnt_Identity, static_cast<void *>(weaponIdentity.data()));
        Put(weaponIdentity, tg::kEntIdentity_EHandle, uint32_t{55});
        const auto ticks = Ticks();
        Check(mr::LoadReplay(slot, ticks.data(), static_cast<int>(ticks.size()), nullptr, 0),
              "load valid replay");
    }

    void Prepare()
    {
        mr::ReplayCommandFrame frame{};
        Check(mr::ReplayCommandFrameForSimulation(slot, frame), "read command frame");
        Check(mr::OnReplayCommandPre(slot, services.data(), *frame.tick, frame.commandView),
              "prepare command");
    }

    void SimulatedOutput()
    {
        Put(node, tg::kNode_AbsOrigin, std::array<float, 3>{107.0f, 2.0f, 16.0f});
        Put(pawn, tg::kEnt_AbsVelocity, std::array<float, 3>{190.0f, 4.0f, -662.0f});
        Put(pawn, tg::kEnt_Flags, uint32_t{0x100});
        Put(services, tg::kServices_DuckAmount, 0.37f);
        Put(services, tg::kServices_DuckSpeed, 4.5f);
        Put(services, tg::kServices_Ducked, uint8_t{0});
        Put(services, tg::kServices_Ducking, uint8_t{1});
    }

    void CheckOutput()
    {
        Check(Get<float>(node, tg::kNode_AbsOrigin) == 107.0f, "engine origin was overwritten");
        Check(Get<float>(node, tg::kNode_AbsOrigin + 8) == 16.0f, "engine origin was nudged");
        Check(Get<float>(pawn, tg::kEnt_AbsVelocity) == 190.0f, "engine velocity was overwritten");
        Check(Get<float>(pawn, tg::kEnt_AbsVelocity + 8) == -662.0f, "fall velocity was clamped");
        Check(Get<uint32_t>(pawn, tg::kEnt_Flags) == 0x100, "engine ground/duck flags were overwritten");
        Check(Get<float>(services, tg::kServices_DuckAmount) == 0.37f, "engine duck amount was overwritten");
        Check(Get<float>(services, tg::kServices_DuckSpeed) == 4.5f, "engine duck speed was overwritten");
        Check(Get<uint8_t>(services, tg::kServices_Ducked) == 0, "engine ducked was overwritten");
        Check(Get<uint8_t>(services, tg::kServices_Ducking) == 1, "engine ducking was overwritten");
    }

    void ContinuousInputAndEngineOutput()
    {
        Reset();
        mr::SetReplayPerfEnabled(true);
        mr::ResetReplayPerfCounters();
        Check(mr::StartReplayAt(slot, false, 0), "start replay");
        // Lazy hook installation may see a FinishMove before the first
        // hooked command. It must not consume an uninitialized replay frame.
        alignas(16) std::array<std::byte, 0x200> unpreparedMove{};
        mr::OnReplaySetupMove(slot, unpreparedMove.data());
        mr::OnReplayFinalView(slot, services.data());
        mr::OnReplayCommit(slot, services.data());
        Check(mr::ReplayCursor(slot) == 0 && initializations == 0 &&
              Get<float>(unpreparedMove, tg::kMove_AbsOrigin) == 0.0f,
              "unprepared engine command consumed replay input");
        Prepare();
        Check(initializations == 1, "start did not initialize exactly once");
        Check(Get<float>(pawn, tg::kEnt_AbsVelocity + 8) == -650.0f, "initial velocity altered");
        Check(Get<float>(services, tg::kServices_DuckAmount) == 1.0f, "start duck state not seeded");
        alignas(16) std::array<std::byte, 0x200> move{};
        Put(move, tg::kMove_ForwardMove, 123.0f);
        SimulatedOutput();
        const auto pawnBeforeSetup = pawn;
        const auto servicesBeforeSetup = services;
        const auto nodeBeforeSetup = node;
        mr::OnReplaySetupMove(slot, move.data());
        Check(Get<float>(move, tg::kMove_AbsOrigin) == 100.0f, "demo pre input missing");
        Check(Get<float>(move, tg::kMove_Velocity + 8) == -650.0f, "input velocity altered");
        Check(Get<float>(move, tg::kMove_ForwardMove) == 123.0f, "setup replaced engine command axes");
        Check(pawn == pawnBeforeSetup && services == servicesBeforeSetup && node == nodeBeforeSetup,
              "SetupMove wrote outside its input buffer");
        mr::OnReplayFinalView(slot, services.data());
        mr::OnReplayCommit(slot, services.data());
        CheckOutput();
        Check(mr::ReplayCursor(slot) == 1, "cursor did not advance");
        Prepare();
        Check(initializations == 1, "continuous replay reinitialized movement");
        CheckOutput();
        mr::OnReplaySetupMove(slot, move.data());
        Check(Get<float>(move, tg::kMove_AbsOrigin) == 110.0f, "next demo pre input missing");
        mr::OnReplayFinalView(slot, services.data());
        mr::OnReplayCommit(slot, services.data());
        CheckOutput();
        const auto perf = mr::GetReplayPerfCounters();
        Check(perf.movementInputs == 2 && perf.movementInitializations == 1,
              "movement counters do not distinguish input from boundary initialization");
        mr::SetReplayPerfEnabled(false);
    }

    void StartSeekLoopAndHeldResume()
    {
        Reset();
        Check(mr::StartReplayUntil(slot, false, 0, 1), "start pre-roll");
        Prepare();
        mr::OnReplayCommit(slot, services.data());
        Prepare();
        Check(initializations == 1 && mr::ReplayCursor(slot) == 0, "hold restarted movement");
        SimulatedOutput();
        Check(mr::StartReplayAt(slot, false, 1), "resume held replay");
        Prepare();
        Check(initializations == 1, "held resume broke continuous movement");
        CheckOutput();
        Check(mr::StartReplayAt(slot, false, 2), "seek");
        Prepare();
        Check(initializations == 2, "seek did not initialize");
        Check(Get<float>(node, tg::kNode_AbsOrigin) == 120.0f, "seek initialized wrong snapshot");
        Check(mr::StartReplayAt(slot, true, 2), "start looping replay");
        Prepare();
        mr::OnReplayCommit(slot, services.data());
        Check(mr::ReplayCursor(slot) == 2, "loop exposed an out-of-range command");
        Prepare();
        Check(initializations == 4, "loop did not initialize next playback pass");
    }

    void FinishAndHumanTakeover()
    {
        Reset();
        Check(mr::StartReplayAt(slot, false, 2), "start final frame");
        Prepare();
        SimulatedOutput();
        Put(services, tg::kServices_Buttons, uint64_t{5});
        mr::OnReplayFinalView(slot, services.data());
        mr::OnReplayCommit(slot, services.data());
        Check(!mr::IsReplaying(slot), "natural finish did not stop");
        CheckOutput();
        Check(Get<uint64_t>(services, tg::kServices_Buttons) == 0, "injected buttons leaked at finish");
        Check(equipmentReleases > 0 && inputReleases > 0, "ownership not released");
        const auto ticks = Ticks();
        Check(!mr::OnReplayCommandPre(slot, services.data(), ticks[2], ticks[2].pre),
              "stopped replay still prepares commands");
        alignas(16) std::array<std::byte, 0x200> move{};
        mr::OnReplaySetupMove(slot, move.data());
        Check(Get<float>(move, tg::kMove_AbsOrigin) == 0.0f, "stopped replay still supplies movement");

        Check(mr::StartReplayAt(slot, false, 0), "restart");
        Prepare();
        humanOwnsPawn = true;
        const auto pawnBefore = pawn;
        const auto servicesBefore = services;
        const auto nodeBefore = node;
        Check(mr::StopReplay(slot), "stop after takeover");
        Check(pawn == pawnBefore && services == servicesBefore && node == nodeBefore,
              "stop rewrote a human-controlled pawn");
        humanOwnsPawn = false;
    }

    void StopPreservesNativeMovementState()
    {
        for (uint8_t moveType : {uint8_t{2}, uint8_t{9}})
        {
            Reset();
            Check(mr::StartReplay(slot, false), "start stop-state test");
            Prepare();
            SimulatedOutput();
            Put(pawn, tg::kEnt_MoveType, moveType);
            Put(pawn, tg::kEnt_ActualMoveType, moveType);
            Put(pawn, tg::kEnt_Flags, uint32_t{tg::kFL_OnGround | tg::kFL_Ducking});
            Put(services, tg::kServices_LadderNormal, std::array<float, 3>{1, 0, 0});
            const auto beforePawn = pawn;
            const auto beforeNode = node;
            auto expectedServices = services;
            Put(expectedServices, tg::kServices_Buttons, uint64_t{0});
            Put(expectedServices, tg::kServices_Buttons1, uint64_t{0});
            Put(expectedServices, tg::kServices_Buttons2, uint64_t{0});
            Put(expectedServices, tg::kServices_DesiresDuck, uint8_t{0});
            Check(mr::StopReplay(slot), "stop-state stop failed");
            Check(pawn == beforePawn && node == beforeNode && services == expectedServices,
                  "stop altered native velocity, ground, duck or retained ladder state");
        }
    }

    void SourceStateRestoresAtBoundariesOnly()
    {
        namespace source = BotController::ReplaySourceState;
        Reset();
        Check(source::InitializeOffsets(), "source offset fixture");
        const source::Change changes[] = {
            {0, source::PlayerTick, 4000, 1},
            {0, source::DuckRoot, source::Bits(7.967285f), 1},
            {0, source::Stamina, source::Bits(25.0f), 1},
            {1, source::Stamina, source::Bits(10.0f), 1},
            {2, source::Stamina, 0, 0},
        };
        Check(mr::LoadReplaySourceState(slot, changes, 5, 64, 1.0f / 64), "load source changes");
        Check(mr::StartReplay(slot, false), "start source replay");
        Prepare();
        constexpr int staminaOffset = 0x800 + source::Stamina * 4;
        Check(Get<float>(services, staminaOffset) == 25, "start stamina not restored");
        Put(services, staminaOffset, 17.0f);
        mr::OnReplayCommit(slot, services.data()); Prepare();
        Check(Get<float>(services, staminaOffset) == 17, "continuous tick overwrote engine stamina");
        Check(mr::StartReplayAt(slot, false, 2), "seek source replay"); Prepare();
        Check(Get<float>(services, staminaOffset) == 17, "missing state became zero/stale truth");
        Check(!mr::LoadReplaySourceState(slot, changes, 5, 64, 1.0f / 64), "mutated source while replaying");
        source::Timeline timeline;
        Check(timeline.Load(changes, 5, 3), "load timeline");
        Check(timeline.Get(source::Stamina, 1) == source::Bits(10), "seek state wrong");
        source::Change invalid = {3, source::Stamina, source::Bits(10), 1};
        Check(!timeline.Load(&invalid, 1, 3) && timeline.Get(source::Stamina, 1) == source::Bits(10), "invalid load damaged timeline");
        const source::LiveClock live{1000, 15.625f, 1.0f / 64};
        Check(source::Rebase(4008, source::ClockKind::Tick, 4000, 64, live) == 1008, "future attack deadline not rebased");
        Check(source::Rebase(3992, source::ClockKind::Tick, 4000, 64, live) == 992, "historical jump not rebased");
        Check(source::Float(*source::Rebase(source::Bits(62.625f), source::ClockKind::Seconds, 4000, 64, live)) == 15.75f, "seconds clock disagrees with tick clock");
        Check(source::Rebase(UINT32_MAX, source::ClockKind::Tick, 4000, 64, live) == UINT32_MAX &&
              source::Rebase(0, source::ClockKind::Tick, 4000, 64, live) == 0, "sentinel timestamp changed");
    }

    void InitializationFailureDoesNotConsumeBoundary()
    {
        Reset();
        Check(mr::StartReplayAt(slot, false, 0), "start");
        allowInitialization = false;
        const auto before = pawn;
        const auto ticks = Ticks();
        Check(!mr::OnReplayCommandPre(slot, services.data(), ticks[0], ticks[0].pre),
              "failed engine setter accepted");
        Check(pawn == before && initializations == 0, "failed initialization changed pawn");
        allowInitialization = true;
        Prepare();
        Check(initializations == 1, "failed initialization consumed boundary");
    }

    void LadderStartRequiresContact()
    {
        Reset();
        auto ticks = Ticks(); ticks[0].pre.moveType = 9;
        Check(mr::LoadReplay(slot, ticks.data(), 3, nullptr, 0), "load ladder start");
        const auto before = pawn;
        Check(!mr::StartReplay(slot, false) && !mr::StartReplayUntil(slot, false, 0, 2), "accepted ladder without contact");
        Check(pawn == before && initializations == 0, "unsupported start modified pawn");
        ticks[0].pre.ladderNormalX = 1;
        Check(mr::LoadReplay(slot, ticks.data(), 3, nullptr, 0) && mr::StartReplay(slot, false), "rejected recorded ladder plane");
        Prepare();
        Check(Get<float>(services, tg::kServices_LadderNormal) == 1, "lost ladder plane");
    }

    void WeaponSourceRestoresOnceAfterDeploy()
    {
        namespace source = BotController::ReplaySourceState;
        Reset(); weaponsAvailable = true;
        auto ticks = Ticks(); for (auto &t : ticks) t.weaponDefIndex = 7;
        Check(mr::LoadReplay(slot, ticks.data(), 3, nullptr, 0), "load weapon replay");
        const source::Change changes[] = {
            {0, source::PlayerTick, 4000, 1}, {0, source::Clip1, uint32_t(-1), 1},
            {0, source::Clip2, uint32_t(-1), 1}, {0, source::InReload, 1, 1},
            {0, source::NextPrimaryTick, 4008, 1},
            {0, source::NextAttack, source::Bits(62.625f), 1},
            {0, source::ActiveWeaponHandle, 1234, 1},
            {0, source::ReserveAmmoPrimary, 999, 1}, {0, source::ReserveAmmoSecondary, 999, 1},
        };
        constexpr int clipOffset = 0x800 + source::Clip1 * 4;
        constexpr int attackOffset = 0x800 + source::NextPrimaryTick * 4;
        Put(weapon, clipOffset, 30);
        Put(weapon, 0x800 + source::Clip2 * 4, 0);
        Put(weapon, 0x800 + source::InReload * 4, 0);
        Put(weapon, 0x800 + source::ReserveAmmoPrimary * 4, 90);
        Put(weapon, 0x800 + source::ReserveAmmoSecondary * 4, 0);
        Check(mr::LoadReplaySourceState(slot, changes, 9, 64, 1.0f / 64) && mr::StartReplay(slot, false), "start weapon replay");
        Prepare();
        Check(deploys == 1 && publishes == 2, "boundary deploy/publication count");
        Check(Get<int>(weapon, clipOffset) == 30 && Get<int>(weapon, attackOffset) == 1008 &&
              Get<float>(weaponServices, 0x800 + source::NextAttack * 4) == 15.75f, "weapon clock restore changed native ammo");
        Check(Get<int>(weapon, 0x800 + source::Clip2 * 4) == 0 &&
              Get<int>(weapon, 0x800 + source::InReload * 4) == 0 &&
              Get<int>(weapon, 0x800 + source::ReserveAmmoPrimary * 4) == 90 &&
              Get<int>(weapon, 0x800 + source::ReserveAmmoSecondary * 4) == 0, "demo replaced server ammo/reload rules");
        Put(weapon, clipOffset, 6); Put(weapon, attackOffset, 1010);
        mr::OnReplayCommit(slot, services.data()); Prepare();
        Check(Get<int>(weapon, clipOffset) == 6 && Get<int>(weapon, attackOffset) == 1010 && publishes == 2, "continuous replay reset weapon");
        // A normal switch away/back must preserve the native incarnation.
        weaponActive = false; Prepare();
        Check(deploys == 1 && Get<int>(weapon, clipOffset) == 6, "switch reset weapon state");
        // Reused entity index, new serial: this really is a new incarnation.
        Put(weaponIdentity, tg::kEntIdentity_EHandle, uint32_t{55 + 0x8000}); Prepare();
        Check(deploys == 2 && publishes == 4 && Get<int>(weapon, clipOffset) == 6, "recreated weapon changed native ammo");
        source::Snapshot missingClock{};
        missingClock[source::Clip1] = 20;
        missingClock[source::NextPrimaryTick] = 10;
        Check(!source::Apply(pawn.data(), services.data(), weaponServices.data(), weapon.data(), missingClock, 64,
                            {1000, 15.625f, 1.0f / 64}, true) && Get<int>(weapon, clipOffset) == 6,
              "missing source clock partially applied weapon state");
        Check(!source::Rebase(4008, source::ClockKind::Tick, 4000, 128, {1000, 15.625f, 1.0f / 64}), "accepted incompatible clocks");
    }

    void CommandAxisPresence()
    {
        Reset();
        Check(mr::StartReplay(slot, false), "start legacy replay");
        mr::ReplayCommandFrame frame{};
        frame.forwardMove = frame.leftMove = frame.upMove = 999.0f;
        Check(mr::ReplayCommandFrameForSimulation(slot, frame), "read absent axes");
        Check(frame.forwardMove == 0.0f && frame.leftMove == 0.0f && frame.upMove == 0.0f,
              "absent axes must be neutral, not stale engine/AI input");

        const auto ticks = Ticks();
        std::array<BotController::ReplayCommandFrameData, 3> commands{};
        for (auto &command : commands) command.weaponSelect = -1;
        commands[0].fields = mr::kCommandFieldForwardMove | mr::kCommandFieldLeftMove;
        commands[0].forwardMove = 0.7f;
        commands[0].leftMove = -0.25f;
        Check(mr::StopReplay(slot), "stop before replacing replay buffer");
        Check(mr::LoadReplayExtended(slot, ticks.data(), 3, nullptr, 0,
                                    commands.data(), 3, nullptr, 0), "load recorded axes");
        Check(mr::StartReplay(slot, false), "start recorded axes");
        Check(mr::ReplayCommandFrameForSimulation(slot, frame), "read recorded axes");
        Check(frame.forwardMove == 0.7f && frame.leftMove == -0.25f && frame.upMove == 0.0f,
              "recorded analog input changed");
    }
}

// Replace only external engine/provider dependencies. MotionRecorder.cpp and
// ReplaySubtickLayout.cpp are linked unchanged from the production target.
namespace BotController
{
    bool TryReadMemory(const void *base, int offset, void *out, size_t size)
    {
        if (!base || !out || offset < 0) return false;
        std::memcpy(out, static_cast<const std::byte *>(base) + offset, size);
        return true;
    }
    bool TryWriteMemory(void *base, int offset, const void *value, size_t size)
    {
        if (!base || !value || offset < 0) return false;
        std::memcpy(static_cast<std::byte *>(base) + offset, value, size);
        return true;
    }
    void DebugOut(const char *) {}
    namespace BotControllerHooks { void ReleaseReplayNavigation(int) {} }
    namespace Schema
    {
        int GetFieldOffset(const char *, const char *field)
        {
            if (!std::strcmp(field, "m_ModernJump")) return 0;
            if (!std::strcmp(field, "m_pAimPunchServices")) return 0x2000;
            for (uint32_t i = 0; i < ReplaySourceState::FieldCount; ++i)
                if (!std::strcmp(field, ReplaySourceState::fields[i].field)) return 0x800 + i * 4;
            return -1;
        }
    }
    namespace InputInjector
    {
        void PublishReplayState(void *) { ++publishes; }
        void *ResolveReplayPawn(int s, void *sv) { return s == slot && sv == services.data() ? pawn.data() : nullptr; }
        void *LiveMovementServices(int s) { return s == slot ? services.data() : nullptr; }
        bool IsSlotControllingBot(int) { return humanOwnsPawn; }
        bool InitializeReplayMoveType(void *p, uint8_t value)
        {
            if (p != pawn.data()) return false;
            Put(pawn, tg::kEnt_MoveType, value); Put(pawn, tg::kEnt_ActualMoveType, value);
            return true;
        }
        bool ReadReplayClock(int, float, ReplaySourceState::LiveClock &clock) { clock = {1000, 15.625f, 1.0f / 64}; return true; }
        bool InitializeReplayPose(void *p, const float *origin, const float *velocity)
        {
            if (!allowInitialization || p != pawn.data()) return false;
            std::memcpy(node.data() + tg::kNode_AbsOrigin, origin, sizeof(float) * 3);
            std::memcpy(pawn.data() + tg::kEnt_AbsVelocity, velocity, sizeof(float) * 3);
            ++initializations;
            return true;
        }
        bool ClearUsercmdMovementIntent(int) { ++inputReleases; return true; }
        void ClearAllUsercmdMovementIntents() {}
        void ClearReplayPawn(int) {}
    }
    namespace ReplayPawnEquipment
    {
        bool PrepareForReplayStart(int) { return true; }
        bool Clear(int) { ++equipmentReleases; return true; }
        void ClearAll() {}
    }
    namespace WeaponLockerHooks
    {
        bool WeaponHooksReady() { return weaponsAvailable; }
        void *WsForSlot(int) { return weaponsAvailable ? weaponServices.data() : nullptr; }
        int ReadDefIndex(void *) { return weaponsAvailable ? 7 : -1; }
        int WeaponEntIndex(void *) { return weaponsAvailable ? 55 : -1; }
        int ActiveWeaponDef(void *) { return -1; }
        int ActiveWeaponEntIndex(void *) { return weaponActive ? 55 : -1; }
        void *FindWeaponByDef(void *, int def, int *s, unsigned int *p) {
            if (!weaponsAvailable || def != 7) return nullptr;
            *s = 0; *p = 0; return weapon.data();
        }
        void *WeaponAtInventoryPosition(void *, int, unsigned int) { return weaponsAvailable ? weapon.data() : nullptr; }
        bool SelectWeaponRaw(void *, void *) {
            if (!weaponsAvailable) return false;
            ++deploys; weaponActive = true;
            Put(weaponServices, 0x800 + ReplaySourceState::NextAttack * 4, 9999.0f);
            Put(weapon, 0x800 + ReplaySourceState::NextPrimaryTick * 4, 999999);
            return true;
        }
    }
}

int main()
{
    ContinuousInputAndEngineOutput();
    StartSeekLoopAndHeldResume();
    FinishAndHumanTakeover();
    InitializationFailureDoesNotConsumeBoundary();
    CommandAxisPresence();
    StopPreservesNativeMovementState();
    SourceStateRestoresAtBoundariesOnly();
    LadderStartRequiresContact();
    WeaponSourceRestoresOnceAfterDeploy();
    mr::ClearAll();
    return 0;
}
