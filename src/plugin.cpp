// BotController native Metamod:Source plugin entry point.

#include <ISmmPlugin.h>

#include <cstdio>
#include <cstring>
#include <string>

#include <eiface.h>
#include <icvar.h>
#include <convar.h>
#include <interfaces/interfaces.h>
#include <networksystem/inetworkmessages.h>
#include <networkstringtabledefs.h>

#include <nlohmann/json.hpp>

#include "WeaponLocker.h"
#include "BuyController.h"
#include "BuyControllerState.h"
#include "BotController.h"
#include "InputInjector.h"
#include "MotionRecorder.h"
#include "VoiceSender.h"
#include "dispatch.h"
#include "WeaponLockerState.h"
#include "BotControllerState.h"
#include "commands.h"
#include "sig_scan.h"
#include "platform.h"
#include "version_targets.h"

class BotControllerPlugin : public ISmmPlugin
{
public:
    bool Load(PluginId id, ISmmAPI *ismm, char *error, size_t maxlen, bool late) override;
    bool Unload(char *error, size_t maxlen) override;

    bool Pause(char * /*error*/, size_t /*maxlen*/) override { return true; }
    bool Unpause(char * /*error*/, size_t /*maxlen*/) override { return true; }
    void AllPluginsLoaded() override {}

    const char *GetAuthor() override { return "XBribo(๑•.•๑)"; }
    const char *GetName() override { return "BotController"; }
    const char *GetDescription() override { return "Record and Replay CS2 bots."; }
    const char *GetURL() override { return ""; }
    const char *GetLicense() override { return "AGPLv3"; }
    const char *GetVersion() override { return "0.4.5"; }
    const char *GetDate() override { return __DATE__; }
    const char *GetLogTag() override { return "BL"; }
};

BotControllerPlugin g_BotControllerPlugin;
PLUGIN_EXPOSE(BotControllerPlugin, g_BotControllerPlugin);

// addons/<name>/bin/<platform>/<lib> -> up 3 dirs -> addons/<name>/gamedata.json
static std::string ComputeGamedataPath()
{
    std::string p = BotController::SelfModulePath();
    if (p.empty())
        return "";
    for (int i = 0; i < 3; ++i)
    {
        size_t slash = p.find_last_of("/\\");
        if (slash == std::string::npos)
            return "";
        p.resize(slash);
    }
    return p + "/gamedata.json";
}

