// schema_resolver.h
//
// Runtime schema field-offset resolver

#pragma once

namespace cs2bh::schema
{

    // Resolve the ready SchemaSystem interface and server type scope.
    bool Init();

    // Look up a networked field's byte offset
    // Returns -1 if class/field not found
    int GetFieldOffset(const char *className, const char *fieldName);

    // Clear this plugin's interface, scopes, and field cache at unload.
    void Reset();

} // namespace cs2bh::schema
