// Resolves server field offsets from the live CS2 SchemaSystem. Keeping these
// values out of static gamedata prevents valid-memory corruption after updates.

#include "schema_resolver.h"

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

namespace BotController::Schema
{
    using CreateIfaceFn = void *(*)(const char *, int *);

    namespace
    {
        ISchemaSystem *g_schema = nullptr;
        std::unordered_map<std::string, int> g_offsetCache;

#if defined(_WIN32)
        constexpr const char *kSchemaModule = "schemasystem.dll";
        constexpr const char *kServerScope = "server.dll";
#else
        constexpr const char *kSchemaModule = "libschemasystem.so";
        constexpr const char *kServerScope = "libserver.so";

        const char *BaseName(const char *path)
        {
            if (!path)
                return "";
            const char *slash = std::strrchr(path, '/');
            return slash ? slash + 1 : path;
        }

        struct FindModuleContext
        {
            const char *name = nullptr;
            const char *path = nullptr;
        };

        int FindModuleCallback(dl_phdr_info *info, size_t, void *data)
        {
            auto *context = static_cast<FindModuleContext *>(data);
            if (info->dlpi_name &&
                std::strcmp(BaseName(info->dlpi_name), context->name) == 0)
            {
                context->path = info->dlpi_name;
                return 1;
            }
            return 0;
        }

        void *OpenLoadedModule(const char *moduleName)
        {
            if (void *module = dlopen(moduleName, RTLD_NOW | RTLD_NOLOAD))
                return module;

            FindModuleContext context{};
            context.name = moduleName;
            dl_iterate_phdr(FindModuleCallback, &context);
            return context.path && context.path[0]
                       ? dlopen(context.path, RTLD_NOW | RTLD_NOLOAD)
                       : nullptr;
        }
#endif

        CSchemaClassInfo *FindClass(const char *className)
        {
            if (!g_schema)
                return nullptr;

            if (auto *scope = g_schema->FindTypeScopeForModule(kServerScope, nullptr))
            {
                if (auto *info = scope->FindDeclaredClass(className).Get())
                    return info;
            }
            if (auto *scope = g_schema->GlobalTypeScope())
                return scope->FindDeclaredClass(className).Get();
            return nullptr;
        }
    }

    bool Init()
    {
        if (g_schema)
            return true;

#if defined(_WIN32)
        HMODULE module = GetModuleHandleA(kSchemaModule);
        if (!module)
            return false;
        auto createInterface = reinterpret_cast<CreateIfaceFn>(
            GetProcAddress(module, "CreateInterface"));
#else
        void *module = OpenLoadedModule(kSchemaModule);
        if (!module)
            return false;
        auto createInterface = reinterpret_cast<CreateIfaceFn>(
            dlsym(module, "CreateInterface"));
#endif
        if (!createInterface)
            return false;

        g_schema = reinterpret_cast<ISchemaSystem *>(
            createInterface(SCHEMASYSTEM_INTERFACE_VERSION, nullptr));
        return g_schema != nullptr;
    }

    int GetFieldOffset(const char *className, const char *fieldName)
    {
        if (!className || !fieldName || !g_schema)
            return -1;

        const std::string key = std::string(className) + "::" + fieldName;
        if (const auto cached = g_offsetCache.find(key); cached != g_offsetCache.end())
            return cached->second;

        CSchemaClassInfo *info = FindClass(className);
        if (!info)
            return -1;

        for (uint16 i = 0; i < info->m_nFieldCount; ++i)
        {
            const SchemaClassFieldData_t &field = info->m_pFields[i];
            if (field.m_pszName && std::strcmp(field.m_pszName, fieldName) == 0)
            {
                const int offset = field.m_nSingleInheritanceOffset;
                g_offsetCache.emplace(key, offset);
                return offset;
            }
        }
        return -1;
    }
}
