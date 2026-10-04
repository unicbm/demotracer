#pragma once

class CCommandContext;
class IVEngineServer2;
class INetworkStringTableContainer;

namespace BotController
{
    namespace Commands
    {
        // Set by plugin.cpp Load(). Used to ClientPrintf back to the player
        // who issued a console command. nullptr -> fall back to server log.
        extern IVEngineServer2 *g_pEngine;
        extern INetworkStringTableContainer *g_pStringTables;

        void PrintToCaller(const CCommandContext &context, const char *fmt, ...);
    }
}
