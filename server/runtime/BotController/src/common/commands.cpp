// BotController console commands: bc_lock / bc_unlock / bc_unlock_all / bc_status.

#include "commands.h"
#include "avatar_overrides.h"
#include <charconv>
#include "dispatch.h"
#include "WeaponLocker.h"
#include "BotController.h"
#include "InputInjector.h"
#include "MotionRecorder.h"
#include "WeaponLockerState.h"
#include "BotControllerState.h"
#include "BuyController.h"
#include "BuyControllerState.h"

#include <tier0/dbg.h>
#include <convar.h>
#include <eiface.h>
#include <icvar.h>
#include <iserver.h>
#include <networkstringtabledefs.h>
#include <playerslot.h>

#include <cstdarg>
#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <string>
#include <vector>

namespace BotController
{
    namespace Commands
    {
        IVEngineServer2 *g_pEngine = nullptr;
        INetworkStringTableContainer *g_pStringTables = nullptr;

        // ClientPrintf to the calling player, or server log if from console.
        void PrintToCaller(const CCommandContext &context, const char *fmt, ...)
        {
            char buf[1024];
            va_list args;
            va_start(args, fmt);
            std::vsnprintf(buf, sizeof(buf), fmt, args);
            va_end(args);

            const CPlayerSlot slot = context.GetPlayerSlot();
            if (g_pEngine && slot.IsValid())
                g_pEngine->ClientPrintf(slot, buf);
            else
                Msg("%s", buf);
        }

        // Parse kind string into LockKind.
        static bool ParseKind(const char *s, LockKind &out)
        {
            if (!s)
                return false;
            if (std::strcmp(s, "all") == 0)
            {
                out = LockKind::All;
                return true;
            }
            if (std::strcmp(s, "aim") == 0)
            {
                out = LockKind::Aim;
                return true;
            }
            if (std::strcmp(s, "weapon") == 0)
            {
                out = LockKind::Weapon;
                return true;
            }
            return false;
        }

        // Parse "slotN" into LockTarget.
        static LockTarget ParseTarget(const char *s)
        {
            if (!s)
                return LockTarget::None;
            if (std::strcmp(s, "slot1") == 0)
                return LockTarget::Slot1;
            if (std::strcmp(s, "slot2") == 0)
                return LockTarget::Slot2;
            if (std::strcmp(s, "slot3") == 0)
                return LockTarget::Slot3;
            if (std::strcmp(s, "slot4") == 0)
                return LockTarget::Slot4;
            if (std::strcmp(s, "slot5") == 0)
                return LockTarget::Slot5;
            return LockTarget::None;
        }

        static const char *TargetName(LockTarget t)
        {
            switch (t)
            {
            case LockTarget::Slot1:
                return "slot1";
            case LockTarget::Slot2:
                return "slot2";
            case LockTarget::Slot3:
                return "slot3";
            case LockTarget::Slot4:
                return "slot4";
            case LockTarget::Slot5:
                return "slot5";
            default:
                return "none";
            }
        }

        static const char *KindName(LockKind k)
        {
            switch (k)
            {
            case LockKind::All:
                return "all";
            case LockKind::Aim:
                return "aim";
            case LockKind::Weapon:
                return "weapon";
            }
            return "?";
        }

        static bool ParseSteamId64(const char *text, uint64_t &id)
        {
            const char *end = text + std::strlen(text);
            const auto result = std::from_chars(text, end, id);
            return result.ec == std::errc{} && result.ptr == end && id != 0;
        }

