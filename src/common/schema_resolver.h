// Runtime server-schema field resolver.

#pragma once

namespace BotController::Schema
{
    // Resolve ISchemaSystem from the already-loaded engine module.
    bool Init();

    // Return the byte offset of a field declared by className, or -1.
    int GetFieldOffset(const char *className, const char *fieldName);
}
