// Resolves live server field offsets through SchemaSystem_001.

#include "schema_resolver.h"

#include <schemasystem/schemasystem.h>

#if defined(_WIN32)
#include <Windows.h>
#else
#include <dlfcn.h>
#include <link.h>
#endif

#include <cstdio>
#include <cstring>
#include <string>
#include <unordered_map>

namespace BotController::Schema
{
    using CreateInterfaceFn = void *(*)(const char *, int *);

    namespace
    {
        ISchemaSystem *g_schemaSystem = nullptr;
        CSchemaSystemTypeScope *g_serverScope = nullptr;
        CSchemaSystemTypeScope *g_entityScope = nullptr;
        CSchemaSystemTypeScope *g_globalScope = nullptr;
        std::unordered_map<std::string, int> g_offsetCache;

#if defined(_WIN32)
        constexpr const char *kSchemaModuleName = "schemasystem.dll";
        constexpr const char *kServerScopeName = "server.dll";
        constexpr const char *kEntityScopeName = "entity2.dll";
#else
        constexpr const char *kSchemaModuleName = "libschemasystem.so";
        constexpr const char *kServerScopeName = "libserver.so";
        constexpr const char *kEntityScopeName = "libentity2.so";

        const char *BaseName(const char *path)
        {
            if (!path)
                return "";
            const char *slash = std::strrchr(path, '/');
            return slash ? slash + 1 : path;
        }

        struct FindModuleContext
        {
            const char *moduleName = nullptr;
            const char *modulePath = nullptr;
        };

        int FindModuleCallback(dl_phdr_info *info, size_t, void *data)
        {
            auto *context = static_cast<FindModuleContext *>(data);
            if (info->dlpi_name &&
                std::strcmp(BaseName(info->dlpi_name), context->moduleName) == 0)
            {
                context->modulePath = info->dlpi_name;
                return 1;
            }
            return 0;
        }

        void *OpenLoadedModule(const char *moduleName)
        {
            if (void *module = dlopen(moduleName, RTLD_NOW | RTLD_NOLOAD))
                return module;

            FindModuleContext context{};
            context.moduleName = moduleName;
            dl_iterate_phdr(FindModuleCallback, &context);
            return context.modulePath && context.modulePath[0]
                       ? dlopen(context.modulePath, RTLD_NOW | RTLD_NOLOAD)
                       : nullptr;
        }
#endif

        bool Fail(char *errorOut, std::size_t errorOutLen, const char *message)
        {
            if (errorOut && errorOutLen > 0)
                std::snprintf(errorOut, errorOutLen, "%s", message);
            return false;
        }

        CSchemaClassInfo *FindClass(const char *className)
        {
            if (auto *info = g_serverScope->FindDeclaredClass(className).Get())
                return info;
            if (g_entityScope)
            {
                if (auto *info = g_entityScope->FindDeclaredClass(className).Get())
                    return info;
            }
            return g_globalScope
                       ? g_globalScope->FindDeclaredClass(className).Get()
                       : nullptr;
        }
    }

    bool Init(char *errorOut, std::size_t errorOutLen)
    {
        if (g_schemaSystem && g_serverScope)
            return true;
        Reset();

#if defined(_WIN32)
        HMODULE module = GetModuleHandleA(kSchemaModuleName);
        if (!module)
            return Fail(errorOut, errorOutLen, "schemasystem.dll is not loaded");
        auto createInterface = reinterpret_cast<CreateInterfaceFn>(
            GetProcAddress(module, "CreateInterface"));
#else
        void *module = OpenLoadedModule(kSchemaModuleName);
        if (!module)
            return Fail(errorOut, errorOutLen, "libschemasystem.so is not loaded");
        auto createInterface = reinterpret_cast<CreateInterfaceFn>(
            dlsym(module, "CreateInterface"));
#endif
        if (!createInterface)
        {
#if !defined(_WIN32)
            dlclose(module);
#endif
            return Fail(errorOut, errorOutLen,
                        "schemasystem CreateInterface export is unavailable");
        }

        g_schemaSystem = static_cast<ISchemaSystem *>(
            createInterface(SCHEMASYSTEM_INTERFACE_VERSION, nullptr));
#if !defined(_WIN32)
        dlclose(module);
#endif
        if (!g_schemaSystem)
            return Fail(errorOut, errorOutLen, "SchemaSystem_001 is unavailable");
        if (!g_schemaSystem->SchemaSystemIsReady())
        {
            Reset();
            return Fail(errorOut, errorOutLen, "SchemaSystem_001 is not ready");
        }

        g_serverScope =
            g_schemaSystem->FindTypeScopeForModule(kServerScopeName, nullptr);
        if (!g_serverScope)
        {
            Reset();
            return Fail(errorOut, errorOutLen,
                        "server Schema type scope is unavailable");
        }
        g_entityScope =
            g_schemaSystem->FindTypeScopeForModule(kEntityScopeName, nullptr);
        g_globalScope = g_schemaSystem->GlobalTypeScope();
        return true;
    }

    int GetFieldOffset(const char *className, const char *fieldName)
    {
        if (!g_serverScope || !className || !fieldName)
            return -1;

        const std::string key = std::string(className) + "::" + fieldName;
        if (const auto cached = g_offsetCache.find(key); cached != g_offsetCache.end())
            return cached->second;

        CSchemaClassInfo *info = FindClass(className);
        if (!info || !info->m_pFields)
        {
            g_offsetCache.emplace(key, -1);
            return -1;
        }

        for (uint16 i = 0; i < info->m_nFieldCount; ++i)
        {
            const SchemaClassFieldData_t &field = info->m_pFields[i];
            if (!field.m_pszName || std::strcmp(field.m_pszName, fieldName) != 0)
                continue;

            const int offset = field.m_nSingleInheritanceOffset;
            if (offset < 0 || offset >= info->m_nSize)
                break;
            g_offsetCache.emplace(key, offset);
            return offset;
        }
        g_offsetCache.emplace(key, -1);
        return -1;
    }

    void Reset()
    {
        g_offsetCache.clear();
        g_globalScope = nullptr;
        g_entityScope = nullptr;
        g_serverScope = nullptr;
        g_schemaSystem = nullptr;
    }
}
