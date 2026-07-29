// version_targets.h

#pragma once

#include <cstdint>

namespace cs2bh::targets
{

    // CNetworkGameServerBase::m_Clients — CUtlVector<CServerSideClient*>
    inline int kClientListOffset = 584;

    // CServerSideClient::m_bFakePlayer
    inline int kFakePlayerOffset = 160;

    // CServerSideClient::m_Name — CUtlString { char* m_pString } @ +0
    inline int kNameOffset = 64;

    // IServerGameClients (VCSource2GameClients) vtable slots
    inline constexpr int kVTSlot_OnClientConnected = 11;
    inline constexpr int kVTSlot_ClientPutInServer = 13;

#if defined(_WIN32)
    // Current engine2.dll CServerSideClient::SetName vtable slot
    inline constexpr int kVTSlot_ClientSetName = 0x1E8 / 8;
#else
    // Preserve the upstream Linux CreateFakeClient hook path
    inline constexpr int kVTSlot_CreateFakeClient = 52;
#endif

    // INetworkGameServer::StartChangeLevel vtable slot
    inline constexpr int kVTSlot_StartChangeLevel = 39;

    // Schema candidates
    inline constexpr int kSchemaFallback_m_iszPlayerName = 1300; // 0x514
    inline constexpr int kSchemaFallback_m_iPing = 2048;         // 0x800

    // * UTIL_Remove(CEntityInstance*) in server.dll

    inline constexpr const char *kIface_GameResourceServiceServer = "GameResourceServiceServerV001";
    inline constexpr int kEntSys_OffsetInGameResSvc = 0x58;   // GameResourceService → CGameEntitySystem*
    inline constexpr int kEntSys_IdentityChunksOffset = 0x10; // CEntitySystem → m_pIdentityChunks[]
    inline constexpr int kEntIdentity_Size = 0x70;            // sizeof(CEntityIdentity) = 112 (runtime-verified stride)
    inline constexpr int kEntIdentity_InstanceOffset = 0x00;  // CEntityIdentity::m_pInstance
    inline constexpr int kEntListChunkSize = 512;             // entities per identity chunk

    // CBasePlayerController::m_iszPlayerName
    inline constexpr int kController_PlayerNameOffset = 1780;

    inline int kController_FakeClientFlagsOffset = 904; // 0x388
    inline int kController_TeamOffset = 836;

    // CBaseEntity::m_fFlags network field
    inline constexpr int kBaseEntity_FlagsOffset = 0x388;
    inline constexpr uint32_t kEntityFlagBot = 0x10;

    // Linux upstream name path
#if !defined(_WIN32)
    inline constexpr const char *kSym_CUtlString_Set =
        "_ZN10CUtlString3SetEPKc";
#endif

#if defined(_WIN32)
    inline constexpr const char *kServerModuleName = "server.dll";
    inline constexpr const char *kEngineModuleName = "engine2.dll";
    inline constexpr const char *kTier0ModuleName = "tier0.dll";
    inline constexpr const char *kSchemaSystemModuleName = "schemasystem.dll";
    inline constexpr const char *kSchemaServerTypeScope = "server.dll";
#else
    inline constexpr const char *kEngineModuleName = "libengine2.so";
    inline constexpr const char *kServerModuleName = "libserver.so";
    inline constexpr const char *kTier0ModuleName = "libtier0.so";
    inline constexpr const char *kSchemaSystemModuleName = "libschemasystem.so";
    inline constexpr const char *kSchemaServerTypeScope = "libserver.so";
#endif

    // Interface version strings
    inline constexpr const char *kIface_ServerGameClients = "Source2GameClients001";

} // namespace cs2bh::targets
