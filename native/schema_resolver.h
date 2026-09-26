// Shared live SchemaSystem resolver derived from the maintained native runtimes.
// Licensed under the GNU Affero General Public License v3.0 only.
#pragma once

#include <schemasystem/schemasystem.h>
#include <cstddef>
#include <string>
#include <unordered_map>

namespace DemoTracerRuntime
{
    // Each plugin owns its own resolver; no interface or cache crosses DLL lifetimes.
    class SchemaResolver
    {
    public:
        bool Init(char *errorOut = nullptr, std::size_t errorOutLen = 0);
        int GetFieldOffset(const char *className, const char *fieldName);
        void Reset();

    private:
        CSchemaClassInfo *FindClass(const char *className);
        ISchemaSystem *m_schemaSystem = nullptr;
        CSchemaSystemTypeScope *m_serverScope = nullptr;
        CSchemaSystemTypeScope *m_entityScope = nullptr;
        CSchemaSystemTypeScope *m_globalScope = nullptr;
        std::unordered_map<std::string, int> m_offsetCache;
    };
}
