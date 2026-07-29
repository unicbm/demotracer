// plugin.h
//
// Metamod:Source plugin entry

#pragma once

#include <ISmmPlugin.h>
#include <playerslot.h>
#include <tier1/utlvector.h>
#include <array>

class CServerSideClient;
class INetworkGameClient;
class CCSPlayerController;
class ConCommandRef;
class CCommandContext;
class CCommand;
enum ENetworkDisconnectionReason : int;

namespace cs2bh
{

#if !defined(_WIN32)
    class PlayerSlotHookResult
    {
    public:
        // Constructs a SourceHook-compatible slot result
        explicit PlayerSlotHookResult(int slot = -1) : m_Data(slot) {}
        // Returns the underlying slot index
        int Get() const { return m_Data; }

    private:
        int m_Data;
    };

    static_assert(sizeof(PlayerSlotHookResult) == sizeof(CPlayerSlot));
    static_assert(alignof(PlayerSlotHookResult) == alignof(CPlayerSlot));
#endif

    class HiderPlugin : public ISmmPlugin, public IMetamodListener
    {
    public:
        // ISmmPlugin
        bool Load(PluginId id, ISmmAPI *ismm, char *error, size_t maxlen, bool late) override;
        bool Unload(char *error, size_t maxlen) override;

        const char *GetAuthor() override { return "XBribo(๑•.•๑)"; }
        const char *GetName() override { return "CS2-Bot-Hider"; }
        const char *GetDescription() override { return "Bot persona/steamid/ping/crosshair hider"; }
        const char *GetURL() override { return ""; }
        const char *GetLicense() override { return "AGPL-3.0"; }
        const char *GetVersion() override { return "0.2.9"; }
        const char *GetDate() override { return __DATE__; }
        const char *GetLogTag() override { return "BH"; }

        // IMetamodListener
        void OnLevelInit(char const *pMapName, char const *, char const *, char const *, bool, bool) override;
        void OnLevelShutdown() override;

        // Hook entry points
        void Hook_OnClientConnected_Post(CPlayerSlot slot, const char *pszName, uint64 xuid,
                                         const char *pszNetworkID, const char *pszAddress,
                                         bool bFakePlayer);
        void Hook_ClientPutInServer_Post(CPlayerSlot slot, char const *pszName, int type, uint64 xuid);
        void Hook_ClientDisconnect_Pre(CPlayerSlot slot, ENetworkDisconnectionReason reason,
                                       const char *pszName, uint64 xuid, const char *pszNetworkID);
#if !defined(_WIN32)
        PlayerSlotHookResult Hook_CreateFakeClient_Pre(const char *netname);
        PlayerSlotHookResult Hook_CreateFakeClient_Post(const char *netname);
#endif
        CUtlVector<INetworkGameClient *> *Hook_StartChangeLevel_Pre(
            const char *mapName, const char *landmark, void *changelevelState);
        void Hook_GameFrame_Post(bool simulating, bool bFirstTick, bool bLastTick);

        // ICvar::DispatchConCommand  — restore bot identity before the engine and processes a kick
        void Hook_DispatchConCommand_Pre(ConCommandRef cmd, const CCommandContext &ctx,
                                         const CCommand &args);
        void Hook_DispatchConCommand_Post(ConCommandRef cmd, const CCommandContext &ctx,
                                          const CCommand &args);

#if !defined(_WIN32)
        // Linux name path using CUtlString::Set from libtier0.so
        using CUtlStringSetFn = void (*)(void * /*CUtlString this*/, const char *);
        CUtlStringSetFn m_pUtlStringSet = nullptr;
#endif

        // Toggle disguise globally
        void SetDisguiseEnabled(bool enabled);

        void RebuildBots();

        // Toggle the display-name source: true=bot_info.json name, false=botprofile name
        void SetUseBotInfoName(bool useBotInfo) { m_bUseBotInfoName = useBotInfo; }

    private:
        void *m_pHookedGameServer = nullptr;
        int m_StartChangeLevelHookId = 0;
        bool m_bSelfDisabled = false;
        unsigned int m_TickCounter = 0; // throttles per-tick idle-timer reset
        // Master disguise switch
        bool m_bDisguiseEnabled = true;

        bool m_bRebuilding = false;

        int m_QuotaBeforeAdd = 0;

        int m_ManagedBeforeKick = 0;
        int m_QuotaBeforeKick = -1;
        bool m_AdjustQuotaAfterKick = false;

        // Display-name source: false=botprofile name, true=bot_info.json name
        bool m_bUseBotInfoName = false;
    };

    extern HiderPlugin g_Plugin;

} // namespace cs2bh

PLUGIN_GLOBALVARS();