        static bool ReadPngFile(const char *path, std::vector<unsigned char> &out,
                                char *err, size_t errLen)
        {
            if (!path || !*path)
            {
                std::snprintf(err, errLen, "empty path");
                return false;
            }

            std::ifstream file(path, std::ios::binary | std::ios::ate);
            if (!file)
            {
                std::snprintf(err, errLen, "failed to open file");
                return false;
            }

            const std::streamoff size = file.tellg();
            if (size <= 0)
            {
                std::snprintf(err, errLen, "empty file");
                return false;
            }
            if (size > 16 * 1024)
            {
                std::snprintf(err, errLen, "PNG must be 16 KiB or smaller");
                return false;
            }

            out.resize(static_cast<size_t>(size));
            file.seekg(0, std::ios::beg);
            if (!file.read(reinterpret_cast<char *>(out.data()), size))
            {
                std::snprintf(err, errLen, "failed to read file");
                return false;
            }

            static constexpr unsigned char kPngSig[8] =
                {0x89, 'P', 'N', 'G', 0x0d, 0x0a, 0x1a, 0x0a};
            if (out.size() < sizeof(kPngSig) ||
                std::memcmp(out.data(), kPngSig, sizeof(kPngSig)) != 0)
            {
                std::snprintf(err, errLen, "file is not a PNG");
                return false;
            }
            return true;
        }

    }
}

CON_COMMAND_F(bc_avatar_status, "Show native avatar publication and local HUD bridge status.", FCVAR_NONE)
{
    BotController::Commands::PrintToCaller(context, "[BC avatar] %s\n", BotController::Avatars::Status());
}

CON_COMMAND_F(bc_avatar_override_probe,
              "bc_avatar_override_probe <steamid64> <png_path>  Publish a diagnostic PNG through the native bridge.", FCVAR_NONE)
{
    using namespace BotController;
    uint64_t id = 0;
    if (args.ArgC() != 3 || !Commands::ParseSteamId64(args.Arg(1), id))
    {
        Commands::PrintToCaller(context, "usage: bc_avatar_override_probe <steamid64> <png_path>\n");
        return;
    }
    std::vector<unsigned char> png;
    char error[128]{};
    if (!Commands::ReadPngFile(args.Arg(2), png, error, sizeof(error)))
    {
        Commands::PrintToCaller(context, "[BC avatar] %s\n", error);
        return;
    }
    const int result = Avatars::Publish(id, png.data(), static_cast<int>(png.size()));
    Commands::PrintToCaller(context, "[BC avatar] publication result=%d; %s\n", result, Avatars::Status());
}

CON_COMMAND_F(bc_avatar_override_clear,
              "bc_avatar_override_clear <steamid64>  Restore the value preceding this publisher's PNG.", FCVAR_NONE)
{
    using namespace BotController;
    uint64_t id = 0;
    if (args.ArgC() != 2 || !Commands::ParseSteamId64(args.Arg(1), id))
    {
        Commands::PrintToCaller(context, "usage: bc_avatar_override_clear <steamid64>\n");
        return;
    }
    const int result = Avatars::Clear(id);
    Commands::PrintToCaller(context, "[BC avatar] restoration result=%d; %s\n", result, Avatars::Status());
}

CON_COMMAND_F(bc_lock,
              "bc_lock <all|aim|weapon> <slot> [slot1..slot5]  "
              "Lock a bot. weapon mode requires the weapon slot.",
              FCVAR_NONE)
{
    using namespace BotController;

    if (args.ArgC() < 3)
    {
        Commands::PrintToCaller(context,
                                "usage: bc_lock <all|aim|weapon> <slot> [slot1..slot5]\n");
        return;
    }

    LockKind kind;
    if (!Commands::ParseKind(args.Arg(1), kind))
    {
        Commands::PrintToCaller(context,
                                "[BC] error: kind must be all|aim|weapon\n");
        return;
    }

    const int slot = std::atoi(args.Arg(2));
    int arg = 0;

    if (kind == LockKind::Weapon)
    {
        if (args.ArgC() < 4)
        {
            Commands::PrintToCaller(context,
                                    "usage: bc_lock weapon <slot> <slot1..slot5>\n");
            return;
        }
        const auto tgt = Commands::ParseTarget(args.Arg(3));
        if (tgt == LockTarget::None)
        {
            Commands::PrintToCaller(context,
                                    "[BC] error: weapon target must be slot1..slot5\n");
            return;
        }
        arg = static_cast<int>(tgt);
    }

    int rc = Dispatch::Lock(slot, kind, arg);
    if (rc == 0)
    {
        if (kind == LockKind::Weapon)
            Commands::PrintToCaller(context,
                                    "[BC] locked slot %d weapon -> %s\n", slot,
                                    Commands::TargetName(static_cast<LockTarget>(arg)));
        else
            Commands::PrintToCaller(context,
                                    "[BC] locked slot %d (%s)\n", slot, Commands::KindName(kind));
    }
    else
    {
        Commands::PrintToCaller(context,
                                "[BC] error: lock failed (rc=%d)\n", rc);
    }
}

