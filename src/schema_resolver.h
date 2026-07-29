// schema_resolver.h
//
// Runtime schema field-offset resolver

#pragma once

namespace cs2bh::schema
{

    // Resolve ISchemaSystem from schemasystem.dll. Returns false if unavailable
    bool Init();

    // Look up a networked field's byte offset
    // Returns -1 if class/field not found
    int GetFieldOffset(const char *className, const char *fieldName);

} // namespace cs2bh::schema
