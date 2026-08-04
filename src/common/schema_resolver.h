// Runtime SchemaSystem field-offset resolver.

#pragma once

#include <cstddef>

namespace BotController::Schema
{
    // Resolve the live SchemaSystem interface and required type scopes.
    bool Init(char *errorOut, std::size_t errorOutLen);

    // Return the byte offset of a field declared by className, or -1.
    int GetFieldOffset(const char *className, const char *fieldName);

    // Clear the cached interface, type scope, and field offsets.
    void Reset();
}