CON_COMMAND_F(bc_unlock,
              "bc_unlock <all|aim|weapon> <slot>  Release one lock on a bot.",
              FCVAR_NONE)
{
    using namespace BotController;

    if (args.ArgC() < 3)
    {
        Commands::PrintToCaller(context,
                                "usage: bc_unlock <all|aim|weapon> <slot>\n");
        return;
    }

    LockKind kind;
    if (!Commands::ParseKind(args.Arg(1), kind))
    {
        Commands::PrintToCaller(context,
                                "[BC] error: kind must be all|aim|weapon\n");
        return;
    }

    const int slot = std::atoi(args.Arg(2));
    int rc = Dispatch::Unlock(slot, kind);
    if (rc == 0)
        Commands::PrintToCaller(context,
                                "[BC] unlocked slot %d (%s)\n", slot, Commands::KindName(kind));
    else
        Commands::PrintToCaller(context,
                                "[BC] error: unlock failed (rc=%d)\n", rc);
}

CON_COMMAND_F(bc_unlock_all,
              "bc_unlock_all <all|aim|weapon>  Release every lock of that kind.",
              FCVAR_NONE)
{
    using namespace BotController;

    if (args.ArgC() < 2)
    {
        Commands::PrintToCaller(context,
                                "usage: bc_unlock_all <all|aim|weapon>\n");
        return;
    }

    LockKind kind;
    if (!Commands::ParseKind(args.Arg(1), kind))
    {
        Commands::PrintToCaller(context,
                                "[BC] error: kind must be all|aim|weapon\n");
        return;
    }

    int rc = Dispatch::UnlockAll(kind);
    if (rc == 0)
        Commands::PrintToCaller(context,
                                "[BC] unlocked all (%s)\n", Commands::KindName(kind));
    else
        Commands::PrintToCaller(context,
                                "[BC] error: unlock_all failed (rc=%d)\n", rc);
}

CON_COMMAND_F(bc_subtick_view_delta,
              "bc_subtick_view_delta <0|1>  Toggle replay subtick pitch/yaw delta injection.",
              FCVAR_NONE)
{
    using namespace BotController;

    if (args.ArgC() >= 2)
    {
        if (std::strcmp(args.Arg(1), "1") == 0 ||
            std::strcmp(args.Arg(1), "on") == 0)
        {
            InputInjector::SetReplaySubtickViewDeltas(true);
        }
        else if (std::strcmp(args.Arg(1), "0") == 0 ||
                 std::strcmp(args.Arg(1), "off") == 0)
        {
            InputInjector::SetReplaySubtickViewDeltas(false);
        }
        else
        {
            Commands::PrintToCaller(context,
                                    "usage: bc_subtick_view_delta <0|1>\n");
            return;
        }
    }

    Commands::PrintToCaller(context, "[BC] subtick_view_delta=%s\n",
                            InputInjector::ReplaySubtickViewDeltas() ? "on" : "off");
}

