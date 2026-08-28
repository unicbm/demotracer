// plugin.cpp
// All constants (offsets, vtable slots, schema candidates) live in version_targets.h

#include "plugin.h"
#include "bot_info.h"
#include "personas.h"
#include "fake_client_manager.h"
#include "ping_display.h"
#include "serversideclient_ref.h"
#include "slot_publisher.h"
#include "version_targets.h"
#include "sig_scan.h"
#include "schema_resolver.h"

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <functional>
#include <fstream>
#include <array>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include <funchook.h>
#include <nlohmann/json.hpp>
#include <entity2/entityinstance.h>

#if defined(_WIN32)
#include <Windows.h>
#else
#include <dlfcn.h>
#include <strings.h>
#endif

#include <iserver.h>
#include <eiface.h>
#include <tier1/utlvector.h>
#include <tier1/convar.h>

SH_DECL_HOOK6_void(IServerGameClients, OnClientConnected, SH_NOATTRIB, 0,
                   CPlayerSlot, const char *, uint64, const char *, const char *, bool);
SH_DECL_HOOK4_void(IServerGameClients, ClientPutInServer, SH_NOATTRIB, 0,
                   CPlayerSlot, char const *, int, uint64);
SH_DECL_HOOK5_void(IServerGameClients, ClientDisconnect, SH_NOATTRIB, 0,
                   CPlayerSlot, ENetworkDisconnectionReason, const char *, uint64, const char *);
SH_DECL_HOOK3(INetworkGameServer, StartChangeLevel, SH_NOATTRIB, 0,
              CUtlVector<INetworkGameClient *> *, const char *, const char *, void *);
SH_DECL_HOOK3_void(IServerGameDLL, GameFrame, SH_NOATTRIB, 0, bool, bool, bool);
SH_DECL_HOOK3_void(ICvar, DispatchConCommand, SH_NOATTRIB, 0,
                   ConCommandRef, const CCommandContext &, const CCommand &);
#if !defined(_WIN32)
SH_DECL_MANUALHOOK1(CreateFakeClientSlotHook, cs2bh::targets::kVTSlot_CreateFakeClient, 0, 0,
                    cs2bh::PlayerSlotHookResult, const char *);
#endif

namespace cs2bh
{

    HiderPlugin g_Plugin;

} // namespace cs2bh

PLUGIN_EXPOSE(cs2bh::HiderPlugin, cs2bh::g_Plugin);

// Interface globals

IVEngineServer *engine = nullptr;
ICvar *icvar = nullptr;
IServerGameClients *gameclients = nullptr;
IServerGameDLL *server = nullptr;
extern INetworkServerService *g_pNetworkServerService;

// GameResourceServiceServerV001
static void *g_pGameResourceService = nullptr;

// * UTIL_Remove(CEntityInstance*)
// Used to destroy the CCSPlayerController a kicked bot leaves behind

#if defined(_WIN32)
using UtilRemoveFn = void(__fastcall *)(void * /*CEntityInstance*/);
#else
using UtilRemoveFn = void (*)(void * /*CEntityInstance*/);
#endif
static UtilRemoveFn g_pfnUtilRemove = nullptr;

// Cross-check anchor: address of the CGameEntitySystem singleton global that UTIL_Remove references.
static void **g_ppEntSysGlobal = nullptr;

static funchook_t *g_pFunchook = nullptr;
static size_t g_PreparedFunchookCount = 0;
static bool g_FunchooksInstalled = false;

// * inline detour on CCSBotManager::MaintainBotQuota
#if defined(_WIN32)
using MaintainQuotaFn = int64_t(__fastcall *)(void * /*CCSBotManager*/);
#else
using MaintainQuotaFn = int64_t (*)(void * /*CCSBotManager*/);
#endif
static MaintainQuotaFn g_pfnQuotaTramp = nullptr;
static void *g_pQuotaHookTarget = nullptr;

#if defined(_WIN32)
using HandleJoinTeamFn = int64_t(__fastcall *)(void * /*CCSPlayerController*/,
                                               unsigned int, bool);
static HandleJoinTeamFn g_pfnHandleJoinTeamTramp = nullptr;
static void *g_pHandleJoinTeamHookTarget = nullptr;
#endif

#if defined(_WIN32)
using PackEntitiesFn = void(__fastcall *)(void *, void *, int, void *, void *);
#else
using PackEntitiesFn = void (*)(void *, void *, int, void *, void *);
#endif
static PackEntitiesFn g_pfnPackEntitiesTramp = nullptr;
static void *g_pPackEntitiesHookTarget = nullptr;
static std::atomic_bool g_PackEntitiesFirstCallLogged = false;
static std::recursive_mutex g_PackEntitiesMutex;
static thread_local uint32_t g_PackEntitiesDepth = 0;

struct BotPawnRef
{
    void *Instance = nullptr;
    uint32_t Handle = 0xFFFFFFFF;
};

struct ManagedControllerFlagSnapshot
{
    void *Client = nullptr;
    void *Controller = nullptr;
    uint32_t Handle = 0xFFFFFFFF;
    uint16_t UserId = 0;
    bool Modified = false;
};

struct PendingControllerRemoval
{
    void *Controller = nullptr;
    uint32_t Handle = 0xFFFFFFFF;
    int Slot = -1;
    uint16_t UserId = 0;
    unsigned int ReferencedFrames = 0;
};

static std::vector<PendingControllerRemoval> g_PendingControllerRemovals;

#if defined(_WIN32)
struct ManagedControllerTrace
{
    int Slot = -1;
    uint32_t Handle = 0xFFFFFFFF;
    uint32_t Flags = 0;
    unsigned int CurrentTeam = 0;
    bool Managed = false;
    bool Hltv = false;
};
#endif

namespace cs2bh
{
    std::vector<BotPawnRef> ApplyBotFlagOverride();
    void RestoreBotFlagOverride(const std::vector<BotPawnRef> &pawns);
#if defined(_WIN32)
    ManagedControllerTrace TraceManagedController(void *controller);
    bool SetJoinTeamFakeClientFlag(void *controller, uint32_t handle, bool enabled);
#endif
}

class PackEntitiesDepthGuard
{
public:
    // Marks the current thread as executing the outer packing callback
    PackEntitiesDepthGuard()
    {
        ++g_PackEntitiesDepth;
    }

    // Clears the current thread packing depth
    ~PackEntitiesDepthGuard()
    {
        --g_PackEntitiesDepth;
    }
};

class ScopedBotFlagOverride
{
public:
    // Clears FL_BOT and marks changed fields before entity packing
    ScopedBotFlagOverride() : m_ModifiedPawns(cs2bh::ApplyBotFlagOverride())
    {
    }

    // Restores only FL_BOT after entity packing without marking changes
    ~ScopedBotFlagOverride()
    {
        cs2bh::RestoreBotFlagOverride(m_ModifiedPawns);
    }

private:
    std::vector<BotPawnRef> m_ModifiedPawns;
};

#if defined(_WIN32)
class ScopedJoinTeamFakeClientFlag
{
public:
    // Temporarily restores only the controller bot bit needed by team validation.
    ScopedJoinTeamFakeClientFlag(void *controller, uint32_t handle, bool enable)
        : m_Controller(controller),
          m_Handle(handle),
          m_Applied(enable && cs2bh::SetJoinTeamFakeClientFlag(controller, handle, true))
    {
    }

    // Clear only the bit this scope added; do not publish a transient state change.
    ~ScopedJoinTeamFakeClientFlag()
    {
        if (m_Applied && !cs2bh::SetJoinTeamFakeClientFlag(m_Controller, m_Handle, false))
            META_CONPRINTF("[BOTHIDER] warning: failed to restore JoinTeam fake-client scope\n");
    }

    bool Applied() const { return m_Applied; }

private:
    void *m_Controller = nullptr;
    uint32_t m_Handle = 0xFFFFFFFF;
    bool m_Applied = false;
};
#endif

// Passes entity packing through unchanged and logs the first calling thread
#if defined(_WIN32)
static void __fastcall Detour_PackEntities(void *serverObject, void *packContext,
                                          int clientCount, void *clients,
                                          void *snapshotContext)
#else
static void Detour_PackEntities(void *serverObject, void *packContext,
                                int clientCount, void *clients,
                                void *snapshotContext)
#endif
{
    if (!g_PackEntitiesFirstCallLogged.exchange(true, std::memory_order_relaxed))
    {
        size_t threadId = std::hash<std::thread::id>{}(std::this_thread::get_id());
        META_CONPRINTF("[BOTHIDER] CNetworkGameServer::PackEntities first entered on thread %zu\n",
                       threadId);
    }

    std::lock_guard<std::recursive_mutex> lock(g_PackEntitiesMutex);
    if (g_PackEntitiesDepth != 0)
    {
        g_pfnPackEntitiesTramp(serverObject, packContext, clientCount, clients, snapshotContext);
        return;
    }

    PackEntitiesDepthGuard depthGuard;
    ScopedBotFlagOverride flagOverride;
    g_pfnPackEntitiesTramp(serverObject, packContext, clientCount, clients, snapshotContext);
}

// Prepares one target and replaces its original pointer with the trampoline
template <typename Function>
static bool PrepareFunchook(Function &original, void *target, void *detour, const char *name)
{
    if (!g_pFunchook)
    {
        g_pFunchook = funchook_create();
        if (!g_pFunchook)
        {
            META_CONPRINTF("[BOTHIDER] warning: funchook_create failed for %s\n", name);
            return false;
        }
    }

    void *trampoline = target;
    int result = funchook_prepare(g_pFunchook, &trampoline, detour);
    if (result != FUNCHOOK_ERROR_SUCCESS)
    {
        META_CONPRINTF("[BOTHIDER] warning: funchook_prepare failed for %s: %s (%d)\n",
                       name, funchook_error_message(g_pFunchook), result);
        original = nullptr;
        return false;
    }

    original = reinterpret_cast<Function>(trampoline);
    ++g_PreparedFunchookCount;
    return true;
}

// Clears all published funchook targets and trampoline pointers
static void ClearFunchookBindings()
{
    g_pfnQuotaTramp = nullptr;
    g_pfnPackEntitiesTramp = nullptr;
    g_pQuotaHookTarget = nullptr;
    g_pPackEntitiesHookTarget = nullptr;
#if defined(_WIN32)
    g_pfnHandleJoinTeamTramp = nullptr;
    g_pHandleJoinTeamHookTarget = nullptr;
#endif
    g_PreparedFunchookCount = 0;
    g_FunchooksInstalled = false;
}

// Installs every successfully prepared hook through the shared handle
static void InstallPreparedFunchooks()
{
    if (!g_pFunchook || g_PreparedFunchookCount == 0)
    {
        if (g_pFunchook)
            funchook_destroy(g_pFunchook);
        g_pFunchook = nullptr;
        ClearFunchookBindings();
        return;
    }

    int result = funchook_install(g_pFunchook, 0);
    if (result != FUNCHOOK_ERROR_SUCCESS)
    {
        META_CONPRINTF("[BOTHIDER] warning: funchook_install failed: %s (%d)\n",
                       funchook_error_message(g_pFunchook), result);
        funchook_destroy(g_pFunchook);
        g_pFunchook = nullptr;
        ClearFunchookBindings();
        return;
    }

    g_FunchooksInstalled = true;
    if (g_pQuotaHookTarget)
        META_CONPRINTF("[BOTHIDER] bot-quota fix installed at %p\n", g_pQuotaHookTarget);
    if (g_pPackEntitiesHookTarget)
        META_CONPRINTF("[BOTHIDER] CNetworkGameServer::PackEntities hook installed at %p\n",
                       g_pPackEntitiesHookTarget);
#if defined(_WIN32)
    if (g_pHandleJoinTeamHookTarget)
        META_CONPRINTF("[BOTHIDER] CCSPlayerController::HandleCommand_JoinTeam hook installed at %p\n",
                       g_pHandleJoinTeamHookTarget);
#endif
}

// Uninstalls all hooks before releasing their shared funchook handle
static bool RemoveFunchooks()
{
    if (g_PackEntitiesDepth != 0)
    {
        META_CONPRINTF("[BOTHIDER] error: refusing funchook removal during PackEntities\n");
        return false;
    }

    std::unique_lock<std::recursive_mutex> lock(g_PackEntitiesMutex);
    if (!g_pFunchook)
    {
        ClearFunchookBindings();
        return true;
    }

    if (g_FunchooksInstalled)
    {
        int result = funchook_uninstall(g_pFunchook, 0);
        if (result != FUNCHOOK_ERROR_SUCCESS)
        {
            std::string message = funchook_error_message(g_pFunchook);
            lock.unlock();
            META_CONPRINTF("[BOTHIDER] error: funchook_uninstall failed: %s (%d)\n",
                           message.c_str(), result);
            return false;
        }
    }

    int result = funchook_destroy(g_pFunchook);
    std::string destroyMessage;
    if (result != FUNCHOOK_ERROR_SUCCESS)
        destroyMessage = funchook_error_message(g_pFunchook);
    g_pFunchook = nullptr;
    ClearFunchookBindings();
    lock.unlock();
    if (result != FUNCHOOK_ERROR_SUCCESS)
        META_CONPRINTF("[BOTHIDER] warning: funchook_destroy failed: %s (%d)\n",
                       destroyMessage.c_str(), result);
    return true;
}

