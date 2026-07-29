// sig_scan.h

#pragma once

#include <cstdint>
#include <cstddef>
#include <string>
#include <vector>

#include <nlohmann/json.hpp>

namespace cs2bh::sig
{
    struct ModuleSegment
    {
        unsigned char *Base = nullptr;
        size_t Size = 0;
    };

    struct ModuleInfo
    {
        unsigned char *Base = nullptr;
        size_t Size = 0;
        std::vector<ModuleSegment> Segments;

        explicit operator bool() const { return Base != nullptr && Size != 0; }
    };

    // Read + parse gamedata.json into `out`. Returns false on open/parse error
    bool LoadGamedata(const char *path, nlohmann::json &out);

    std::string FindPlatformSig(const nlohmann::json &gamedata, const std::string &name);

    // Read gamedata[name].offsets[platform]; returns fallback if missing/non-integer
    int FindPlatformOffset(const nlohmann::json &gamedata, const std::string &name, int fallback);

    const char *PlatformName();

    bool ParseSigString(const std::string &sigStr,
                        std::vector<uint8_t> &outBytes,
                        std::vector<bool> &outWild);

    void *FindPatternIn(const ModuleInfo &module,
                        const std::vector<uint8_t> &pattern,
                        const std::vector<bool> &wild);

    // Finds every pattern match in the selected module segments
    std::vector<void *> FindPatternMatchesIn(const ModuleInfo &module,
                                             const std::vector<uint8_t> &pattern,
                                             const std::vector<bool> &wild);

    // Resolve a module by basename, e.g. server.dll or libserver.so
    ModuleInfo ModuleFromName(const char *moduleName);

    // Resolves executable code ranges from a loaded module
    ModuleInfo ModuleCodeFromName(const char *moduleName);

    ModuleInfo ModuleFromInterfacePtr(void *interfacePtr);

    void *ResolveSig(const nlohmann::json &gamedata, const ModuleInfo &module,
                     const char *name, char *errorOut, size_t errorOutLen);
}
