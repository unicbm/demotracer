// Cross-platform sig scanning + gamedata.json loader

#include "sig_scan.h"
#include "ccsbot_slot.h"

#if defined(_WIN32)
#include <Windows.h>
#include <psapi.h>
#else
#include <dlfcn.h>
#include <link.h>
#include <strings.h>
#endif

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <string>

namespace BotController::Sig
{
    namespace
    {
        const char *BaseName(const char *path)
        {
            if (!path)
                return "";
            const char *slash = std::strrchr(path, '/');
            const char *backslash = std::strrchr(path, '\\');
            const char *base = slash && backslash ? std::max(slash, backslash) : (slash ? slash : backslash);
            return base ? base + 1 : path;
        }

        void SetError(char *out, size_t outLen, const char *fmt, const char *a, const char *b = nullptr)
        {
            if (!out || outLen == 0)
                return;
            if (b)
                std::snprintf(out, outLen, fmt, a, b);
            else
                std::snprintf(out, outLen, fmt, a);
        }

#if defined(_WIN32)
        ModuleInfo ModuleFromHandle(HMODULE handle)
        {
            ModuleInfo out;
            if (!handle)
                return out;

            MODULEINFO mi{};
            if (!GetModuleInformation(GetCurrentProcess(), handle, &mi, sizeof(mi)))
                return out;

            out.Base = static_cast<unsigned char *>(mi.lpBaseOfDll);
            out.Size = static_cast<size_t>(mi.SizeOfImage);
            out.Segments.push_back({out.Base, out.Size});
            return out;
        }
#else
        bool NameMatches(const char *loadedPath, const char *moduleName)
        {
            if (!loadedPath || !loadedPath[0] || !moduleName || !moduleName[0])
                return false;
            const char *loadedBase = BaseName(loadedPath);
            const char *wantBase = BaseName(moduleName);
            return std::strcmp(loadedBase, wantBase) == 0;
        }

        void FillModuleFromPhdr(dl_phdr_info *info, ModuleInfo &out)
        {
            uintptr_t minAddr = UINTPTR_MAX;
            uintptr_t maxAddr = 0;
            out.Segments.clear();

            for (int i = 0; i < info->dlpi_phnum; ++i)
            {
                const ElfW(Phdr) &ph = info->dlpi_phdr[i];
                if (ph.p_type != PT_LOAD || ph.p_memsz == 0)
                    continue;

                auto *segBase = reinterpret_cast<unsigned char *>(info->dlpi_addr + ph.p_vaddr);
                size_t segSize = static_cast<size_t>(ph.p_memsz);
                out.Segments.push_back({segBase, segSize});

                uintptr_t start = reinterpret_cast<uintptr_t>(segBase);
                uintptr_t end = start + segSize;
                minAddr = std::min(minAddr, start);
                maxAddr = std::max(maxAddr, end);
            }

            if (minAddr != UINTPTR_MAX && maxAddr > minAddr)
            {
                out.Base = reinterpret_cast<unsigned char *>(minAddr);
                out.Size = static_cast<size_t>(maxAddr - minAddr);
            }
        }

        struct FindByNameCtx
        {
            const char *Name = nullptr;
            ModuleInfo Result;
        };

        int FindByNameCallback(dl_phdr_info *info, size_t, void *data)
        {
            auto *ctx = static_cast<FindByNameCtx *>(data);
            if (!NameMatches(info->dlpi_name, ctx->Name))
                return 0;

            FillModuleFromPhdr(info, ctx->Result);
            return ctx->Result ? 1 : 0;
        }

        struct FindByAddressCtx
        {
            uintptr_t Address = 0;
            ModuleInfo Result;
        };

        int FindByAddressCallback(dl_phdr_info *info, size_t, void *data)
        {
            auto *ctx = static_cast<FindByAddressCtx *>(data);
            for (int i = 0; i < info->dlpi_phnum; ++i)
            {
                const ElfW(Phdr) &ph = info->dlpi_phdr[i];
                if (ph.p_type != PT_LOAD || ph.p_memsz == 0)
                    continue;

                uintptr_t start = info->dlpi_addr + ph.p_vaddr;
                uintptr_t end = start + ph.p_memsz;
                if (ctx->Address >= start && ctx->Address < end)
                {
                    FillModuleFromPhdr(info, ctx->Result);
                    return ctx->Result ? 1 : 0;
                }
            }
            return 0;
        }
#endif
    } // namespace

    bool LoadGamedata(const char *path, nlohmann::json &out)
    {
        std::ifstream ifs(path, std::ios::binary);
        if (!ifs.is_open())
            return false;
        try
        {
            out = nlohmann::json::parse(ifs);
        }
        catch (...)
        {
            return false;
        }
        return out.is_object();
    }