namespace cs2bh
{
    // Flip managed bots' controller fakeclient bit around the engine quota pass.
    int FlipManagedController904(
        bool restore,
        std::array<ManagedControllerFlagSnapshot, 64> *saved);
}

// Detour
#if defined(_WIN32)
static int64_t __fastcall Detour_MaintainBotQuota(void *mgr)
#else
static int64_t Detour_MaintainBotQuota(void *mgr)
#endif
{
    std::array<ManagedControllerFlagSnapshot, 64> flipped{};
    cs2bh::FlipManagedController904(false, &flipped);
    int64_t r = g_pfnQuotaTramp ? g_pfnQuotaTramp(mgr) : 0;
    cs2bh::FlipManagedController904(true, &flipped);
    return r;
}

#if defined(_WIN32)
// Restore bot identity only while the engine validates an initial team join.
static int64_t __fastcall Detour_HandleCommandJoinTeam(void *controller,
                                                       unsigned int requestedTeam,
                                                       bool unknownFlag)
{
    ManagedControllerTrace trace = cs2bh::TraceManagedController(controller);
    const bool needsFakeClientScope = trace.Managed && !trace.Hltv &&
                                      (trace.Flags & 0x100u) == 0;
    ScopedJoinTeamFakeClientFlag fakeClientScope(
        controller, trace.Handle, needsFakeClientScope);
    if (fakeClientScope.Applied())
    {
        META_CONPRINTF(
            "[BOTHIDER] HandleCommand_JoinTeam bot scope ctrl=%p slot=%d current=%u requested=%u\n",
            controller, trace.Slot, trace.CurrentTeam, requestedTeam);
    }

    return g_pfnHandleJoinTeamTramp
               ? g_pfnHandleJoinTeamTramp(controller, requestedTeam, unknownFlag)
               : 0;
}
#endif

namespace cs2bh
{

#if !defined(_WIN32)
    struct FakeClientCallContext
    {
        const BotEntry *Entry = nullptr;
        std::string RequestedName;
        std::string Persona;
        std::string ConnectedName;
        bool UseBotInfoName = false;
        bool Enabled = false;
        bool ConnectedObserved = false;
        int ConnectedSlot = -1;
    };

    // Keep the upstream Linux CreateFakeClient context path unchanged
    static thread_local std::vector<FakeClientCallContext> g_FakeClientCallStack;
#endif

    // Per-slot bound bot_info entry
    static std::array<const BotEntry *, PersonaPool::kMaxSlots> g_SlotEntry{};

    // Original engine names used when an HLTV slot was adopted before its flag initialized
    static std::array<std::string, PersonaPool::kMaxSlots> g_OriginalSlotName{};

    // Maps where bots stay disguised, loaded from map_whitelist.json
    static std::vector<std::string> g_DisguiseWhitelist;

    // Current controller field used to resolve each managed bot pawn
    static int g_BotPawnHandleOffset = -1;

#if !defined(_WIN32)
    static void ClearFakeClientCallStack()
    {
        for (const auto &context : g_FakeClientCallStack)
            BotInfo().ReleaseAssignment(context.Entry);
        g_FakeClientCallStack.clear();
    }
#endif

    // Resolve CServerSideClient* for a slot
    static void *ResolveClientBySlot(int slot)
    {
        if (!g_pNetworkServerService)
            return nullptr;
        auto *gs = g_pNetworkServerService->GetIGameServer();
        if (!gs)
            return nullptr;
        auto *vec = reinterpret_cast<CUtlVector<void *> *>(
            reinterpret_cast<unsigned char *>(gs) + targets::kClientListOffset);
        int count = vec->Count();
        if (count < 0 || count > 256 || slot < 0 || slot >= count)
            return nullptr;
        return vec->Element(slot);
    }

    // Rebuild CMsgPlayerInfo from CServerSideClient and broadcast it
    static bool RefreshClientUserInfo(int slot)
    {
        if (!g_pNetworkServerService || slot < 0 || slot >= PersonaPool::kMaxSlots)
            return false;
        auto *gameServer = g_pNetworkServerService->GetIGameServer();
        if (!gameServer)
            return false;

        gameServer->UserInfoChanged(CPlayerSlot(slot));
        return true;
    }

    // Count online human clients
    static int CountHumanClients()
    {
        if (!g_pNetworkServerService)
            return 0;
        auto *gs = g_pNetworkServerService->GetIGameServer();
        if (!gs)
            return 0;
        auto *vec = reinterpret_cast<CUtlVector<void *> *>(
            reinterpret_cast<unsigned char *>(gs) + targets::kClientListOffset);
        int count = vec->Count();
        if (count < 0 || count > 256)
            return 0;
        int humans = 0;
        for (int i = 0; i < count; ++i)
        {
            void *pClient = vec->Element(i);
            if (!pClient)
                continue;
            void *netChan = *reinterpret_cast<void **>(
                reinterpret_cast<unsigned char *>(pClient) + ssc::OFFSET_m_NetChannel);
            if (netChan)
                ++humans;
        }
        return humans;
    }

    // True if sid is already live on any connected client other than exceptSlot
    static bool IsSteamIdInUseByOther(uint64_t sid, int exceptSlot)
    {
        if (sid == 0 || !g_pNetworkServerService)
            return false;
        auto *gs = g_pNetworkServerService->GetIGameServer();
        if (!gs)
            return false;
        auto *vec = reinterpret_cast<CUtlVector<void *> *>(
            reinterpret_cast<unsigned char *>(gs) + targets::kClientListOffset);
        int count = vec->Count();
        if (count < 0 || count > 256)
            return false;
        for (int i = 0; i < count; ++i)
        {
            if (i == exceptSlot)
                continue;
            void *pClient = vec->Element(i);
            if (!pClient)
                continue;
            uint64_t other = *reinterpret_cast<uint64_t *>(
                reinterpret_cast<unsigned char *>(pClient) + ssc::OFFSET_m_SteamID);
            if (other == sid)
                return true;
        }
        return false;
    }

    // Resolve a SteamID for slot that collides with other client
    static uint64_t MakeUniqueSteamId(int slot, uint64_t desired)
    {
        if (desired != 0 && !IsSteamIdInUseByOther(desired, slot))
            return desired;

        // Scan bot_info entries for a non-colliding SteamID64
        for (const auto &e : BotInfo().All())
        {
            if (e.SteamId64 != 0 && !IsSteamIdInUseByOther(e.SteamId64, slot))
                return e.SteamId64;
        }

        // Bump the AccountId off a base until it is free
        uint64_t base = desired != 0 ? desired : BotInfoStore::kSteamId64Base + 1;
        for (int bump = 1; bump <= 4096; ++bump)
        {
            uint64_t candidate = base + static_cast<uint64_t>(bump);
            if (!IsSteamIdInUseByOther(candidate, slot))
                return candidate;
        }
        return desired; // give up
    }

    // Resolve UTIL_Remove from the server module
    static void ResolveUtilRemoveAndEntSys(const nlohmann::json &gamedata, const sig::ModuleInfo &serverModule)
    {
        if (!serverModule)
        {
            META_CONPRINTF("[BOTHIDER] warning: %s module unresolved for signature scan\n",
                           targets::kServerModuleName);
            return;
        }
        std::string sigStr = sig::FindPlatformSig(gamedata, "UTIL_Remove");
        std::vector<uint8_t> bytes;
        std::vector<bool> wild;
        if (sigStr.empty() || !sig::ParseSigString(sigStr, bytes, wild))
        {
            META_CONPRINTF("[BOTHIDER] warning: UTIL_Remove %s sig missing/malformed in gamedata.json\n",
                           sig::PlatformName());
            return;
        }
        auto *hit = static_cast<unsigned char *>(sig::FindPatternIn(serverModule, bytes, wild));
        if (!hit)
            return;
        g_pfnUtilRemove = reinterpret_cast<UtilRemoveFn>(hit);

        // Windows: mov rcx, [rip+disp32]
        // Linux:   lea rax, [rip+disp32]; mov rdi, [rax]
        for (size_t i = 0; i + 7 <= bytes.size(); ++i)
        {
            bool isWindowsMov = bytes[i] == 0x48 && bytes[i + 1] == 0x8B && bytes[i + 2] == 0x0D;
            bool isLinuxLea = bytes[i] == 0x48 && bytes[i + 1] == 0x8D && bytes[i + 2] == 0x05;
            if (isWindowsMov || isLinuxLea)
            {
                unsigned char *dispAt = hit + i + 3; // first byte of disp32
                int32_t disp = *reinterpret_cast<int32_t *>(dispAt);
                unsigned char *instrEnd = dispAt + 4; // RIP points past the instr
                g_ppEntSysGlobal = reinterpret_cast<void **>(instrEnd + disp);
                break;
            }
        }
    }

    // Load engine-private member offsets from gamedata.json. Compiled defaults
    // remain unavailable so a missing entry cannot silently select an old CS2
    // layout.
    static void LoadMemberOffsets(const nlohmann::json &gamedata)
    {
        using sig::FindPlatformOffset;
        // CServerSideClient layout — shifts as a block across game updates
        ssc::OFFSET_m_Name = FindPlatformOffset(gamedata, "CServerSideClient::m_Name", ssc::OFFSET_m_Name);
        ssc::OFFSET_m_nEntityIndex = FindPlatformOffset(gamedata, "CServerSideClient::m_nEntityIndex", ssc::OFFSET_m_nEntityIndex);
        ssc::OFFSET_m_NetChannel = FindPlatformOffset(gamedata, "CServerSideClient::m_NetChannel", ssc::OFFSET_m_NetChannel);
        ssc::OFFSET_m_nConnectionTypeFlags = FindPlatformOffset(gamedata, "CServerSideClient::m_nConnectionTypeFlags", ssc::OFFSET_m_nConnectionTypeFlags);
        ssc::OFFSET_m_bFakePlayer = FindPlatformOffset(gamedata, "CServerSideClient::m_bFakePlayer", ssc::OFFSET_m_bFakePlayer);
        ssc::OFFSET_m_UserID = FindPlatformOffset(gamedata, "CServerSideClient::m_UserID", ssc::OFFSET_m_UserID);
        ssc::OFFSET_m_SteamID = FindPlatformOffset(gamedata, "CServerSideClient::m_SteamID", ssc::OFFSET_m_SteamID);
        ssc::OFFSET_m_SteamIDMirror = FindPlatformOffset(gamedata, "CServerSideClient::m_SteamIDMirror", ssc::OFFSET_m_SteamIDMirror);
        ssc::OFFSET_m_bIsHLTV = FindPlatformOffset(gamedata, "CServerSideClient::m_bIsHLTV", ssc::OFFSET_m_bIsHLTV);
        // CNetworkGameServerBase::m_Clients vector base
        targets::kClientListOffset = FindPlatformOffset(gamedata, "CNetworkGameServerBase::m_Clients", targets::kClientListOffset);
        // CBasePlayerController fakeclient flags
        targets::kController_FakeClientFlagsOffset = FindPlatformOffset(gamedata, "CBasePlayerController::FakeClientFlags", targets::kController_FakeClientFlagsOffset);
#if defined(_WIN32)
        targets::kVTSlot_ClientSetName = FindPlatformOffset(
            gamedata, "CServerSideClient::SetName", targets::kVTSlot_ClientSetName);
#endif
        targets::kEntSys_OffsetInGameResSvc = FindPlatformOffset(
            gamedata, "GameResourceServiceServer::m_pEntitySystem",
            targets::kEntSys_OffsetInGameResSvc);
        targets::kEntSys_IdentityChunksOffset = FindPlatformOffset(
            gamedata, "CEntitySystem::m_EntityList",
            targets::kEntSys_IdentityChunksOffset);
        targets::kEntIdentity_Size = FindPlatformOffset(
            gamedata, "CEntityIdentity::Size", targets::kEntIdentity_Size);
        targets::kEntIdentity_InstanceOffset = FindPlatformOffset(
            gamedata, "CEntityIdentity::m_pInstance",
            targets::kEntIdentity_InstanceOffset);
        targets::kEntIdentity_ClassNameOffset = FindPlatformOffset(
            gamedata, "CEntityIdentity::m_designerName",
            targets::kEntIdentity_ClassNameOffset);
    }

