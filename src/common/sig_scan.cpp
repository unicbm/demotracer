// Keep the runtime's existing internal API over the shared scanner.
#include "sig_scan.h"

namespace BotController::Sig
{
    namespace Shared = DemoTracerRuntime::Sig;

    bool LoadGamedata(const char *path, nlohmann::json &out)
    { return Shared::LoadGamedata(path, out); }

    std::string FindPlatformSig(const nlohmann::json &gamedata, const std::string &name)
    { return Shared::FindPlatformSig(gamedata, name); }

    int FindPlatformOffset(const nlohmann::json &gamedata, const std::string &name, int fallback)
    { return Shared::FindPlatformOffset(gamedata, name, fallback); }

    bool ParseSigString(const std::string &signature,
                        std::vector<uint8_t> &bytes, std::vector<bool> &wild)
    { return Shared::ParseSigString(signature, bytes, wild); }

    void *FindPatternIn(const ModuleInfo &module,
                        const std::vector<uint8_t> &pattern, const std::vector<bool> &wild)
    { return Shared::FindPatternIn(module, pattern, wild); }

    ModuleInfo ModuleFromName(const char *name)
    { return Shared::ModuleFromName(name); }

    ModuleInfo ModuleFromInterfacePtr(void *interfacePtr)
    { return Shared::ModuleFromInterfacePtr(interfacePtr); }

    void *ResolveSig(const nlohmann::json &gamedata, const ModuleInfo &module,
                     const char *name, char *errorOut, size_t errorOutLen)
    { return Shared::ResolveSig(gamedata, module, name, errorOut, errorOutLen); }
}