CON_COMMAND_F(bc_perf,
              "bc_perf [0|1|reset]  Toggle, reset, and print replay performance counters.",
              FCVAR_NONE)
{
    using namespace BotController;

    if (args.ArgC() >= 2)
    {
        if (std::strcmp(args.Arg(1), "1") == 0 ||
            std::strcmp(args.Arg(1), "on") == 0)
        {
            MotionRecorder::SetReplayPerfEnabled(true);
        }
        else if (std::strcmp(args.Arg(1), "0") == 0 ||
                 std::strcmp(args.Arg(1), "off") == 0)
        {
            MotionRecorder::SetReplayPerfEnabled(false);
        }
        else if (std::strcmp(args.Arg(1), "reset") == 0)
        {
            MotionRecorder::ResetReplayPerfCounters();
        }
        else
        {
            Commands::PrintToCaller(context,
                                    "usage: bc_perf [0|1|reset]\n");
            return;
        }
    }

    const MotionRecorder::ReplayPerfCounters perf =
        MotionRecorder::GetReplayPerfCounters();
    Commands::PrintToCaller(context, "[BC] perf=%s\n",
                            MotionRecorder::ReplayPerfEnabled() ? "on" : "off");
    Commands::PrintToCaller(
        context,
        "[BC] hooks: process=%llu finish=%llu usercmd=%llu physics=%llu\n",
        (unsigned long long)perf.processMovementHooks,
        (unsigned long long)perf.finishMoveHooks,
        (unsigned long long)perf.playerRunCommandHooks,
        (unsigned long long)perf.physicsSimulateHooks);
    Commands::PrintToCaller(
        context,
        "[BC] replay: tick_reads=%llu command_frames=%llu sync_local_view=%llu\n",
        (unsigned long long)perf.replayTickReads,
        (unsigned long long)perf.replayCommandFrameReads,
        (unsigned long long)perf.syncReplayLocalViewCalls);
    Commands::PrintToCaller(
        context,
        "[BC] movement: inputs=%llu initializations=%llu\n",
        (unsigned long long)perf.movementInputs,
        (unsigned long long)perf.movementInitializations);
    Commands::PrintToCaller(
        context,
        "[BC] subtick_pb: rebuilds=%llu clears=%llu noop_skips=%llu subticks_added=%llu\n",
        (unsigned long long)perf.subtickRebuilds,
        (unsigned long long)perf.subtickClears,
        (unsigned long long)perf.subtickNoopSkips,
        (unsigned long long)perf.subticksAdded);
}

CON_COMMAND_F(bc_status,
              "bc_status  Print hook status and every per-slot lock.",
              FCVAR_NONE)
{
    using namespace BotController;

    // Hooks
    Commands::PrintToCaller(context,
                            "[BC] weapon hooks: %s | EquipBest=%p EquipPistol=%p SelectItem=%p GetSlot=%p\n",
                            WeaponLockerHooks::Status(),
                            WeaponLockerHooks::EquipBestWeaponAddress(),
                            WeaponLockerHooks::EquipPistolAddress(),
                            WeaponLockerHooks::SelectItemAddress(),
                            WeaponLockerHooks::GetSlotAddress());

    Commands::PrintToCaller(context,
                            "[BC] bot hooks:    %s | Update=%p Upkeep=%p ULA=%p SEA=%p GEA=%p\n",
                            BotControllerHooks::Status(),
                            BotControllerHooks::UpdateAddress(),
                            BotControllerHooks::UpkeepAddress(),
                            BotControllerHooks::UpdateLookAnglesAddress(),
                            BotControllerHooks::SetEyeAnglesAddress(),
                            BotControllerHooks::GetEyeAnglesAddress());

    Commands::PrintToCaller(context,
                            "[BC] input inject: %s | ProcessUsercmd=%p\n",
                            InputInjector::Status(),
                            InputInjector::ProcessUsercmdAddress());

    Commands::PrintToCaller(context,
                            "[BC] usercmd hook fired: %llu times | last slot=%d\n",
                            (unsigned long long)InputInjector::HookCallCount(),
                            InputInjector::LastResolvedSlot());

    Commands::PrintToCaller(context,
                            "[BC] subtick_view_delta: %s\n",
                            InputInjector::ReplaySubtickViewDeltas() ? "on" : "off");

    Commands::PrintToCaller(context, "[BC] buy hooks:       %s | OnUpdate=%p\n",
                            BuyControllerHooks::Status(),
                            BuyControllerHooks::OnUpdateAddress());

    // All lock
    int nAll = BotControllerState::CountAll();
    Commands::PrintToCaller(context, "[BC] all-locked count:    %d\n", nAll);
    if (nAll > 0)
    {
        for (int s = 0; s < BotControllerState::kMaxSlots; ++s)
            if (BotControllerState::GetAll(s))
                Commands::PrintToCaller(context, "[BC]   all   slot %2d\n", s);
    }

    // Aim lock
    int nAim = BotControllerState::CountAim();
    Commands::PrintToCaller(context, "[BC] aim-locked count:    %d\n", nAim);
    if (nAim > 0)
    {
        for (int s = 0; s < BotControllerState::kMaxSlots; ++s)
            if (BotControllerState::GetAim(s))
                Commands::PrintToCaller(context, "[BC]   aim   slot %2d\n", s);
    }

    // Weapon lock
    int nWp = WeaponLockerState::CountLocked();
    Commands::PrintToCaller(context, "[BC] weapon-locked count: %d\n", nWp);
    if (nWp > 0)
    {
        for (int s = 0; s < WeaponLockerState::kMaxSlots; ++s)
        {
            auto t = WeaponLockerState::Get(s);
            if (t != LockTarget::None)
                Commands::PrintToCaller(context, "[BC]   weapon slot %2d -> %s\n",
                                        s, Commands::TargetName(t));
        }
    }

    int nBuy = BuyControllerState::CountPlans();
    Commands::PrintToCaller(context, "[BC] buy-plan count:      %d\n", nBuy);
    if (nBuy > 0)
    {
        for (int s = 0; s < BuyControllerState::kMaxSlots; ++s)
        {
            BuyPlan plan;
            if (!BuyControllerState::Copy(s, plan))
                continue;
            if (plan.skip)
                Commands::PrintToCaller(context, "[BC]   buy slot %2d -> skip\n", s);
            else
                Commands::PrintToCaller(context, "[BC]   buy slot %2d -> %d items\n",
                                        s, static_cast<int>(plan.items.size()));
        }
    }
}