    static const char *FirstMissingMemberOffset()
    {
        const std::array<std::pair<const char *, int>, 16> required = {{
            {"CServerSideClient::m_Name", ssc::OFFSET_m_Name},
            {"CServerSideClient::m_nEntityIndex", ssc::OFFSET_m_nEntityIndex},
            {"CServerSideClient::m_NetChannel", ssc::OFFSET_m_NetChannel},
            {"CServerSideClient::m_nConnectionTypeFlags", ssc::OFFSET_m_nConnectionTypeFlags},
            {"CServerSideClient::m_bFakePlayer", ssc::OFFSET_m_bFakePlayer},
            {"CServerSideClient::m_UserID", ssc::OFFSET_m_UserID},
            {"CServerSideClient::m_SteamID", ssc::OFFSET_m_SteamID},
            {"CServerSideClient::m_SteamIDMirror", ssc::OFFSET_m_SteamIDMirror},
            {"CServerSideClient::m_bIsHLTV", ssc::OFFSET_m_bIsHLTV},
            {"CNetworkGameServerBase::m_Clients", targets::kClientListOffset},
            {"CBasePlayerController::FakeClientFlags", targets::kController_FakeClientFlagsOffset},
            {"GameResourceServiceServer::m_pEntitySystem", targets::kEntSys_OffsetInGameResSvc},
            {"CEntitySystem::m_EntityList", targets::kEntSys_IdentityChunksOffset},
            {"CEntityIdentity::Size", targets::kEntIdentity_Size},
            {"CEntityIdentity::m_pInstance", targets::kEntIdentity_InstanceOffset},
            {"CEntityIdentity::m_designerName", targets::kEntIdentity_ClassNameOffset},
        }};
        for (const auto &[name, offset] : required)
        {
            if (offset < 0)
                return name;
        }
#if defined(_WIN32)
        if (targets::kVTSlot_ClientSetName < 0)
            return "CServerSideClient::SetName";
#endif
        return nullptr;
    }

    // Resolves and prepares the bot quota flip-around detour
    static void InstallQuotaHook(const nlohmann::json &gamedata, const sig::ModuleInfo &serverModule)
    {
        if (!serverModule)
            return;
        std::string sigStr = sig::FindPlatformSig(gamedata, "CCSBotManager::MaintainBotQuota");
        std::vector<uint8_t> bytes;
        std::vector<bool> wild;
        if (sigStr.empty() || !sig::ParseSigString(sigStr, bytes, wild))
        {
            META_CONPRINTF("[BOTHIDER] warning: MaintainBotQuota sig missing — quota fix disabled\n");
            return;
        }
        void *target = sig::FindPatternIn(serverModule, bytes, wild);
        if (!target)
        {
            META_CONPRINTF("[BOTHIDER] warning: MaintainBotQuota sig not found — quota fix disabled\n");
            return;
        }
        if (PrepareFunchook(g_pfnQuotaTramp, target,
                            reinterpret_cast<void *>(&Detour_MaintainBotQuota),
                            "CCSBotManager::MaintainBotQuota"))
            g_pQuotaHookTarget = target;
    }

#if defined(_WIN32)
    // Resolves the team-join validation path without changing persistent identity.
    static void InstallHandleJoinTeamHook(const nlohmann::json &gamedata,
                                          const sig::ModuleInfo &serverModule)
    {
        if (!serverModule)
            return;

        std::string sigString = sig::FindPlatformSig(
            gamedata, "CCSPlayerController::HandleCommand_JoinTeam");
        std::vector<uint8_t> bytes;
        std::vector<bool> wild;
        if (sigString.empty() || !sig::ParseSigString(sigString, bytes, wild))
        {
            META_CONPRINTF("[BOTHIDER] warning: HandleCommand_JoinTeam signature missing or malformed\n");
            return;
        }

        std::vector<void *> matches = sig::FindPatternMatchesIn(serverModule, bytes, wild);
        META_CONPRINTF("[BOTHIDER] CCSPlayerController::HandleCommand_JoinTeam signature matches=%zu\n",
                       matches.size());
        if (matches.size() != 1)
        {
            META_CONPRINTF("[BOTHIDER] warning: HandleCommand_JoinTeam hook requires exactly one match\n");
            return;
        }

        void *target = matches.front();
        if (PrepareFunchook(g_pfnHandleJoinTeamTramp, target,
                            reinterpret_cast<void *>(&Detour_HandleCommandJoinTeam),
                            "CCSPlayerController::HandleCommand_JoinTeam"))
            g_pHandleJoinTeamHookTarget = target;
    }
#endif

    // Resolves and prepares the pass-through engine entity-packing detour
    static void InstallPackEntitiesHook(const nlohmann::json &gamedata)
    {
        std::string sigString = sig::FindPlatformSig(gamedata, "CNetworkGameServer::PackEntities");
        std::vector<uint8_t> bytes;
        std::vector<bool> wild;
        if (sigString.empty() || !sig::ParseSigString(sigString, bytes, wild))
        {
            META_CONPRINTF("[BOTHIDER] warning: PackEntities signature missing or malformed\n");
            return;
        }

        sig::ModuleInfo codeModule = sig::ModuleCodeFromName(targets::kEngineModuleName);
        if (!codeModule)
        {
            META_CONPRINTF("[BOTHIDER] warning: %s code range unresolved - PackEntities hook disabled\n",
                           targets::kEngineModuleName);
            return;
        }

        std::vector<void *> matches = sig::FindPatternMatchesIn(codeModule, bytes, wild);
        META_CONPRINTF("[BOTHIDER] CNetworkGameServer::PackEntities signature matches=%zu\n",
                       matches.size());
        if (matches.size() != 1)
        {
            META_CONPRINTF("[BOTHIDER] warning: PackEntities hook requires exactly one match\n");
            return;
        }

        void *target = matches.front();
        g_PackEntitiesFirstCallLogged.store(false, std::memory_order_relaxed);
        if (PrepareFunchook(g_pfnPackEntitiesTramp, target,
                            reinterpret_cast<void *>(&Detour_PackEntities),
                            "CNetworkGameServer::PackEntities"))
            g_pPackEntitiesHookTarget = target;
    }

    // SEH-isolated single reads, used by the controller-resolution walk
    static bool SehReadPtr(const void *addr, void **out)
    {
#if defined(_WIN32)
        __try
        {
            *out = *reinterpret_cast<void *const *>(addr);
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            *out = nullptr;
            return false;
        }
#else
        if (!addr)
        {
            *out = nullptr;
            return false;
        }
        *out = *reinterpret_cast<void *const *>(addr);
        return true;
#endif
    }
    // Treat addr as a char** , deref then copy
    static bool SehReadCStr(const void *addr, char *out, size_t cap)
    {
#if defined(_WIN32)
        __try
        {
            const char *p = *reinterpret_cast<const char *const *>(addr);
            if (!p)
            {
                out[0] = '\0';
                return false;
            }
            size_t i = 0;
            for (; i + 1 < cap && p[i]; ++i)
                out[i] = p[i];
            out[i] = '\0';
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            out[0] = '\0';
            return false;
        }
#else
        if (!addr)
        {
            out[0] = '\0';
            return false;
        }
        const char *p = *reinterpret_cast<const char *const *>(addr);
        if (!p)
        {
            out[0] = '\0';
            return false;
        }
        size_t i = 0;
        for (; i + 1 < cap && p[i]; ++i)
            out[i] = p[i];
        out[i] = '\0';
        return true;
#endif
    }

    // Resolve a CEntityInstance* by entity index
    static void *ResolveEntityInstance(int entityIndex, char *classnameOut, size_t classnameCap)
    {
        if (classnameOut && classnameCap)
            classnameOut[0] = '\0';
        if (!g_pGameResourceService || entityIndex <= 0 || entityIndex >= 0x8000 ||
            targets::kEntSys_OffsetInGameResSvc < 0 ||
            targets::kEntSys_IdentityChunksOffset < 0 ||
            targets::kEntIdentity_Size <= 0 ||
            targets::kEntIdentity_InstanceOffset < 0 ||
            (classnameOut && classnameCap && targets::kEntIdentity_ClassNameOffset < 0))
            return nullptr;

        void *entSys = nullptr;
        if (!SehReadPtr(reinterpret_cast<unsigned char *>(g_pGameResourceService) +
                            targets::kEntSys_OffsetInGameResSvc,
                        &entSys) ||
            !entSys)
            return nullptr;

        void *chunk = nullptr;
        const void *chunkSlot = reinterpret_cast<unsigned char *>(entSys) +
                                targets::kEntSys_IdentityChunksOffset +
                                (entityIndex / targets::kEntListChunkSize) * sizeof(void *);
        if (!SehReadPtr(chunkSlot, &chunk) || !chunk)
            return nullptr;

        unsigned char *identity = reinterpret_cast<unsigned char *>(chunk) +
                                  (entityIndex % targets::kEntListChunkSize) * targets::kEntIdentity_Size;
        if (classnameOut && classnameCap)
            SehReadCStr(identity + targets::kEntIdentity_ClassNameOffset,
                        classnameOut, classnameCap);

        void *instance = nullptr;
        if (!SehReadPtr(identity + targets::kEntIdentity_InstanceOffset, &instance) || !instance)
            return nullptr;
        return instance;
    }

