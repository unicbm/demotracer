// CCSBot* -> player slot via pawn (+0x18) -> m_hController.

#include "ccsbot_slot.h"
#include "version_targets.h"

#include <cstdint>
#include <cstring>
#include <mutex>
#include <unordered_set>

#include <tier0/dbg.h>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace tg = BotController::targets;

namespace BotController
{
    // Compile-time switch to turn the once-per-bot diagnostic scan back on.
    static constexpr bool kEnableHandleScan = false;

    static std::unordered_set<void *> g_scanned;
    static std::mutex g_scannedMu;

    static int EntIndexFromHandle(uint32_t h)
    {
        if (h == 0u || h == 0xFFFFFFFFu)
            return -1;
        return static_cast<int>(h & 0x7FFFu);
    }

    bool TryReadMemory(const void *base, int offset, void *out, size_t size)
    {
        if (!base || !out || offset < 0 || size == 0)
            return false;
        const auto addr = reinterpret_cast<uintptr_t>(base) + static_cast<uintptr_t>(offset);
        if (addr < 0x10000u || addr < reinterpret_cast<uintptr_t>(base) ||
            addr + size < addr)
            return false;
        const auto *field = reinterpret_cast<const void *>(addr);

#if defined(_WIN32)
        __try
        {
            std::memcpy(out, field, size);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
#else
        std::memcpy(out, field, size);
#endif
        return true;
    }

    bool TryWriteMemory(void *base, int offset, const void *value, size_t size)
    {
        if (!base || !value || offset < 0 || size == 0)
            return false;
        const auto addr = reinterpret_cast<uintptr_t>(base) + static_cast<uintptr_t>(offset);
        if (addr < 0x10000u || addr < reinterpret_cast<uintptr_t>(base) ||
            addr + size < addr)
            return false;
        auto *field = reinterpret_cast<void *>(addr);

#if defined(_WIN32)
        __try
        {
            std::memcpy(field, value, size);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
#else
        std::memcpy(field, value, size);
#endif
        return true;
    }

    static void ScanPawnForControllerHandle(void *pawn)
    {
        if (!pawn)
            return;
        Msg("[BWL][scan] pawn=%p candidate handles idx 1..64, 0x008..0x1000:\n", pawn);
        for (int off = 0x8; off < 0x1000; off += 4)
        {
            uint32_t v = 0;
            if (!SafeRead(pawn, off, v))
                continue;
            if (v == 0u || v == 0xFFFFFFFFu)
                continue;
            int idx = static_cast<int>(v & 0x7FFFu);
            uint32_t serial = (v >> 15);
            if (idx >= 1 && idx <= 64)
                Msg("[BWL][scan]   +0x%03X = 0x%08X  idx=%d  serial=%u\n",
                    off, v, idx, serial);
        }
    }

    SlotResolution ResolveSlot(void *bot)
    {
        SlotResolution out{nullptr, -1, -1};
        if (!bot)
            return out;

        void *pawn = nullptr;
        if (!SafeRead(bot, tg::kBot_Pawn, pawn))
            return out;
        if (!pawn)
            return out;
        out.pawn = pawn;

        void *identity = nullptr;
        if (!SafeRead(pawn, tg::kEnt_Identity, identity))
            return out;
        if (identity)
        {
            uint32_t handle = 0;
            if (!SafeRead(identity, tg::kEntIdentity_EHandle, handle))
                return out;
            int idx = EntIndexFromHandle(handle);
            if (idx > 0)
                out.pawnEntIndex = idx;
        }

        // Read m_hController; fall back to m_hOriginalController if the
        // primary handle isn't populated for this pawn yet.
        uint32_t ctrlHandle = 0;
        if (!SafeRead(pawn, tg::kPawn_Controller, ctrlHandle))
            return out;
        int ctrlIdx = EntIndexFromHandle(ctrlHandle);
        if (ctrlIdx < 1 || ctrlIdx > 64)
        {
            uint32_t origHandle = 0;
            if (!SafeRead(pawn, tg::kPawn_OriginalController, origHandle))
                return out;
            ctrlIdx = EntIndexFromHandle(origHandle);
        }
        if (ctrlIdx >= 1 && ctrlIdx <= 64)
            out.slot = ctrlIdx - 1;

        if (kEnableHandleScan)
        {
            std::lock_guard<std::mutex> lk(g_scannedMu);
            if (g_scanned.insert(bot).second)
                ScanPawnForControllerHandle(pawn);
        }

        return out;
    }

    int CCSBotToSlot(void *bot)
    {
        return ResolveSlot(bot).slot;
    }

    SlotResolution ResolveSlotFromBotOrContext(void *botOrContext)
    {
        SlotResolution direct = ResolveSlot(botOrContext);
        if (direct.slot >= 0)
            return direct;

        void *bot = nullptr;
        if (!SafeRead(botOrContext, 0x10, bot))
            return direct;

        SlotResolution viaContext = ResolveSlot(bot);
        return viaContext.slot >= 0 ? viaContext : direct;
    }

    int CCSBotContextToSlot(void *botOrContext)
    {
        return ResolveSlotFromBotOrContext(botOrContext).slot;
    }

    // pawn->m_hController -> entindex -> slot. Bot-managed pawns may keep
    // their stable owner in m_hOriginalController while m_hController is unset
    // or transient, so fall back to the original controller handle.
    int ControllerSlotForPawn(void *pawn)
    {
        if (!pawn)
            return -1;
        uint32_t h = 0;
        if (!SafeRead(pawn, tg::kPawn_Controller, h))
            return -1;
        int idx = EntIndexFromHandle(h);
        if (idx < 1 || idx > 64)
        {
            uint32_t orig = 0;
            if (!SafeRead(pawn, tg::kPawn_OriginalController, orig))
                return -1;
            idx = EntIndexFromHandle(orig);
        }
        if (idx < 1 || idx > 64)
            return -1;
        return idx - 1;
    }

    // CCSPlayerController*'s own identity ehandle -> entindex -> slot.
    int ControllerToSlot(void *controller)
    {
        if (!controller)
            return -1;
        void *identity = nullptr;
        if (!SafeRead(controller, tg::kEnt_Identity, identity))
            return -1;
        if (!identity)
            return -1;
        uint32_t h = 0;
        if (!SafeRead(identity, tg::kEntIdentity_EHandle, h))
            return -1;
        int idx = EntIndexFromHandle(h);
        if (idx < 1 || idx > 64)
            return -1;
        return idx - 1;
    }
}
