// Each runtime retains an independent resolver and its existing namespace.
#include "schema_resolver.h"
#include "../../../common/schema_resolver.h"

namespace BotController::Schema
{
    namespace { DemoTracerRuntime::SchemaResolver resolver; }

    bool Init(char *errorOut, std::size_t errorOutLen)
    { return resolver.Init(errorOut, errorOutLen); }

    int GetFieldOffset(const char *className, const char *fieldName)
    { return resolver.GetFieldOffset(className, fieldName); }

    void Reset()
    { resolver.Reset(); }
}