CON_COMMAND_F(bc_buy,
              "bc_buy <slot> <alias> [alias...]  Force a bot's buy plan for each round.",
              FCVAR_NONE)
{
    using namespace BotController;

    if (args.ArgC() < 3)
    {
        Commands::PrintToCaller(context, "usage: bc_buy <slot> <alias> [alias...]\n");
        return;
    }

    const int slot = std::atoi(args.Arg(1));
    if (slot < 0 || slot >= BuyControllerState::kMaxSlots)
    {
        Commands::PrintToCaller(context, "[BC] error: slot out of range\n");
        return;
    }

    std::vector<std::string> items;
    for (int i = 2; i < args.ArgC(); ++i)
        items.emplace_back(args.Arg(i));

    BuyControllerState::Set(slot, items, false);
    Commands::PrintToCaller(context, "[BC] buy plan set slot %d (%d items)\n",
                            slot, static_cast<int>(items.size()));
}

CON_COMMAND_F(bc_buy_skip,
              "bc_buy_skip <slot>  Force a bot to buy nothing each round.",
              FCVAR_NONE)
{
    using namespace BotController;

    if (args.ArgC() < 2)
    {
        Commands::PrintToCaller(context, "usage: bc_buy_skip <slot>\n");
        return;
    }

    const int slot = std::atoi(args.Arg(1));
    if (slot < 0 || slot >= BuyControllerState::kMaxSlots)
    {
        Commands::PrintToCaller(context, "[BC] error: slot out of range\n");
        return;
    }

    BuyControllerState::Set(slot, {}, true);
    Commands::PrintToCaller(context, "[BC] buy plan set slot %d -> skip\n", slot);
}

CON_COMMAND_F(bc_unbuy,
              "bc_unbuy <slot>  Remove a bot's buy plan (back to vanilla).",
              FCVAR_NONE)
{
    using namespace BotController;

    if (args.ArgC() < 2)
    {
        Commands::PrintToCaller(context, "usage: bc_unbuy <slot>\n");
        return;
    }

    const int slot = std::atoi(args.Arg(1));
    if (slot < 0 || slot >= BuyControllerState::kMaxSlots)
    {
        Commands::PrintToCaller(context, "[BC] error: slot out of range\n");
        return;
    }

    BuyControllerState::Clear(slot);
    Commands::PrintToCaller(context, "[BC] buy plan cleared slot %d\n", slot);
}

CON_COMMAND_F(bc_unbuy_all,
              "bc_unbuy_all  Remove every bot buy plan.",
              FCVAR_NONE)
{
    using namespace BotController;
    BuyControllerState::ClearAll();
    Commands::PrintToCaller(context, "[BC] all buy plans cleared\n");
}