    const char *PlatformName()
    {
#if defined(_WIN32)
        return "windows";
#else
        return "linux";
#endif
    }

    std::string FindPlatformSig(const nlohmann::json &gamedata, const std::string &name)
    {
        auto it = gamedata.find(name);
        if (it == gamedata.end() || !it->is_object())
            return "";
        auto sigIt = it->find("signatures");
        if (sigIt == it->end() || !sigIt->is_object())
            return "";
        auto platformIt = sigIt->find(PlatformName());
        if (platformIt == sigIt->end() || !platformIt->is_string())
            return "";
        return platformIt->get<std::string>();
    }

    // Read gamedata[name].offsets[platform]; fall back if entry missing/non-integer
    int FindPlatformOffset(const nlohmann::json &gamedata, const std::string &name, int fallback)
    {
        auto it = gamedata.find(name);
        if (it == gamedata.end() || !it->is_object())
            return fallback;
        auto offIt = it->find("offsets");
        if (offIt == it->end() || !offIt->is_object())
            return fallback;
        auto platformIt = offIt->find(PlatformName());
        if (platformIt == offIt->end() || !platformIt->is_number_integer())
            return fallback;
        return platformIt->get<int>();
    }

    bool ParseSigString(const std::string &sigStr,
                        std::vector<uint8_t> &outBytes,
                        std::vector<bool> &outWild)
    {
        outBytes.clear();
        outWild.clear();
        const char *p = sigStr.c_str();
        while (*p)
        {
            if (*p == ' ')
            {
                ++p;
                continue;
            }
            if (*p == '?')
            {
                outBytes.push_back(0);
                outWild.push_back(true);
                ++p;
                if (*p == '?')
                    ++p;
                continue;
            }
            char *end = nullptr;
            unsigned long v = std::strtoul(p, &end, 16);
            if (end == p || end - p > 2 || v > 0xFF)
                return false;
            outBytes.push_back(static_cast<uint8_t>(v));
            outWild.push_back(false);
            p = end;
        }
        return !outBytes.empty();
    }

    void *FindPatternIn(const ModuleInfo &module,
                        const std::vector<uint8_t> &pattern,
                        const std::vector<bool> &wild)
    {
        if (!module || pattern.empty() || pattern.size() != wild.size())
            return nullptr;

        const size_t plen = pattern.size();
        for (const ModuleSegment &segment : module.Segments)
        {
            if (!segment.Base || segment.Size < plen)
                continue;

            for (size_t i = 0; i + plen <= segment.Size; ++i)
            {
                bool match = true;
                for (size_t j = 0; j < plen; ++j)
                {
                    if (!wild[j] && segment.Base[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return segment.Base + i;
            }
        }
        return nullptr;
    }

    ModuleInfo ModuleFromName(const char *moduleName)
    {
#if defined(_WIN32)
        return ModuleFromHandle(GetModuleHandleA(moduleName));
#else
        FindByNameCtx ctx{};
        ctx.Name = moduleName;
        dl_iterate_phdr(FindByNameCallback, &ctx);
        return ctx.Result;
#endif
    }

    ModuleInfo ModuleFromInterfacePtr(void *interfacePtr)
    {
        if (!interfacePtr)
            return {};
        void *vtable = nullptr;
        if (!SafeRead(interfacePtr, 0, vtable) || !vtable)
            return {};

#if defined(_WIN32)
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(vtable, &mbi, sizeof(mbi)))
            return {};
        if (mbi.Type != MEM_IMAGE)
            return {};
        return ModuleFromHandle(reinterpret_cast<HMODULE>(mbi.AllocationBase));
#else
        FindByAddressCtx ctx{};
        ctx.Address = reinterpret_cast<uintptr_t>(vtable);
        dl_iterate_phdr(FindByAddressCallback, &ctx);
        return ctx.Result;
#endif
    }

    void *ResolveSig(const nlohmann::json &gamedata, const ModuleInfo &module,
                     const char *name, char *errorOut, size_t errorOutLen)
    {
        std::string sig = FindPlatformSig(gamedata, name);
        if (sig.empty())
        {
            SetError(errorOut, errorOutLen,
                     "gamedata missing '%s.signatures.%s'", name, PlatformName());
            return nullptr;
        }
        std::vector<uint8_t> bytes;
        std::vector<bool> wild;
        if (!ParseSigString(sig, bytes, wild))
        {
            SetError(errorOut, errorOutLen,
                     "failed to parse '%s' sig: '%s'", name, sig.c_str());
            return nullptr;
        }
        void *addr = FindPatternIn(module, bytes, wild);
        if (!addr)
        {
            SetError(errorOut, errorOutLen,
                     "sig '%s' not found in target module", name);
            return nullptr;
        }
        return addr;
    }
} // namespace BotController::Sig
