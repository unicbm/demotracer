// Each runtime retains an independent resolver and its existing namespace.
#include "schema_resolver.h"
#include "../../common/schema_resolver.h"

namespace cs2bh::schema
{
    namespace { DemoTracerRuntime::SchemaResolver resolver; }

    bool Init()
    { return resolver.Init(); }

    int GetFieldOffset(const char *className, const char *fieldName)
    { return resolver.GetFieldOffset(className, fieldName); }

    void Reset()
    { resolver.Reset(); }
}