    static bool IsEntityBeingDeleted(void *instance);

#if defined(_WIN32)
    // Collect the managed-slot identity for the controller passed to JoinTeam.
    ManagedControllerTrace TraceManagedController(void *controller)
    {
        ManagedControllerTrace trace;
        if (!controller || targets::kController_FakeClientFlagsOffset < 0)
            return trace;

        __try
        {
            trace.Flags = *reinterpret_cast<uint32_t *>(
                reinterpret_cast<unsigned char *>(controller) +
                targets::kController_FakeClientFlagsOffset);
            if (targets::kController_TeamOffset >= 0)
            {
                trace.CurrentTeam = *reinterpret_cast<unsigned char *>(
                    reinterpret_cast<unsigned char *>(controller) +
                    targets::kController_TeamOffset);
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return trace;
        }

        for (int idx = 0; idx < PersonaPool::kMaxSlots; ++idx)
        {
            if (!Manager().IsManaged(idx))
                continue;
            void *client = ResolveClientBySlot(idx);
            if (!client)
                continue;
            int entityIndex = *reinterpret_cast<int *>(
                reinterpret_cast<unsigned char *>(client) + ssc::OFFSET_m_nEntityIndex);
            char className[64];
            void *resolved = ResolveEntityInstance(entityIndex, className, sizeof(className));
            if (resolved != controller || std::strcmp(className, "cs_player_controller") != 0 ||
                IsEntityBeingDeleted(resolved))
                continue;

            trace.Slot = idx;
            trace.Handle = static_cast<uint32_t>(
                reinterpret_cast<CEntityInstance *>(resolved)->GetRefEHandle().ToInt());
            trace.Managed = true;
            trace.Hltv = ssc::IsHltv(client);
            break;
        }
        return trace;
    }

    // Toggle only the transient controller bit; never mark it for replication.
    bool SetJoinTeamFakeClientFlag(void *controller, uint32_t handle, bool enabled)
    {
        if (!controller || handle == 0xFFFFFFFF ||
            targets::kController_FakeClientFlagsOffset < 0)
            return false;

        const int entityIndex = static_cast<int>(handle & 0x7FFF);
        char className[64];
        void *current = ResolveEntityInstance(entityIndex, className, sizeof(className));
        if (current != controller || std::strcmp(className, "cs_player_controller") != 0 ||
            IsEntityBeingDeleted(current) ||
            static_cast<uint32_t>(
                reinterpret_cast<CEntityInstance *>(current)->GetRefEHandle().ToInt()) != handle)
        {
            return false;
        }

        __try
        {
            auto *flags = reinterpret_cast<uint32_t *>(
                reinterpret_cast<unsigned char *>(current) +
                targets::kController_FakeClientFlagsOffset);
            if (enabled)
            {
                if ((*flags & 0x100u) != 0)
                    return false;
                *flags |= 0x100u;
            }
            else
                *flags &= ~0x100u;
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
    }
#endif

    // Returns true when an entity is already entering its destruction path
    static bool IsEntityBeingDeleted(void *instance)
    {
        if (!instance)
            return true;
        auto *entity = reinterpret_cast<CEntityInstance *>(instance);
        if (!entity->m_pEntity)
            return true;
        uint32_t flags = static_cast<uint32_t>(entity->m_pEntity->m_flags);
        return (flags & (EF_DELETE_IN_PROGRESS | EF_MARKED_FOR_DELETE)) != 0;
    }

    // Marks one flattened entity field offset as changed
    static void MarkEntityFieldChanged(void *instance, uint32_t offset)
    {
        if (!instance)
            return;
        NetworkStateChangedData changed(offset);
        reinterpret_cast<CEntityInstance *>(instance)->NetworkStateChanged(changed);
    }

    // Resolves the current pawn handle for one managed bot slot
    static BotPawnRef ResolveManagedBotPawn(int slot)
    {
        BotPawnRef result;
        if (g_BotPawnHandleOffset < 0 || !Manager().IsManaged(slot))
            return result;

        void *client = ResolveClientBySlot(slot);
        if (!client)
            return result;

        int controllerIndex = *reinterpret_cast<int *>(
            reinterpret_cast<unsigned char *>(client) + ssc::OFFSET_m_nEntityIndex);
        char className[64];
        void *controller = ResolveEntityInstance(controllerIndex, className, sizeof(className));
        if (!controller || std::strcmp(className, "cs_player_controller") != 0 ||
            IsEntityBeingDeleted(controller))
            return result;

        uint32_t pawnHandle = *reinterpret_cast<uint32_t *>(
            reinterpret_cast<unsigned char *>(controller) + g_BotPawnHandleOffset);
        if (pawnHandle == 0xFFFFFFFF)
            return result;

        int pawnIndex = static_cast<int>(pawnHandle & 0x7FFF);
        void *pawn = ResolveEntityInstance(pawnIndex, nullptr, 0);
        if (!pawn || IsEntityBeingDeleted(pawn))
            return result;

        auto *pawnEntity = reinterpret_cast<CEntityInstance *>(pawn);
        if (static_cast<uint32_t>(pawnEntity->GetRefEHandle().ToInt()) != pawnHandle)
            return result;

        result.Instance = pawn;
        result.Handle = pawnHandle;
        return result;
    }

    // Collects every current managed bot pawn before any flags are modified
    static std::vector<BotPawnRef> CollectManagedBotPawns()
    {
        std::vector<BotPawnRef> pawns;
        pawns.reserve(PersonaPool::kMaxSlots);
        for (int slot = 0; slot < PersonaPool::kMaxSlots; ++slot)
        {
            BotPawnRef pawn = ResolveManagedBotPawn(slot);
            if (!pawn.Instance)
                continue;

            auto duplicate = std::find_if(
                pawns.begin(), pawns.end(),
                [&pawn](const BotPawnRef &existing)
                {
                    return existing.Instance == pawn.Instance;
                });
            if (duplicate == pawns.end())
                pawns.push_back(pawn);
        }
        return pawns;
    }

    // Clears FL_BOT for collected pawns and marks only those field writes changed
    std::vector<BotPawnRef> ApplyBotFlagOverride()
    {
        if (targets::kBaseEntity_FlagsOffset < 0)
            return {};

        std::vector<BotPawnRef> pawns = CollectManagedBotPawns();
        std::vector<BotPawnRef> modified;
        modified.reserve(pawns.size());
        for (const BotPawnRef &pawn : pawns)
        {
            auto *flags = reinterpret_cast<uint32_t *>(
                reinterpret_cast<unsigned char *>(pawn.Instance) +
                targets::kBaseEntity_FlagsOffset);
            if ((*flags & targets::kEntityFlagBot) == 0)
                continue;

            *flags &= ~targets::kEntityFlagBot;
            MarkEntityFieldChanged(pawn.Instance,
                                   static_cast<uint32_t>(targets::kBaseEntity_FlagsOffset));
            modified.push_back(pawn);
        }
        return modified;
    }

    // Restores only FL_BOT on still-current pawns without marking network changes
    void RestoreBotFlagOverride(const std::vector<BotPawnRef> &pawns)
    {
        for (const BotPawnRef &pawn : pawns)
        {
            int pawnIndex = static_cast<int>(pawn.Handle & 0x7FFF);
            void *currentPawn = ResolveEntityInstance(pawnIndex, nullptr, 0);
            if (currentPawn != pawn.Instance || IsEntityBeingDeleted(currentPawn))
                continue;

            auto *currentEntity = reinterpret_cast<CEntityInstance *>(currentPawn);
            if (static_cast<uint32_t>(currentEntity->GetRefEHandle().ToInt()) != pawn.Handle)
                continue;

            auto *flags = reinterpret_cast<uint32_t *>(
                reinterpret_cast<unsigned char *>(currentPawn) +
                targets::kBaseEntity_FlagsOffset);
            *flags |= targets::kEntityFlagBot;
        }
    }

    // Queue a kicked controller for identity-checked removal after the disconnect
    // callback has unwound. The engine may delete or reuse the slot during that
    // callback, so UTIL_Remove must never run against the raw pre-hook pointer.
    static bool QueueControllerRemovalForClient(void *pClient, int slot)
    {
        if (!pClient)
            return false;
        if (!g_pfnUtilRemove)
        {
            META_CONPRINTF("[BOTHIDER] deferred destroy unavailable: UTIL_Remove unresolved\n");
            return false;
        }
        int entIdx = *reinterpret_cast<int *>(
            reinterpret_cast<unsigned char *>(pClient) + ssc::OFFSET_m_nEntityIndex);
        char cls[64];
        void *inst = ResolveEntityInstance(entIdx, cls, sizeof(cls));
        if (!inst)
        {
            META_CONPRINTF("[BOTHIDER] deferred destroy skipped: entity resolve failed "
                           "entIdx=%d cls='%s' grs=%p (check kEntSys_* offsets)\n",
                           entIdx, cls, g_pGameResourceService);
            return false;
        }
        // Only ever destroy a player controller — never collateral entities
        if (std::strcmp(cls, "cs_player_controller") != 0)
        {
            META_CONPRINTF("[BOTHIDER] deferred destroy skipped entIdx=%d cls='%s' (not a controller)\n",
                           entIdx, cls);
            return false;
        }
        if (IsEntityBeingDeleted(inst))
            return false;

        const uint32_t handle = static_cast<uint32_t>(
            reinterpret_cast<CEntityInstance *>(inst)->GetRefEHandle().ToInt());
        auto duplicate = std::find_if(
            g_PendingControllerRemovals.begin(),
            g_PendingControllerRemovals.end(),
            [handle](const PendingControllerRemoval &pending)
            {
                return pending.Handle == handle;
            });
        if (duplicate != g_PendingControllerRemovals.end())
            return true;

        const uint16_t userId = *reinterpret_cast<uint16_t *>(
            reinterpret_cast<unsigned char *>(pClient) + ssc::OFFSET_m_UserID);
        g_PendingControllerRemovals.push_back({inst, handle, slot, userId, 0});
        META_CONPRINTF(
            "[BOTHIDER] deferred destroy queued slot=%d entIdx=%d handle=0x%08x\n",
            slot, entIdx, handle);
        return true;
    }

    static bool IsControllerReferencedByClient(void *controller, uint16_t *userIdOut)
    {
        for (int slot = 0; slot < PersonaPool::kMaxSlots; ++slot)
        {
            void *client = ResolveClientBySlot(slot);
            if (!client)
                continue;
            int entityIndex = *reinterpret_cast<int *>(
                reinterpret_cast<unsigned char *>(client) + ssc::OFFSET_m_nEntityIndex);
            char className[64];
            void *current = ResolveEntityInstance(entityIndex, className, sizeof(className));
            if (current == controller && std::strcmp(className, "cs_player_controller") == 0)
            {
                if (userIdOut)
                {
                    *userIdOut = *reinterpret_cast<uint16_t *>(
                        reinterpret_cast<unsigned char *>(client) + ssc::OFFSET_m_UserID);
                }
                return true;
            }
        }
        return false;
    }

    static void DrainPendingControllerRemovals()
    {
        if (!g_pfnUtilRemove)
        {
            g_PendingControllerRemovals.clear();
            return;
        }

        for (auto it = g_PendingControllerRemovals.begin();
             it != g_PendingControllerRemovals.end();)
        {
            const int entityIndex = static_cast<int>(it->Handle & 0x7FFF);
            char className[64];
            void *current = ResolveEntityInstance(entityIndex, className, sizeof(className));
            if (current != it->Controller ||
                std::strcmp(className, "cs_player_controller") != 0 ||
                IsEntityBeingDeleted(current) ||
                static_cast<uint32_t>(
                    reinterpret_cast<CEntityInstance *>(current)->GetRefEHandle().ToInt()) != it->Handle)
            {
                it = g_PendingControllerRemovals.erase(it);
                continue;
            }

            uint16_t currentUserId = 0;
            if (IsControllerReferencedByClient(current, &currentUserId))
            {
                if (currentUserId != it->UserId)
                {
                    META_CONPRINTF(
                        "[BOTHIDER] deferred destroy abandoned slot=%d handle=0x%08x: controller was rebound to userid=%u\n",
                        it->Slot, it->Handle, static_cast<unsigned int>(currentUserId));
                    it = g_PendingControllerRemovals.erase(it);
                    continue;
                }

                // Give normal disconnect teardown one complete GameFrame. If the
                // same userid still owns the exact same handle afterwards, this
                // is the orphaned-controller case the workaround targets.
                if (it->ReferencedFrames++ == 0)
                {
                    ++it;
                    continue;
                }
            }

            // One-time cross-check: prove our GameResourceService+0x58 chain resolves.
            static bool s_crossChecked = false;
            if (!s_crossChecked && g_ppEntSysGlobal)
            {
                void *entSysFromChain = nullptr;
                SehReadPtr(reinterpret_cast<unsigned char *>(g_pGameResourceService) +
                               targets::kEntSys_OffsetInGameResSvc,
                           &entSysFromChain);
                void *entSysFromRemove = nullptr;
                SehReadPtr(g_ppEntSysGlobal, &entSysFromRemove);
                META_CONPRINTF("[BOTHIDER] entSys cross-check: chain=%p remove=%p %s\n",
                               entSysFromChain, entSysFromRemove,
                               (entSysFromChain == entSysFromRemove) ? "MATCH" : "MISMATCH");
                s_crossChecked = true;
            }

            g_pfnUtilRemove(current);
            META_CONPRINTF(
                "[BOTHIDER] deferred destroy dispatched slot=%d entIdx=%d handle=0x%08x\n",
                it->Slot, entityIndex, it->Handle);
            it = g_PendingControllerRemovals.erase(it);
        }
    }

    // Keep the controller identity in sync with CServerSideClient
    // The quota detour temporarily restores this bit while counting bots
    static bool SetControllerFakeClientFlag(int slot, bool fakeClient)
    {
        void *pClient = ResolveClientBySlot(slot);
        if (!pClient)
            return false;

        int entIdx = *reinterpret_cast<int *>(
            reinterpret_cast<unsigned char *>(pClient) + ssc::OFFSET_m_nEntityIndex);
        char cls[64];
        void *ctrl = ResolveEntityInstance(entIdx, cls, sizeof(cls));
        if (!ctrl || std::strcmp(cls, "cs_player_controller") != 0)
            return false;

        constexpr uint32_t kBit = 0x100;
        auto *flags = reinterpret_cast<uint32_t *>(
            reinterpret_cast<unsigned char *>(ctrl) +
            targets::kController_FakeClientFlagsOffset);
        const uint32_t before = *flags;
        if (fakeClient)
            *flags |= kBit;
        else
            *flags &= ~kBit;
        if (*flags != before)
            MarkEntityFieldChanged(
                ctrl, static_cast<uint32_t>(targets::kController_FakeClientFlagsOffset));
        return true;
    }

    // Flip managed bots' controller fakeclient bit around the quota pass.
    int FlipManagedController904(
        bool restore,
        std::array<ManagedControllerFlagSnapshot, 64> *saved)
    {
        if (!saved)
            return 0;
        const int kCtrlOff = targets::kController_FakeClientFlagsOffset;
        constexpr uint32_t kBit = 0x100;
        int touched = 0;
        for (int idx = 0; idx < PersonaPool::kMaxSlots; ++idx)
        {
            if (!restore)
                (*saved)[idx] = ManagedControllerFlagSnapshot{};
            if (!restore && !Manager().IsManaged(idx))
                continue;
            ManagedControllerFlagSnapshot &snapshot = (*saved)[idx];
            if (restore && !snapshot.Modified)
                continue;

            void *pClient = ResolveClientBySlot(idx);
            if (!pClient)
                continue;
            int entIdx = *reinterpret_cast<int *>(
                reinterpret_cast<unsigned char *>(pClient) + ssc::OFFSET_m_nEntityIndex);
            char cls[64];
            void *ctrl = ResolveEntityInstance(entIdx, cls, sizeof(cls));
            if (!ctrl || std::strcmp(cls, "cs_player_controller") != 0 ||
                IsEntityBeingDeleted(ctrl))
                continue;

            auto *entity = reinterpret_cast<CEntityInstance *>(ctrl);
            const uint32_t handle = static_cast<uint32_t>(entity->GetRefEHandle().ToInt());

            auto *p = reinterpret_cast<uint32_t *>(
                reinterpret_cast<unsigned char *>(ctrl) + kCtrlOff);
            if (!restore)
            {
                if ((*p & kBit) == 0) // only flip slots that read as human
                {
                    snapshot.Client = pClient;
                    snapshot.Controller = ctrl;
                    snapshot.Handle = handle;
                    snapshot.UserId = *reinterpret_cast<uint16_t *>(
                        reinterpret_cast<unsigned char *>(pClient) + ssc::OFFSET_m_UserID);
                    snapshot.Modified = true;
                    *p |= kBit;
                    ++touched;
                }
            }
            else
            {
                const uint16_t userId = *reinterpret_cast<uint16_t *>(
                    reinterpret_cast<unsigned char *>(pClient) + ssc::OFFSET_m_UserID);
                if (pClient != snapshot.Client || ctrl != snapshot.Controller ||
                    handle != snapshot.Handle || userId != snapshot.UserId)
                {
                    snapshot = ManagedControllerFlagSnapshot{};
                    continue;
                }
                *p &= ~kBit;
                snapshot = ManagedControllerFlagSnapshot{};
                ++touched;
            }
        }
        return touched;
    }

    // EXPERIMENT: !restore clears +904 0x100 on managed bots reading as bot and records them
    int ClearManagedController904(bool restore, std::array<bool, 64> *saved)
    {
        if (!saved)
            return 0;
        const int kCtrlOff = targets::kController_FakeClientFlagsOffset;
        constexpr uint32_t kBit = 0x100;
        int touched = 0;
        for (int idx = 0; idx < PersonaPool::kMaxSlots; ++idx)
        {
            if (!restore)
                (*saved)[idx] = false;
            if (!restore && !Manager().IsManaged(idx))
                continue;
            if (restore && !(*saved)[idx])
                continue;

            void *pClient = ResolveClientBySlot(idx);
            if (!pClient)
                continue;
            int entIdx = *reinterpret_cast<int *>(
                reinterpret_cast<unsigned char *>(pClient) + ssc::OFFSET_m_nEntityIndex);
            char cls[64];
            void *ctrl = ResolveEntityInstance(entIdx, cls, sizeof(cls));
            if (!ctrl || std::strcmp(cls, "cs_player_controller") != 0)
                continue;

            auto *p = reinterpret_cast<uint32_t *>(
                reinterpret_cast<unsigned char *>(ctrl) + kCtrlOff);
            if (!restore)
            {
                if ((*p & kBit) != 0) // only clear slots currently reading as bot
                {
                    *p &= ~kBit;
                    (*saved)[idx] = true;
                    ++touched;
                }
            }
            else
            {
                *p |= kBit;
                (*saved)[idx] = false;
                ++touched;
            }
        }
        return touched;
    }

    // Reset a disguised bot's idle timer
    static void ResetIdleTimerForClient(void *pClient)
    {
        if (!pClient)
            return;
        // controller offsets
        int pawnOff = schema::GetFieldOffset("CBasePlayerController", "m_hPawn");
        int idleOff = schema::GetFieldOffset("CCSPlayerPawnBase", "m_flIdleTimeSinceLastAction");
        if (pawnOff < 0 || idleOff < 0)
            return;

        int entIdx = *reinterpret_cast<int *>(
            reinterpret_cast<unsigned char *>(pClient) + ssc::OFFSET_m_nEntityIndex);
        char cls[64];
        void *controller = ResolveEntityInstance(entIdx, cls, sizeof(cls));
        if (!controller || std::strcmp(cls, "cs_player_controller") != 0)
            return;

        // m_hPawn is a CHandle; low 15 bits are the pawn entity index
        uint32_t hPawn = *reinterpret_cast<uint32_t *>(
            reinterpret_cast<unsigned char *>(controller) + pawnOff);
        if (hPawn == 0xFFFFFFFF)
            return;
        int pawnIdx = static_cast<int>(hPawn & 0x7FFF);
        void *pawn = ResolveEntityInstance(pawnIdx, nullptr, 0);
        if (!pawn)
            return;

        *reinterpret_cast<float *>(
            reinterpret_cast<unsigned char *>(pawn) + idleOff) = 0.0f;
    }

    // Update the engine-side client name
    static const char *SetEngineName(HiderPlugin *plugin, void *pClient, const char *newName)
    {
        if (!pClient || !newName || !newName[0])
            return nullptr;
#if defined(_WIN32)
        (void)plugin;
        if (targets::kVTSlot_ClientSetName < 0)
            return nullptr;
        __try
        {
            auto **vtable = *reinterpret_cast<void ***>(pClient);
            if (!vtable)
                return nullptr;
            using SetNameFn = void(__fastcall *)(void *, const char *);
            auto setName = reinterpret_cast<SetNameFn>(
                vtable[targets::kVTSlot_ClientSetName]);
            if (!setName)
                return nullptr;
            setName(pClient, newName);
            return ssc::ReadName(pClient);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return nullptr;
        }
#else
        if (!plugin->m_pUtlStringSet)
            return nullptr;
        void *pUtlString =
            reinterpret_cast<unsigned char *>(pClient) + ssc::OFFSET_m_Name;
        plugin->m_pUtlStringSet(pUtlString, newName);
        return *reinterpret_cast<const char **>(pUtlString);
#endif
    }

    static bool EngineNameMatches(const char *storedName, const char *expectedName)
    {
        if (!storedName || !expectedName)
            return false;
#if defined(_WIN32)
        __try
        {
            return std::strcmp(storedName, expectedName) == 0;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
#else
        return std::strcmp(storedName, expectedName) == 0;
#endif
    }

    // Identifies SourceTV before the server-side HLTV flag is initialized
    static bool IsHltvConnection(const char *name, const char *networkId)
    {
        return (name && std::strcmp(name, "SourceTV") == 0) ||
               (networkId && std::strcmp(networkId, "HLTV") == 0);
    }

    // Releases an HLTV slot that was temporarily misclassified as a managed bot
    static bool ReleaseManagedHltvSlot(HiderPlugin *plugin, int slot, void *pClient)
    {
        if (!plugin || slot < 0 || slot >= PersonaPool::kMaxSlots || !pClient ||
            !Manager().IsManaged(slot) || !ssc::IsHltv(pClient))
            return false;

        ssc::WriteSteamId(pClient, 0);
        if (!g_OriginalSlotName[slot].empty())
            SetEngineName(plugin, pClient, g_OriginalSlotName[slot].c_str());
        RefreshClientUserInfo(slot);

        BotInfo().ReleaseAssignment(g_SlotEntry[slot]);
        g_SlotEntry[slot] = nullptr;
        g_OriginalSlotName[slot].clear();
        Manager().ReleaseSlot(slot);

        META_CONPRINTF("[BOTHIDER] slot=%d rejected: SourceTV/HLTV client\n", slot);
        return true;
    }

    // Hook bodies

    // Windows binds from the authoritative OnClientConnected slot and avoids
    // the unstable IVEngineServer::CreateFakeClient return hook. Linux keeps
    // the upstream CreateFakeClient context path below
    void HiderPlugin::Hook_OnClientConnected_Post(CPlayerSlot slot, const char *pszName, uint64 /*xuid*/,
                                                  const char *pszNetworkID, const char * /*pszAddress*/,
                                                  bool bFakePlayer)
    {
        if (m_bSelfDisabled || !bFakePlayer || IsHltvConnection(pszName, pszNetworkID))
            RETURN_META(MRES_IGNORED);
#if defined(_WIN32)
        int idx = slot.Get();
        if (idx < 0 || idx >= PersonaPool::kMaxSlots)
            RETURN_META(MRES_IGNORED);
        if (Manager().IsManaged(idx))
            RETURN_META(MRES_IGNORED);

        void *pClient = ResolveClientBySlot(idx);
        if (!pClient || ssc::IsHltv(pClient))
            RETURN_META(MRES_IGNORED);

        const BotEntry *entry = BotInfo().PickForBot(pszName);
        std::string displayName;
        if (m_bUseBotInfoName && entry)
            displayName = entry->Name;
        else if (pszName && pszName[0])
            displayName = pszName;
        else if (entry)
            displayName = entry->Name;
        else
            displayName = Personas().PickFromRoster();

        if (displayName.empty())
        {
            BotInfo().ReleaseAssignment(entry);
            RETURN_META(MRES_IGNORED);
        }

        const uint64_t configuredSid =
            (entry && entry->SteamId64 != 0) ? entry->SteamId64 : 0;
        const char *crosshairCode = entry ? entry->CrosshairCode.c_str() : nullptr;
        const uint32_t scoreboardFlair = entry ? entry->ScoreboardFlair : 0;
        if (!Manager().AdoptSlot(
                idx, displayName.c_str(), configuredSid,
                crosshairCode, scoreboardFlair))
        {
            BotInfo().ReleaseAssignment(entry);
            RETURN_META(MRES_IGNORED);
        }
        g_SlotEntry[idx] = entry;
        g_OriginalSlotName[idx] = (pszName && pszName[0]) ? pszName : "";

        if (m_bDisguiseEnabled)
        {
            ssc::ClearFakePlayer(pClient);
            SetControllerFakeClientFlag(idx, false);
        }

        uint64_t sid = 0;
        if (configuredSid != 0)
        {
            sid = MakeUniqueSteamId(idx, configuredSid);
            ssc::WriteSteamId(pClient, sid);
            Manager().SetSyntheticSid(idx, sid);
            Publisher().UpdateSyntheticSid(idx, sid);
        }

        META_CONPRINTF("[BOTHIDER] slot=%d adopted name='%s' steamid64=%llu\n",
                       idx, displayName.c_str(),
                       static_cast<unsigned long long>(sid));
        RETURN_META(MRES_IGNORED);
#else
        if (g_FakeClientCallStack.empty())
            RETURN_META(MRES_IGNORED);

        int idx = slot.Get();
        if (idx < 0 || idx >= PersonaPool::kMaxSlots)
            RETURN_META(MRES_IGNORED);

        auto &context = g_FakeClientCallStack.back();
        if (!context.Enabled || context.ConnectedObserved)
            RETURN_META(MRES_IGNORED);

        context.ConnectedObserved = true;
        context.ConnectedSlot = idx;
        context.ConnectedName = (pszName && pszName[0]) ? pszName : "";
        META_CONPRINTF("[BOTHIDER] CreateFakeClient observed slot=%d name='%s'\n",
                       idx, context.ConnectedName.empty() ? "<empty>" : context.ConnectedName.c_str());
        RETURN_META(MRES_IGNORED);
#endif
    }

    void HiderPlugin::Hook_ClientPutInServer_Post(CPlayerSlot slot, char const *pszName,
                                                  int type, uint64 /*xuid*/)
    {
        if (m_bSelfDisabled)
            RETURN_META(MRES_IGNORED);
#if defined(_WIN32)
        // OnClientConnected already classified the client before disguise
        // changed its fake-client fields
        (void)type;
#else
        if (type != 1)
            RETURN_META(MRES_IGNORED);
#endif
        int idx = slot.Get();
        if (idx < 0 || idx >= PersonaPool::kMaxSlots)
            RETURN_META(MRES_IGNORED);
        if (!Personas().IsSlotManaged(idx))
            RETURN_META(MRES_IGNORED);

        void *pClient = ResolveClientBySlot(idx);
        if (!pClient)
            RETURN_META(MRES_IGNORED);
        if (ReleaseManagedHltvSlot(this, idx, pClient))
            RETURN_META(MRES_IGNORED);

        if (m_bDisguiseEnabled)
        {
            ssc::ClearFakePlayer(pClient);
            SetControllerFakeClientFlag(idx, false);
        }

        auto *entry = g_SlotEntry[idx];
        if (entry && entry->SteamId64 != 0)
        {
            uint64_t sid = MakeUniqueSteamId(idx, entry->SteamId64);
            ssc::WriteSteamId(pClient, sid);
            Manager().SetSyntheticSid(idx, sid);
            Publisher().UpdateSyntheticSid(idx, sid);
        }

        std::string visibleName = Personas().GetSlotName(idx);
        if (visibleName.empty() && pszName)
            visibleName = pszName;
        if (!visibleName.empty())
        {
            SetEngineName(this, pClient, visibleName.c_str());
#if !defined(_WIN32)
            RefreshClientUserInfo(idx);
#endif
        }
        else
        {
            RefreshClientUserInfo(idx);
        }

        META_CONPRINTF("[BOTHIDER] CPiS safety-net slot=%d name='%s'\n", idx, pszName ? pszName : "<null>");
        RETURN_META(MRES_IGNORED);
    }

    // Checks whether the engine may leave a controller after disconnect
    static bool IsTargetedClientRemovalReason(ENetworkDisconnectionReason reason)
    {
        switch (reason)
        {
        case NETWORK_DISCONNECT_KICKED:
        case NETWORK_DISCONNECT_BANADDED:
        case NETWORK_DISCONNECT_KICKBANADDED:
            return true;
        default:
            break;
        }

        const int value = static_cast<int>(reason);
        return value >= static_cast<int>(NETWORK_DISCONNECT_KICKED_TEAMKILLING) &&
               value <= static_cast<int>(NETWORK_DISCONNECT_KICKED_INSECURECLIENT);
    }

    // Clean teardown on disconnect
    // Restore the bot identity
    void HiderPlugin::Hook_ClientDisconnect_Pre(CPlayerSlot slot, ENetworkDisconnectionReason reason,
                                                const char * /*pszName*/, uint64 /*xuid*/,
                                                const char * /*pszNetworkID*/)
    {
        if (m_bSelfDisabled)
            RETURN_META(MRES_IGNORED);
        int idx = slot.Get();
        if (idx < 0 || idx >= PersonaPool::kMaxSlots)
            RETURN_META(MRES_IGNORED);
        if (!Personas().IsSlotManaged(idx))
            RETURN_META(MRES_IGNORED);

        // Capture the persona name
        std::string persona = Personas().GetSlotName(idx);

        // Restore engine-side bot identity
        void *pClient = ResolveClientBySlot(idx);
        if (pClient)
        {
            ssc::SetFakePlayer(pClient);
            SetControllerFakeClientFlag(idx, true);
            ssc::WriteSteamId(pClient, 0);

            // Targeted removals can leave a controller behind
            // Map and server teardown already own controller destruction
            if (IsTargetedClientRemovalReason(reason))
                QueueControllerRemovalForClient(pClient, idx);
        }

        // Free the bot_info assignment bound to this slot
        BotInfo().ReleaseAssignment(g_SlotEntry[idx]);
        g_SlotEntry[idx] = nullptr;
        g_OriginalSlotName[idx].clear();

        // Drain manager + personas + shared memory for this slot
        Manager().ReleaseSlot(idx);

        META_CONPRINTF("[BOTHIDER] ClientDisconnect slot=%d name='%s' — slot released\n",
                       idx, persona.empty() ? "<null>" : persona.c_str());
        RETURN_META(MRES_IGNORED);
    }

    // True for console commands that disconnect a client
    static bool IsKickCommand(const char *name)
    {
        if (!name || !name[0])
            return false;
        return !std::strcmp(name, "kickid") ||
               !std::strcmp(name, "kick") ||
               !std::strcmp(name, "bot_kick") ||
               !std::strcmp(name, "banid");
    }

    // True for commands that force bots/humans onto teams
    static bool IsHumanTeamCommand(const char *name)
    {
        if (!name || !name[0])
            return false;
        return !std::strcmp(name, "mp_humanteam") ||
               !std::strcmp(name, "bot_join_team");
    }

    // True for bot_add variants — their quota census must see disguised bots as bots
    static bool IsBotAddCommand(const char *name)
    {
        if (!name || !name[0])
            return false;
        return !std::strcmp(name, "bot_add") ||
               !std::strcmp(name, "bot_add_t") ||
               !std::strcmp(name, "bot_add_ct");
    }

    // Returns true when the bot_kick target is a built-in group selector.
    static bool IsBotKickGroupTarget(const char *target)
    {
        if (!target || !target[0])
            return false;
        return !std::strcmp(target, "all") ||
               !std::strcmp(target, "t") ||
               !std::strcmp(target, "ct");
    }

    // Finds a managed bot slot by its current persona name.
    static int FindManagedSlotByPersonaName(const char *name)
    {
        if (!name || !name[0])
            return -1;
        for (int idx = 0; idx < PersonaPool::kMaxSlots; ++idx)
        {
            if (!Manager().IsManaged(idx))
                continue;
            std::string persona = Personas().GetSlotName(idx);
            if (persona == name)
                return idx;
        }
        return -1;
    }

    static int CaseCmp(const char *a, const char *b)
    {
#if defined(_WIN32)
        return _stricmp(a, b);
#else
        return strcasecmp(a, b);
#endif
    }

    // True if the value forces a specific team
    static bool IsTeamForceValue(const char *v)
    {
        if (!v || !v[0])
            return false;
        return std::strcmp(v, "0") != 0 && CaseCmp(v, "any") != 0;
    }

    // Poll mp_humanteam/bot_join_team values directly
    static bool IsTeamForceActive()
    {
        static const char *kNames[] = {"mp_humanteam", "bot_join_team"};
        for (const char *name : kNames)
        {
            ConVarRefAbstract ref(name);
            if (!ref.IsValidRef())
                continue;
            CUtlString val = ref.GetString();
            if (IsTeamForceValue(val.Get()))
                return true;
        }
        return false;
    }

    // Used when map_whitelist.json is absent/invalid
    static void LoadDefaultDisguiseWhitelist()
    {
        g_DisguiseWhitelist = {
            "ar_baggage",
            "ar_pool_day",
            "ar_shoots",
            "ar_shoots_night",
            "cs_alpine",
            "cs_italy",
            "cs_office",
            "de_ancient",
            "de_ancient_night",
            "de_anubis",
            "de_cache",
            "de_dust2",
            "de_inferno",
            "de_mirage",
            "de_nuke",
            "de_overpass",
            "de_poseidon",
            "de_sanctum",
            "de_stronghold",
            "de_train",
            "de_vertigo",
            "de_warden",
        };
    }

    // Load the map whitelist from a JSON
    static void LoadDisguiseWhitelist(const char *path)
    {
        g_DisguiseWhitelist.clear();
        std::ifstream ifs(path);
        if (ifs.is_open())
        {
            try
            {
                nlohmann::json root = nlohmann::json::parse(ifs);
                if (root.is_array())
                {
                    for (const auto &e : root)
                        if (e.is_string())
                            g_DisguiseWhitelist.push_back(e.get<std::string>());
                }
            }
            catch (...)
            {
                g_DisguiseWhitelist.clear();
            }
        }
        if (g_DisguiseWhitelist.empty())
            LoadDefaultDisguiseWhitelist();
    }

    // Official maps
    // Bots should stay disguised
    static bool IsDisguiseWhitelistMap(const char *mapName)
    {
        if (!mapName || !mapName[0])
            return false;
        // Strip any workshop/path prefix → bare map name
        const char *slash = std::strrchr(mapName, '/');
        const char *base = slash ? slash + 1 : mapName;
        for (const auto &m : g_DisguiseWhitelist)
            if (m == base)
                return true;
        return false;
    }

    // PRE ICvar::DispatchConCommand — restore fake-player identity on all managed slots
    void HiderPlugin::Hook_DispatchConCommand_Pre(ConCommandRef cmd, const CCommandContext &,
                                                  const CCommand &args)
    {
        if (m_bSelfDisabled)
            RETURN_META(MRES_IGNORED);
        if (!cmd.IsValidRef())
            RETURN_META(MRES_IGNORED);
        const char *cmdName = cmd.GetName();

        // Capture the current quota so POST can set it to old+1
        if (IsBotAddCommand(cmdName))
        {
            m_QuotaBeforeAdd = 0;
            ConVarRefAbstract botQuota("bot_quota");
            if (botQuota.IsValidRef())
                m_QuotaBeforeAdd = botQuota.GetInt();
            RETURN_META(MRES_IGNORED);
        }

#if !defined(_WIN32)
        if (IsHumanTeamCommand(cmdName))
        {
            if (m_bDisguiseEnabled)
            {
                META_CONPRINTF("[BOTHIDER] '%s' detected — disabling disguise\n",
                               cmdName);
                SetDisguiseEnabled(false);
            }
            RETURN_META(MRES_IGNORED);
        }
#endif

        if (!std::strcmp(cmdName, "bot_kick"))
        {
            const char *target = (args.ArgC() >= 2) ? args.Arg(1) : "";
            if (target[0] && !IsBotKickGroupTarget(target))
            {
                int slot = FindManagedSlotByPersonaName(target);
                if (slot >= 0 && engine)
                {
                    char kickCmd[640];
                    std::snprintf(kickCmd, sizeof(kickCmd), "kick \"%s\"\n", target);
                    engine->ServerCommand(kickCmd);
                    META_CONPRINTF("[BOTHIDER] bot_kick '%s' redirected to kick for managed slot=%d\n",
                                   target, slot);
                    RETURN_META(MRES_SUPERCEDE);
                }
            }
        }

        if (!IsKickCommand(cmdName))
            RETURN_META(MRES_IGNORED);

        // Disguise-toggle rebuild in progress: skip
        if (m_bRebuilding)
            RETURN_META(MRES_IGNORED);

        m_ManagedBeforeKick = 0;
        m_QuotaBeforeKick = -1;
        m_AdjustQuotaAfterKick = std::strcmp(cmdName, "bot_kick") != 0;
        if (m_AdjustQuotaAfterKick)
        {
            for (int idx = 0; idx < PersonaPool::kMaxSlots; ++idx)
            {
                if (Manager().IsManaged(idx))
                    ++m_ManagedBeforeKick;
            }

            ConVarRefAbstract botQuota("bot_quota");
            if (botQuota.IsValidRef())
                m_QuotaBeforeKick = botQuota.GetInt();
        }

        int restored = 0;
        for (int idx = 0; idx < PersonaPool::kMaxSlots; ++idx)
        {
            if (!Personas().IsSlotManaged(idx))
                continue;
            void *pClient = ResolveClientBySlot(idx);
            if (!pClient)
                continue;
            ssc::SetFakePlayer(pClient);
            SetControllerFakeClientFlag(idx, true);
            ssc::WriteSteamId(pClient, 0);
            ++restored;
        }
        META_CONPRINTF("[BOTHIDER] kick PRE '%s' restored=%d\n", cmdName, restored);
        RETURN_META(MRES_IGNORED);
    } // end Hook_DispatchConCommand_Pre

    // POST ICvar::DispatchConCommand — the kick has run and released its target slot(s) via ClientDisconnect
    // Re-disguise every slot still managed so the surviving bots keep their forged identity
    void HiderPlugin::Hook_DispatchConCommand_Post(ConCommandRef cmd, const CCommandContext &,
                                                   const CCommand & /*args*/)
    {
        if (m_bSelfDisabled)
            RETURN_META(MRES_IGNORED);
        if (!cmd.IsValidRef())
            RETURN_META(MRES_IGNORED);
        const char *cmdName = cmd.GetName();

        if (IsBotAddCommand(cmdName))
        {
            if (m_bDisguiseEnabled && !m_bRebuilding)
            {
                ConVarRefAbstract botQuota("bot_quota");
                if (botQuota.IsValidRef())
                {
                    int want = m_QuotaBeforeAdd + 1;
                    if (botQuota.GetInt() != want)
                        botQuota.SetInt(want);
                }
            }
            RETURN_META(MRES_IGNORED);
        }

        if (!IsKickCommand(cmdName))
            RETURN_META(MRES_IGNORED);

        // Disguise-toggle rebuild: skip re-disguise + quota write, clear the flag
        if (m_bRebuilding)
        {
            m_bRebuilding = false;
            META_CONPRINTF("[BOTHIDER] disguise-off kick done\n");
            RETURN_META(MRES_IGNORED);
        }

        int redisguised = 0;
        int managedAfterKick = 0;
        for (int idx = 0; idx < PersonaPool::kMaxSlots; ++idx)
        {
            if (!Manager().IsManaged(idx))
                continue;
            ++managedAfterKick;
            void *pClient = ResolveClientBySlot(idx);
            if (!pClient)
                continue;
            if (m_bDisguiseEnabled)
            {
                ssc::ClearFakePlayer(pClient);
                SetControllerFakeClientFlag(idx, false);
            }
            uint64_t sid = Manager().GetSyntheticSid(idx);
            if (sid != 0)
                ssc::WriteSteamId(pClient, sid);
            RefreshClientUserInfo(idx);
            ++redisguised;
        }

        if (m_AdjustQuotaAfterKick && m_QuotaBeforeKick >= 0)
        {
            int removedManaged = m_ManagedBeforeKick - managedAfterKick;
            if (removedManaged > 0)
            {
                ConVarRefAbstract botQuota("bot_quota");
                if (botQuota.IsValidRef())
                {
                    int want = m_QuotaBeforeKick - removedManaged;
                    if (want < 0)
                        want = 0;
                    if (botQuota.GetInt() != want)
                        botQuota.SetInt(want);
                }
            }
        }

        m_ManagedBeforeKick = 0;
        m_QuotaBeforeKick = -1;
        m_AdjustQuotaAfterKick = false;

        META_CONPRINTF("[BOTHIDER] kick POST '%s' redisguised=%d quota=%d\n",
                       cmd.GetName(), redisguised, managedAfterKick);
        RETURN_META(MRES_IGNORED);
    } // end Hook_DispatchConCommand_Post

    // Toggle disguise: off restores m_bFakePlayer=1 so the bot manager spawns bots again
    void HiderPlugin::SetDisguiseEnabled(bool enabled)
    {
        if (m_bDisguiseEnabled == enabled)
            return;
        m_bDisguiseEnabled = enabled;

        // Count managed bots
        int managed = 0;
        for (int idx = 0; idx < PersonaPool::kMaxSlots; ++idx)
            if (Manager().IsManaged(idx))
                ++managed;

        // Rebuild
        if (engine && managed > 0)
        {
            for (int idx = 0; idx < PersonaPool::kMaxSlots; ++idx)
            {
                if (!Manager().IsManaged(idx))
                    continue;
                void *pClient = ResolveClientBySlot(idx);
                if (pClient)
                {
                    ssc::SetFakePlayer(pClient);
                    SetControllerFakeClientFlag(idx, true);
                }
            }
            m_bRebuilding = true;
            // fill-mode quota = humans + bot
            int quota = CountHumanClients() + managed;
            char quotaCmd[48];
            std::snprintf(quotaCmd, sizeof(quotaCmd), "bot_quota %d\n", quota);
            engine->ServerCommand("bot_kick\n");
            engine->ServerCommand(quotaCmd);
            META_CONPRINTF("[BOTHIDER] disguise %s — rebuilding %d bot(s), quota=%d\n",
                           enabled ? "ON" : "OFF", managed, quota);
            return;
        }

        // Fallback
        for (int idx = 0; idx < PersonaPool::kMaxSlots; ++idx)
        {
            if (!Manager().IsManaged(idx))
                continue;
            void *pClient = ResolveClientBySlot(idx);
            if (!pClient)
                continue;
            if (enabled)
            {
                ssc::ClearFakePlayer(pClient);
                SetControllerFakeClientFlag(idx, false);
                uint64_t sid = Manager().GetSyntheticSid(idx);
                if (sid != 0)
                    ssc::WriteSteamId(pClient, sid);
            }
            else
            {
                ssc::SetFakePlayer(pClient);
                SetControllerFakeClientFlag(idx, true);
                ssc::WriteSteamId(pClient, 0);
            }
            RefreshClientUserInfo(idx);
        }
        META_CONPRINTF("[BOTHIDER] disguise %s (no rebuild)\n", enabled ? "ON" : "OFF");
    }

    // Clean-rebuild on rematch
    void HiderPlugin::RebuildBots()
    {
        if (m_bSelfDisabled || !m_bDisguiseEnabled || !engine || m_bRebuilding)
            return;

        // Restore m_bFakePlayer
        int managed = 0;
        for (int idx = 0; idx < PersonaPool::kMaxSlots; ++idx)
        {
            if (!Manager().IsManaged(idx))
                continue;
            void *pClient = ResolveClientBySlot(idx);
            if (pClient)
            {
                ssc::SetFakePlayer(pClient);
                SetControllerFakeClientFlag(idx, true);
            }
            ++managed;
        }
        if (managed == 0)
            return;

        // Re-fill
        int quota = managed;
        ConVarRefAbstract botQuota("bot_quota");
        if (botQuota.IsValidRef())
            quota = botQuota.GetInt();

        m_bRebuilding = true;
        char quotaCmd[48];
        std::snprintf(quotaCmd, sizeof(quotaCmd), "bot_quota %d\n", quota);
        // Drop quota to 0 before kicking: otherwise the engine keeps bots alive to
        // satisfy the live quota mid-kick, and survivors skip CreateFakeClient
        engine->ServerCommand("bot_quota 0\n");
        engine->ServerCommand("bot_kick all\n");
        engine->ServerCommand(quotaCmd);
        META_CONPRINTF("[BOTHIDER] rematch rebuild — kicked %d bot(s), bot_quota->%d\n",
                       managed, quota);
    }

#if !defined(_WIN32)
    // Preserve the upstream Linux CreateFakeClient binding path
    PlayerSlotHookResult HiderPlugin::Hook_CreateFakeClient_Pre(const char *netname)
    {
        FakeClientCallContext context;
        context.RequestedName = (netname && netname[0]) ? netname : "";
        context.UseBotInfoName = m_bUseBotInfoName;
        context.Enabled = !m_bSelfDisabled;

        if (context.Enabled)
        {
            context.Entry = BotInfo().PickForBot(netname);
            if (context.UseBotInfoName && context.Entry)
                context.Persona = context.Entry->Name;
            else if (netname && netname[0])
                context.Persona = netname;
            else if (context.Entry)
                context.Persona = context.Entry->Name;
            else
                context.Persona = Personas().PickFromRoster();
        }

        g_FakeClientCallStack.push_back(std::move(context));
        META_CONPRINTF("[BOTHIDER] CreateFakeClient begin depth=%zu requested='%s'\n",
                       g_FakeClientCallStack.size(),
                       netname && netname[0] ? netname : "<empty>");
        RETURN_META_VALUE(MRES_IGNORED, PlayerSlotHookResult(-1));
    }

    PlayerSlotHookResult HiderPlugin::Hook_CreateFakeClient_Post(const char * /*netname*/)
    {
        if (g_FakeClientCallStack.empty())
        {
            META_CONPRINTF("[BOTHIDER] CreateFakeClient end without matching begin\n");
            RETURN_META_VALUE(MRES_IGNORED, PlayerSlotHookResult(-1));
        }

        const int returnedSlot = (META_RESULT_ORIG_RET(PlayerSlotHookResult)).Get();
        FakeClientCallContext context = std::move(g_FakeClientCallStack.back());
        g_FakeClientCallStack.pop_back();

        if (!context.Enabled)
            RETURN_META_VALUE(MRES_IGNORED, PlayerSlotHookResult(-1));

        if (returnedSlot < 0 || returnedSlot >= PersonaPool::kMaxSlots)
        {
            BotInfo().ReleaseAssignment(context.Entry);
            META_CONPRINTF("[BOTHIDER] CreateFakeClient failed returned=%d requested='%s'\n",
                           returnedSlot,
                           context.RequestedName.empty() ? "<empty>" : context.RequestedName.c_str());
            RETURN_META_VALUE(MRES_IGNORED, PlayerSlotHookResult(-1));
        }

        if (context.ConnectedObserved && context.ConnectedSlot != returnedSlot)
        {
            BotInfo().ReleaseAssignment(context.Entry);
            META_CONPRINTF("[BOTHIDER] CreateFakeClient slot mismatch returned=%d connected=%d\n",
                           returnedSlot, context.ConnectedSlot);
            RETURN_META_VALUE(MRES_IGNORED, PlayerSlotHookResult(-1));
        }

        void *pClient = ResolveClientBySlot(returnedSlot);
        if (!pClient)
        {
            BotInfo().ReleaseAssignment(context.Entry);
            META_CONPRINTF("[BOTHIDER] CreateFakeClient bind failed: client slot=%d unavailable after return\n",
                           returnedSlot);
            RETURN_META_VALUE(MRES_IGNORED, PlayerSlotHookResult(-1));
        }

        std::string engineName = context.ConnectedName.empty()
                                     ? context.RequestedName
                                     : context.ConnectedName;
        std::string displayName;
        if (context.UseBotInfoName && !context.Persona.empty())
            displayName = context.Persona;
        else if (!engineName.empty())
            displayName = engineName;
        else
            displayName = context.Persona;

        const char *boundName = engineName.empty() ? displayName.c_str() : engineName.c_str();
        bool nameChanged = false;
        if (!displayName.empty() && displayName != engineName)
        {
            const char *storedName = SetEngineName(this, pClient, displayName.c_str());
            if (storedName && storedName[0])
            {
                boundName = storedName;
                nameChanged = true;
            }
            else
            {
                META_CONPRINTF("[BOTHIDER] CreateFakeClient bind warning: name overwrite failed slot=%d\n",
                               returnedSlot);
            }
        }

        const BotEntry *cfg = context.Entry;
        uint64_t cfgSid = (cfg && cfg->SteamId64 != 0) ? cfg->SteamId64 : 0;
        const char *cfgCross = cfg ? cfg->CrosshairCode.c_str() : nullptr;
        uint32_t cfgFlair = cfg ? cfg->ScoreboardFlair : 0;
        if (!Manager().AdoptSlot(returnedSlot, boundName, cfgSid, cfgCross, cfgFlair))
        {
            if (nameChanged && !engineName.empty())
            {
                SetEngineName(this, pClient, engineName.c_str());
                RefreshClientUserInfo(returnedSlot);
            }
            BotInfo().ReleaseAssignment(context.Entry);
            META_CONPRINTF("[BOTHIDER] CreateFakeClient bind failed: manager rejected slot=%d\n",
                           returnedSlot);
            RETURN_META_VALUE(MRES_IGNORED, PlayerSlotHookResult(-1));
        }

        g_SlotEntry[returnedSlot] = cfg;
        g_OriginalSlotName[returnedSlot] = engineName;
        if (m_bDisguiseEnabled)
        {
            ssc::ClearFakePlayer(pClient);
            SetControllerFakeClientFlag(returnedSlot, false);
        }

        uint64_t sid = 0;
        if (cfgSid != 0)
        {
            sid = MakeUniqueSteamId(returnedSlot, cfgSid);
            ssc::WriteSteamId(pClient, sid);
            Manager().SetSyntheticSid(returnedSlot, sid);
            Publisher().UpdateSyntheticSid(returnedSlot, sid);
        }
        RefreshClientUserInfo(returnedSlot);

        META_CONPRINTF("[BOTHIDER] slot=%d steamid64=%llu name='%s'\n",
                       returnedSlot, static_cast<unsigned long long>(sid),
                       boundName && boundName[0] ? boundName : "<null>");
        META_CONPRINTF("[BOTHIDER] CreateFakeClient end slot=%d depth=%zu\n",
                       returnedSlot, g_FakeClientCallStack.size());
        RETURN_META_VALUE(MRES_IGNORED, PlayerSlotHookResult(-1));
    }
#endif

    CUtlVector<INetworkGameClient *> *HiderPlugin::Hook_StartChangeLevel_Pre(
        const char *mapName, const char *landmark, void * /*changelevelState*/)
    {
        if (m_bSelfDisabled)
        {
            RETURN_META_VALUE(MRES_IGNORED, nullptr);
        }
#if !defined(_WIN32)
        ClearFakeClientCallStack();
#endif
        g_PendingControllerRemovals.clear();
        Manager().ReleaseAll();
        BotInfo().ResetAssignments();
        META_CONPRINTF("[BOTHIDER] StartChangeLevel PRE — map='%s' landmark='%s'\n",
                       mapName ? mapName : "?", landmark ? landmark : "");
        RETURN_META_VALUE(MRES_IGNORED, nullptr);
    }

    // Tick driver
    void HiderPlugin::Hook_GameFrame_Post(bool simulating, bool /*bFirst*/, bool /*bLast*/)
    {
        if (m_bSelfDisabled || !simulating)
            RETURN_META(MRES_IGNORED);

        DrainPendingControllerRemovals();

        for (int idx = 0; idx < PersonaPool::kMaxSlots; ++idx)
        {
            if (!Manager().IsManaged(idx))
                continue;
            void *pClient = ResolveClientBySlot(idx);
            if (pClient)
                ReleaseManagedHltvSlot(this, idx, pClient);
        }
        Manager().OnTick();

        // Reset bots' idle timers (1s)
        if ((++m_TickCounter & 63u) == 0u)
        {
#if !defined(_WIN32)
            // Poll mp_humanteam/bot_join_team. Windows replay servers use
            // these convars while their managed bots remain disguised
            if (m_bDisguiseEnabled && IsTeamForceActive())
            {
                META_CONPRINTF("[BOTHIDER] team-force convar active — disabling disguise\n");
                SetDisguiseEnabled(false);
            }
#endif

            for (int idx = 0; idx < PersonaPool::kMaxSlots; ++idx)
            {
                if (!Manager().IsManaged(idx))
                    continue;
                void *pClient = ResolveClientBySlot(idx);
                if (pClient)
                    ResetIdleTimerForClient(pClient);
            }
        }

        // Drain CSS -> C++ write commands posted via shared memory.
        Publisher().DrainCommands(
            // SET_SID: the versioned lease API has already validated the
            // complete batch. Publish its exact identity; silently replacing
            // it with another bot_info persona would split name/avatar/SID.
            [this](int slot, uint64_t sid)
            {
                if (!Manager().IsManaged(slot))
                    return;
                void *pClient = ResolveClientBySlot(slot);
                if (!pClient)
                    return;
                if (m_bDisguiseEnabled)
                {
                    ssc::ClearFakePlayer(pClient);
                    SetControllerFakeClientFlag(slot, false);
                }
                ssc::WriteSteamId(pClient, sid);
                if (!ssc::SteamIdMatches(pClient, sid))
                {
                    // Keep the publisher cache divergent so the managed lease
                    // reconciler retries instead of accepting a false native ACK.
                    Publisher().UpdateSyntheticSid(slot, 0);
                    return;
                }
                Manager().SetSyntheticSid(slot, sid);
                if (!RefreshClientUserInfo(slot))
                {
                    Publisher().UpdateSyntheticSid(slot, 0);
                    return;
                }
                Publisher().UpdateSyntheticSid(slot, sid);
            },
            // SET_PERSONA: verify the engine setter and explicitly republish
            // userinfo on every platform before acknowledging the command.
            [this](int slot, const char *name)
            {
                if (!Manager().IsManaged(slot) || !name || !name[0])
                    return;
                void *pClient = ResolveClientBySlot(slot);
                if (!pClient)
                    return;
                const char *storedName = SetEngineName(this, pClient, name);
                if (!EngineNameMatches(storedName, name))
                {
                    Publisher().UpdatePersonaName(slot, "");
                    return;
                }
                if (!RefreshClientUserInfo(slot))
                {
                    Publisher().UpdatePersonaName(slot, "");
                    return;
                }
                Personas().MarkSlotManaged(slot, name);
                Publisher().UpdatePersonaName(slot, name);
            },
            // SET_DISGUISE: global toggle for the m_bFakePlayer disguise
            [this](bool enabled)
            {
                SetDisguiseEnabled(enabled);
            },
            // REBUILD
            [this]()
            {
                RebuildBots();
            },
            // SET_NAME_SOURCE: global toggle for the display-name source
            [this](bool useBotInfo)
            {
                SetUseBotInfoName(useBotInfo);
                META_CONPRINTF("[BOTHIDER] name source -> %s\n",
                               useBotInfo ? "bot_info" : "botprofile");
            });
        RETURN_META(MRES_IGNORED);
    }

    void HiderPlugin::OnLevelInit(char const *pMapName, char const *, char const *,
                                  char const *, bool, bool)
    {
        g_PendingControllerRemovals.clear();
        auto *gameServer = g_pNetworkServerService
                               ? g_pNetworkServerService->GetIGameServer()
                               : nullptr;
        if (gameServer && gameServer != m_pHookedGameServer)
        {
            if (m_StartChangeLevelHookId != 0)
            {
                SH_REMOVE_HOOK_ID(m_StartChangeLevelHookId);
                m_StartChangeLevelHookId = 0;
            }
            m_StartChangeLevelHookId = SH_ADD_HOOK_MEMFUNC(
                INetworkGameServer, StartChangeLevel, gameServer,
                this, &HiderPlugin::Hook_StartChangeLevel_Pre, false /* PRE */);
            m_pHookedGameServer = static_cast<void *>(gameServer);
            META_CONPRINTF("[BOTHIDER] StartChangeLevel hook attached to %p (id %d)\n",
                           static_cast<void *>(gameServer), m_StartChangeLevelHookId);
        }
        META_CONPRINTF("[BOTHIDER] OnLevelInit map=%s\n", pMapName ? pMapName : "?");

        // Whitelisted maps run disguised
        if (IsDisguiseWhitelistMap(pMapName) && !m_bDisguiseEnabled)
        {
            META_CONPRINTF("[BOTHIDER] whitelist map '%s' — enabling disguise\n", pMapName);
            SetDisguiseEnabled(true);
        }
    }

    void HiderPlugin::OnLevelShutdown()
    {
#if !defined(_WIN32)
        ClearFakeClientCallStack();
#endif
        g_PendingControllerRemovals.clear();
        Manager().ReleaseAll();
        BotInfo().ResetAssignments();
        META_CONPRINTF("[BOTHIDER] OnLevelShutdown — state drained\n");
    }

    bool HiderPlugin::Load(PluginId id, ISmmAPI *ismm, char *error, size_t maxlen, bool /*late*/)
    {
        PLUGIN_SAVEVARS();

        GET_V_IFACE_CURRENT(GetEngineFactory, engine, IVEngineServer, INTERFACEVERSION_VENGINESERVER);
        GET_V_IFACE_CURRENT(GetEngineFactory, icvar, ICvar, CVAR_INTERFACE_VERSION);
        GET_V_IFACE_ANY(GetServerFactory, gameclients, IServerGameClients, INTERFACEVERSION_SERVERGAMECLIENTS);
        GET_V_IFACE_ANY(GetServerFactory, server, IServerGameDLL, INTERFACEVERSION_SERVERGAMEDLL);
        GET_V_IFACE_ANY(GetEngineFactory, g_pNetworkServerService, INetworkServerService,
                        NETWORKSERVERSERVICE_INTERFACE_VERSION);

        // GameResourceServiceServer — needed to resolve CCSPlayerController by slot
        // Served by engine2.dll
        g_pGameResourceService = ismm->GetEngineFactory(false)(
            targets::kIface_GameResourceServiceServer, nullptr);
        if (!g_pGameResourceService)
        {
            META_CONPRINTF("[BOTHIDER] warning: %s unresolved — controller mgmt disabled\n",
                           targets::kIface_GameResourceServiceServer);
        }
        else
        {
            META_CONPRINTF("[BOTHIDER] GameResourceService at %p\n", g_pGameResourceService);
        }

        // Resolve UTIL_Remove
        // Required to destroy controllers on kick
        {
            std::string gdPath = g_SMAPI->GetBaseDir();
            gdPath += "/addons/BotHider/gamedata.json";
            nlohmann::json gamedata;
            if (!sig::LoadGamedata(gdPath.c_str(), gamedata))
            {
                std::snprintf(error, maxlen,
                              "required gamedata.json not loaded at '%s'",
                              gdPath.c_str());
                return false;
            }
            LoadMemberOffsets(gamedata);
            if (const char *missing = FirstMissingMemberOffset())
            {
                std::snprintf(error, maxlen,
                              "required gamedata target missing: %s", missing);
                return false;
            }
            META_CONPRINTF(
                "[BOTHIDER] gamedata targets: SetName=#%d entitySystem=%d "
                "entityList=%d identitySize=%d instance=%d className=%d\n",
#if defined(_WIN32)
                targets::kVTSlot_ClientSetName,
#else
                -1,
#endif
                targets::kEntSys_OffsetInGameResSvc,
                targets::kEntSys_IdentityChunksOffset,
                targets::kEntIdentity_Size,
                targets::kEntIdentity_InstanceOffset,
                targets::kEntIdentity_ClassNameOffset);
            sig::ModuleInfo serverModule = sig::ModuleFromInterfacePtr(gameclients);
            if (!serverModule)
                serverModule = sig::ModuleFromName(targets::kServerModuleName);
            ResolveUtilRemoveAndEntSys(gamedata, serverModule);

            // Install the bot-quota flip-around detour
            InstallQuotaHook(gamedata, serverModule);

#if defined(_WIN32)
            // Restore managed-bot identity only during team validation.
            InstallHandleJoinTeamHook(gamedata, serverModule);
#endif

            // Install the pass-through engine entity-packing detour
            InstallPackEntitiesHook(gamedata);
        }
        if (g_pfnUtilRemove)
        {
            META_CONPRINTF("[BOTHIDER] UTIL_Remove resolved at %p (entSysGlobal=%p)\n",
                           reinterpret_cast<void *>(g_pfnUtilRemove),
                           reinterpret_cast<void *>(g_ppEntSysGlobal));
        }
        else
        {
            META_CONPRINTF("[BOTHIDER] warning: UTIL_Remove signature unresolved — "
                           "controller cleanup disabled\n");
        }

        g_pCVar = icvar;
        g_SMAPI->AddListener(this, this);

        // Resolve controller pawn and idle-timer schema offsets
        if (schema::Init())
        {
            int pawnOff = schema::GetFieldOffset("CBasePlayerController", "m_hPawn");
            int playerPawnOff = schema::GetFieldOffset("CCSPlayerController", "m_hPlayerPawn");
            int idleOff = schema::GetFieldOffset("CCSPlayerPawnBase", "m_flIdleTimeSinceLastAction");
            targets::kBaseEntity_FlagsOffset =
                schema::GetFieldOffset("CBaseEntity", "m_fFlags");
            targets::kController_TeamOffset =
                schema::GetFieldOffset("CBaseEntity", "m_iTeamNum");
            g_BotPawnHandleOffset = playerPawnOff >= 0 ? playerPawnOff : pawnOff;
            META_CONPRINTF("[BOTHIDER] schema resolved m_hPlayerPawn=%d m_hPawn=%d "
                           "m_flIdleTimeSinceLastAction=%d m_fFlags=%d m_iTeamNum=%d\n",
                           playerPawnOff, pawnOff, idleOff,
                           targets::kBaseEntity_FlagsOffset,
                           targets::kController_TeamOffset);
            if (g_BotPawnHandleOffset < 0 || targets::kBaseEntity_FlagsOffset < 0)
                META_CONPRINTF("[BOTHIDER] warning: bot pawn handle or m_fFlags unresolved - FL_BOT override disabled\n");
        }
        else
        {
            g_BotPawnHandleOffset = -1;
            targets::kBaseEntity_FlagsOffset = -1;
            targets::kController_TeamOffset = -1;
            META_CONPRINTF("[BOTHIDER] warning: SchemaSystem unresolved — idle-kick and FL_BOT overrides disabled\n");
        }

        InstallPreparedFunchooks();

        // Linux retains the upstream CUtlString::Set name path. Windows uses
        // CServerSideClient::SetName directly without this symbol
#if !defined(_WIN32)
        void *tier0 = dlopen(targets::kTier0ModuleName, RTLD_NOLOAD | RTLD_NOW);
        if (tier0)
        {
            m_pUtlStringSet = reinterpret_cast<CUtlStringSetFn>(
                dlsym(tier0, targets::kSym_CUtlString_Set));
        }
        if (!m_pUtlStringSet)
        {
            META_CONPRINTF("[BOTHIDER] warning: CUtlString::Set unresolved — name overwrite disabled\n");
        }
        else
        {
            META_CONPRINTF("[BOTHIDER] CUtlString::Set resolved at %p\n",
                           reinterpret_cast<void *>(m_pUtlStringSet));
        }
#endif

        Manager().Init();

        // Open the shared-memory bridge
        if (Publisher().Init())
        {
            META_CONPRINTF("[BOTHIDER] shared memory '%s' mapped\n", shm::kMappingName);
            // Publish resolved hook/sig addresses for bh_status (0 = unresolved)
            Publisher().PublishSignature("UTIL_Remove", reinterpret_cast<void *>(g_pfnUtilRemove));
            Publisher().PublishSignature("MaintainBotQuota", g_pQuotaHookTarget);
            Publisher().PublishSignature("PackEntities", g_pPackEntitiesHookTarget);
#if defined(_WIN32)
            Publisher().PublishSignature("HandleJoinTeam", g_pHandleJoinTeamHookTarget);
#endif
        }
        else
        {
            META_CONPRINTF("[BOTHIDER] warning: shared memory init failed — CSS bridge disabled\n");
        }

        // Load bot identity data from JSON config
        std::string jsonPath = g_SMAPI->GetBaseDir();
        jsonPath += "/addons/BotHider/bot_info.json";
        META_CONPRINTF("[BOTHIDER] loading JSON from: %s\n", jsonPath.c_str());
        if (BotInfo().Load(jsonPath.c_str()))
        {
            META_CONPRINTF("[BOTHIDER] bot_info.json loaded — %zu entries\n", BotInfo().Count());
        }
        else
        {
            META_CONPRINTF("[BOTHIDER] warning: bot_info.json not found or parse error at '%s' — "
                           "bot identity will fall back to curated roster\n",
                           jsonPath.c_str());
        }

        // Load the map whitelist
        std::string wlPath = g_SMAPI->GetBaseDir();
        wlPath += "/addons/BotHider/map_whitelist.json";
        LoadDisguiseWhitelist(wlPath.c_str());
        META_CONPRINTF("[BOTHIDER] disguise whitelist — %zu map(s) from '%s'\n",
                       g_DisguiseWhitelist.size(), wlPath.c_str());

        SH_ADD_HOOK(IServerGameClients, OnClientConnected, gameclients,
                    SH_MEMBER(this, &HiderPlugin::Hook_OnClientConnected_Post), true);
        SH_ADD_HOOK(IServerGameClients, ClientPutInServer, gameclients,
                    SH_MEMBER(this, &HiderPlugin::Hook_ClientPutInServer_Post), true);
        SH_ADD_HOOK(IServerGameClients, ClientDisconnect, gameclients,
                    SH_MEMBER(this, &HiderPlugin::Hook_ClientDisconnect_Pre), false);
#if !defined(_WIN32)
        SH_ADD_MANUALHOOK(CreateFakeClientSlotHook, engine,
                          SH_MEMBER(this, &HiderPlugin::Hook_CreateFakeClient_Pre), false);
        SH_ADD_MANUALHOOK(CreateFakeClientSlotHook, engine,
                          SH_MEMBER(this, &HiderPlugin::Hook_CreateFakeClient_Post), true);
#endif
        SH_ADD_HOOK(IServerGameDLL, GameFrame, server,
                    SH_MEMBER(this, &HiderPlugin::Hook_GameFrame_Post), true);
        SH_ADD_HOOK(ICvar, DispatchConCommand, icvar,
                    SH_MEMBER(this, &HiderPlugin::Hook_DispatchConCommand_Pre), false);
        SH_ADD_HOOK(ICvar, DispatchConCommand, icvar,
                    SH_MEMBER(this, &HiderPlugin::Hook_DispatchConCommand_Post), true);

        META_CONPRINTF("[BOTHIDER] loaded — m_bFakePlayer offset=%d, OCC=#%d CPiS=#%d\n",
                       ssc::OFFSET_m_bFakePlayer,
                       targets::kVTSlot_OnClientConnected,
                       targets::kVTSlot_ClientPutInServer);
        return true;
    }

    bool HiderPlugin::Unload(char *error, size_t maxlen)
    {
        if (!RemoveFunchooks())
        {
            std::snprintf(error, maxlen, "failed to uninstall funchook detours");
            return false;
        }
        SH_REMOVE_HOOK(IServerGameClients, OnClientConnected, gameclients,
                       SH_MEMBER(this, &HiderPlugin::Hook_OnClientConnected_Post), true);
        SH_REMOVE_HOOK(IServerGameClients, ClientPutInServer, gameclients,
                       SH_MEMBER(this, &HiderPlugin::Hook_ClientPutInServer_Post), true);
        SH_REMOVE_HOOK(IServerGameClients, ClientDisconnect, gameclients,
                       SH_MEMBER(this, &HiderPlugin::Hook_ClientDisconnect_Pre), false);
#if !defined(_WIN32)
        SH_REMOVE_MANUALHOOK(CreateFakeClientSlotHook, engine,
                             SH_MEMBER(this, &HiderPlugin::Hook_CreateFakeClient_Pre), false);
        SH_REMOVE_MANUALHOOK(CreateFakeClientSlotHook, engine,
                             SH_MEMBER(this, &HiderPlugin::Hook_CreateFakeClient_Post), true);
#endif
        SH_REMOVE_HOOK(IServerGameDLL, GameFrame, server,
                       SH_MEMBER(this, &HiderPlugin::Hook_GameFrame_Post), true);
        SH_REMOVE_HOOK(ICvar, DispatchConCommand, icvar,
                       SH_MEMBER(this, &HiderPlugin::Hook_DispatchConCommand_Pre), false);
        SH_REMOVE_HOOK(ICvar, DispatchConCommand, icvar,
                       SH_MEMBER(this, &HiderPlugin::Hook_DispatchConCommand_Post), true);

        if (m_StartChangeLevelHookId != 0)
        {
            SH_REMOVE_HOOK_ID(m_StartChangeLevelHookId);
            m_StartChangeLevelHookId = 0;
        }
        m_pHookedGameServer = nullptr;
        g_PendingControllerRemovals.clear();
        Manager().ReleaseAll();
        Publisher().Shutdown();
        return true;
    }

} // namespace cs2bh
