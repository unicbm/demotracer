// schema_resolver.cpp
//
// Resolves networked field offsets from the live ISchemaSystem at runtime.

#include "schema_resolver.h"
#include "version_targets.h"

#include <schemasystem/schemasystem.h>

#if defined(_WIN32)
#include <Windows.h>
#else
#include <dlfcn.h>
#include <link.h>
#endif

#include <cstring>
#include <string>
#include <unordered_map>

namespace cs2bh::schema
{
    using CreateIfaceFn = void *(*)(const char *, int *);

    namespace
    {
        ISchemaSystem *g_pSchema = nullptr;
        std::unordered_map<std::string, int> g_OffsetCache;

#if !defined(_WIN32)
        const char *BaseName(const char *path)
        {
            if (!path)
                return "";
            const char *slash = std::strrchr(path, '/');
            return slash ? slash + 1 : path;
        }

        struct FindModuleCtx
        {
            const char *Name = nullptr;
            const char *Path = nullptr;
        };

        int FindModuleCallback(dl_phdr_info *info, size_t, void *data)
        {
            auto *ctx = static_cast<FindModuleCtx *>(data);
            if (info->dlpi_name && std::strcmp(BaseName(info->dlpi_name), ctx->Name) == 0)
            {
                ctx->Path = info->dlpi_name;
                return 1;
            }
            return 0;
        }

        void *OpenLoadedModule(const char *moduleName)
        {
            void *mod = dlopen(moduleName, RTLD_NOW | RTLD_NOLOAD);
            if (mod)
                return mod;

            FindModuleCtx ctx{};
            ctx.Name = moduleName;
            dl_iterate_phdr(FindModuleCallback, &ctx);
            if (ctx.Path && ctx.Path[0])
                return dlopen(ctx.Path, RTLD_NOW | RTLD_NOLOAD);
            return nullptr;
        }
#endif
    } // namespace

    bool Init()
    {
        if (g_pSchema)
            return true;

#if defined(_WIN32)
        HMODULE mod = GetModuleHandleA(targets::kSchemaSystemModuleName);
        if (!mod)
            return false;
        auto createIface = reinterpret_cast<CreateIfaceFn>(
            GetProcAddress(mod, "CreateInterface"));
#else
        void *mod = OpenLoadedModule(targets::kSchemaSystemModuleName);
        if (!mod)
            return false;
        auto createIface = reinterpret_cast<CreateIfaceFn>(
            dlsym(mod, "CreateInterface"));
#endif
        if (!createIface)
            return false;

        g_pSchema = reinterpret_cast<ISchemaSystem *>(
            createIface(SCHEMASYSTEM_INTERFACE_VERSION, nullptr));
        return g_pSchema != nullptr;
    }

    static CSchemaClassInfo *FindClass(const char *className)
    {
        static const char *kScopes[] = {
            targets::kSchemaServerTypeScope,
            "server.dll",
            "libserver.so",
        };

        for (const char *scopeName : kScopes)
        {
            if (auto *scope = g_pSchema->FindTypeScopeForModule(scopeName, nullptr))
            {
                if (auto *info = scope->FindDeclaredClass(className).Get())
                    return info;
            }
        }

        if (auto *scope = g_pSchema->GlobalTypeScope())
        {
            if (auto *info = scope->FindDeclaredClass(className).Get())
                return info;
        }
        return nullptr;
    }

    int GetFieldOffset(const char *className, const char *fieldName)
    {
        if (!className || !fieldName)
            return -1;
        std::string key = std::string(className) + "::" + fieldName;
        auto it = g_OffsetCache.find(key);
        if (it != g_OffsetCache.end())
            return it->second;
        if (!g_pSchema)
            return -1;

        CSchemaClassInfo *info = FindClass(className);
        if (!info)
            return -1;

        int offset = -1;
        for (uint16 i = 0; i < info->m_nFieldCount; ++i)
        {
            const SchemaClassFieldData_t &f = info->m_pFields[i];
            if (f.m_pszName && std::strcmp(f.m_pszName, fieldName) == 0)
            {
                offset = f.m_nSingleInheritanceOffset;
                break;
            }
        }
        g_OffsetCache[key] = offset;
        return offset;
    }

} // namespace cs2bh::schema