bool BotControllerPlugin::Load(PluginId id, ISmmAPI *ismm,
                               char *error, size_t maxlen, bool /*late*/)
{
    PLUGIN_SAVEVARS();

    g_pCVar = static_cast<ICvar *>(
        ismm->GetEngineFactory()(CVAR_INTERFACE_VERSION, nullptr));
    if (!g_pCVar)
    {
        std::snprintf(error, maxlen,
                      "Failed to get ICvar (%s) via engine factory",
                      CVAR_INTERFACE_VERSION);
        return false;
    }
    ConVar_Register(FCVAR_RELEASE | FCVAR_GAMEDLL);

    // IVEngineServer2::ClientCommand
    BotController::Dispatch::g_pEngine = static_cast<IVEngineServer2 *>(
        ismm->GetEngineFactory()(INTERFACEVERSION_VENGINESERVER, nullptr));
    if (!BotController::Dispatch::g_pEngine)
    {
        std::snprintf(error, maxlen,
                      "Failed to get IVEngineServer2 (%s)",
                      INTERFACEVERSION_VENGINESERVER);
        return false;
    }

    // Need ISource2GameClients only as the anchor for sig-scan
    void *serverIface =
        ismm->GetServerFactory()(INTERFACEVERSION_SERVERGAMECLIENTS, nullptr);
    if (!serverIface)
    {
        std::snprintf(error, maxlen,
                      "Failed to get ISource2GameClients (%s)",
                      INTERFACEVERSION_SERVERGAMECLIENTS);
        return false;
    }

    // Engine interface used by console command output (ClientPrintf).
    BotController::Commands::g_pEngine = BotController::Dispatch::g_pEngine;
    BotController::Commands::g_pStringTables = static_cast<INetworkStringTableContainer *>(
        ismm->GetEngineFactory()(INTERFACENAME_NETWORKSTRINGTABLESERVER, nullptr));
    if (!BotController::Commands::g_pStringTables)
    {
        BotController::DebugOut(
            "[BotController] WARN: network string table server interface unavailable; "
            "bc_avatar_override_probe disabled\n");
    }
    BotController::Dispatch::g_pGameClients =
        static_cast<ISource2GameClients *>(serverIface);
    auto *networkMessages = static_cast<INetworkMessages *>(
        ismm->GetEngineFactory()(NETWORKMESSAGES_INTERFACE_VERSION, nullptr));
    if (!networkMessages)
    {
        networkMessages = static_cast<INetworkMessages *>(
            ismm->GetServerFactory()(NETWORKMESSAGES_INTERFACE_VERSION, nullptr));
    }
    BotController::VoiceSender::SetInterfaces(
        BotController::Dispatch::g_pEngine,
        networkMessages);
    if (!networkMessages)
    {
        BotController::DebugOut(
            "[BotController] WARN: network messages interface unavailable; "
            "voice send disabled\n");
    }

    std::string gamedataPath = ComputeGamedataPath();
    if (gamedataPath.empty())
    {
        std::snprintf(error, maxlen, "Failed to compute gamedata.json path");
        return false;
    }

    nlohmann::json gd;
    if (!BotController::Sig::LoadGamedata(gamedataPath.c_str(), gd))
    {
        std::snprintf(error, maxlen, "Failed to load gamedata: %s", gamedataPath.c_str());
        return false;
    }

    BotController::Sig::ModuleInfo serverModule =
        BotController::Sig::ModuleFromInterfacePtr(serverIface);
    if (!serverModule)
    {
        std::snprintf(error, maxlen, "ModuleFromInterfacePtr returned null");
        return false;
    }

    // offsets first (hooks/detours read tg::k* at runtime)
    BotController::targets::LoadFromGamedata(gd);

    if (!BotController::WeaponLockerHooks::Install(gd, serverModule, error, maxlen))
        return false;

    if (!BotController::BotControllerHooks::Install(gd, serverModule, error, maxlen))
    {
        BotController::WeaponLockerHooks::Remove();
        return false;
    }

    // BuyController is optional; missing sig only disables buy control.
    char buyErr[256] = {0};
    if (!BotController::BuyControllerHooks::Install(gd, serverModule, buyErr, sizeof(buyErr)))
    {
        char dbg[320];
        std::snprintf(dbg, sizeof(dbg),
                      "[BotController] WARN: BuyController::Install failed (%s); "
                      "bot buy control disabled\n",
                      buyErr);
        BotController::DebugOut(dbg);
    }

    // movement hooks for record/replay
    char injErr[256] = {0};
    if (!BotController::InputInjector::Install(gd, serverModule, injErr, sizeof(injErr)))
    {
        char dbg[320];
        std::snprintf(dbg, sizeof(dbg),
                      "[BotController] WARN: InputInjector::Install failed (%s); "
                      "record/replay movement will be a no-op\n",
                      injErr);
        BotController::DebugOut(dbg);
    }

    BotController::DebugOut("[BotController] plugin loaded successfully\n");
    return true;
}

bool BotControllerPlugin::Unload(char * /*error*/, size_t /*maxlen*/)
{
    BotController::MotionRecorder::ClearAll();
    BotController::InputInjector::Remove();
    BotController::BuyControllerHooks::Remove();
    BotController::BuyControllerState::ClearAll();
    BotController::BotControllerHooks::Remove();
    BotController::WeaponLockerHooks::Remove();
    BotController::WeaponLockerState::ClearAll();
    BotController::BotControllerState::ClearAllAll();
    BotController::BotControllerState::ClearAllAim();
    BotController::BotControllerState::ClearAllJump();
    BotController::Dispatch::g_pEngine = nullptr;
    BotController::Dispatch::g_pGameClients = nullptr;
    BotController::VoiceSender::SetInterfaces(nullptr, nullptr);
    BotController::Commands::g_pEngine = nullptr;
    BotController::Commands::g_pStringTables = nullptr;
    ConVar_Unregister();
    g_pCVar = nullptr;
    BotController::DebugOut("[BotController] plugin unloaded\n");
    return true;
}
