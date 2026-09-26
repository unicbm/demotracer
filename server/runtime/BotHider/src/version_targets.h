// version_targets.h

#pragma once

#include <cstdint>

namespace cs2bh::targets
{

    // CNetworkGameServerBase::m_Clients — CUtlVector<CServerSideClient*>
    inline int kClientListOffset = -1;

#if defined(_WIN32)
    // CServerSideClient::SetName vtable slot, loaded from gamedata.
    inline int kVTSlot_ClientSetName = -1;
#else
    // Preserve the upstream Linux CreateFakeClient hook path
    inline constexpr int kVTSlot_CreateFakeClient = 52;
#endif

    inline constexpr const char *kIface_GameResourceServiceServer = "GameResourceServiceServerV001";
    inline int kEntSys_OffsetInGameResSvc = -1;   // GameResourceService → CGameEntitySystem*
    inline int kEntSys_IdentityChunksOffset = -1; // CEntitySystem → m_pIdentityChunks[]
    inline int kEntIdentity_Size = -1;            // sizeof(CEntityIdentity)
    inline int kEntIdentity_InstanceOffset = -1;  // CEntityIdentity::m_pInstance
    inline int kEntIdentity_ClassNameOffset = -1; // CEntityIdentity::m_designerName
    inline constexpr int kEntListChunkSize = 512;             // entities per identity chunk

    inline int kController_FakeClientFlagsOffset = -1;
    inline int kController_TeamOffset = -1;

    // Schema-backed fields, resolved from the live server at startup.
    inline int kBaseEntity_FlagsOffset = -1;
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
#else
    inline constexpr const char *kEngineModuleName = "libengine2.so";
    inline constexpr const char *kServerModuleName = "libserver.so";
    inline constexpr const char *kTier0ModuleName = "libtier0.so";
#endif

} // namespace cs2bh::targets
