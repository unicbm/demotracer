// Cross-platform debug output + self-module path

#pragma once

#include <string>

namespace BotController
{
    // Windows: OutputDebugStringA; Linux: no-op
    void DebugOut(const char *msg);
    // Absolute path of this shared library on disk; empty on failure
    std::string SelfModulePath();
}
