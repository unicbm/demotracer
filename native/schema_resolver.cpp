// Shared live SchemaSystem resolver derived from the maintained native runtimes.
// Licensed under the GNU Affero General Public License v3.0 only.
#include "schema_resolver.h"

#if defined(_WIN32)
#include <Windows.h>
#else
#include <dlfcn.h>
#include <link.h>
#endif

#include <cstdio>
#include <cstring>

namespace DemoTracerRuntime
{
    namespace
    {
        using CreateInterfaceFn = void *(*)(const char *, int *);

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
            if (!path) return "";
            const char *slash = std::strrchr(path, '/');
            return slash ? slash + 1 : path;
        }

        struct FindModuleContext
        {
            const char *name;
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
            FindModuleContext context{moduleName};
            dl_iterate_phdr(FindModuleCallback, &context);
            return context.path && context.path[0]
                ? dlopen(context.path, RTLD_NOW | RTLD_NOLOAD) : nullptr;
        }
#endif

        bool Fail(char *errorOut, std::size_t errorOutLen, const char *message)
        {
            if (errorOut && errorOutLen > 0)
                std::snprintf(errorOut, errorOutLen, "%s", message);
            return false;
        }
    }

    bool SchemaResolver::Init(char *errorOut, std::size_t errorOutLen)
    {
        if (m_schemaSystem && m_serverScope)
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

        m_schemaSystem = static_cast<ISchemaSystem *>(
            createInterface(SCHEMASYSTEM_INTERFACE_VERSION, nullptr));
#if !defined(_WIN32)
        // RTLD_NOLOAD still acquires a reference; release it on every path.
        dlclose(module);
#endif
        if (!m_schemaSystem)
            return Fail(errorOut, errorOutLen, "SchemaSystem_001 is unavailable");
        if (!m_schemaSystem->SchemaSystemIsReady())
        {
            Reset();
            return Fail(errorOut, errorOutLen, "SchemaSystem_001 is not ready");
        }

        m_serverScope = m_schemaSystem->FindTypeScopeForModule(kServerScopeName, nullptr);
        if (!m_serverScope)
        {
            Reset();
            return Fail(errorOut, errorOutLen, "server Schema type scope is unavailable");
        }
        m_entityScope = m_schemaSystem->FindTypeScopeForModule(kEntityScopeName, nullptr);
        m_globalScope = m_schemaSystem->GlobalTypeScope();
        return true;
    }

    CSchemaClassInfo *SchemaResolver::FindClass(const char *className)
    {
        if (auto *info = m_serverScope->FindDeclaredClass(className).Get())
            return info;
        if (m_entityScope)
        {
            if (auto *info = m_entityScope->FindDeclaredClass(className).Get())
                return info;
        }
        return m_globalScope ? m_globalScope->FindDeclaredClass(className).Get() : nullptr;
    }

    int SchemaResolver::GetFieldOffset(const char *className, const char *fieldName)
    {
        if (!m_serverScope || !className || !fieldName)
            return -1;

        const std::string key = std::string(className) + "::" + fieldName;
        if (const auto cached = m_offsetCache.find(key); cached != m_offsetCache.end())
            return cached->second;

        CSchemaClassInfo *info = FindClass(className);
        if (info && info->m_pFields)
        {
            for (uint16 i = 0; i < info->m_nFieldCount; ++i)
            {
                const SchemaClassFieldData_t &field = info->m_pFields[i];
                if (!field.m_pszName || std::strcmp(field.m_pszName, fieldName) != 0)
                    continue;
                const int offset = field.m_nSingleInheritanceOffset;
                if (offset < 0 || offset >= info->m_nSize)
                    break;
                m_offsetCache.emplace(key, offset);
                return offset;
            }
        }
        m_offsetCache.emplace(key, -1);
        return -1;
    }

    void SchemaResolver::Reset()
    {
        m_offsetCache.clear();
        m_globalScope = m_entityScope = m_serverScope = nullptr;
        m_schemaSystem = nullptr;
    }
}
