// Cross-platform debug output + self-module path

#include "platform.h"

#if defined(_WIN32)
#include <Windows.h>
#else
#include <dlfcn.h>
#endif

namespace BotController
{
    // Route a line to the platform debug sink. Linux: no-op (no debug output)
    void DebugOut(const char *msg)
    {
#if defined(_WIN32)
        if (msg)
            OutputDebugStringA(msg);
#else
        (void)msg;
#endif
    }

    // Resolve the on-disk path of the module containing this function
    std::string SelfModulePath()
    {
#if defined(_WIN32)
        HMODULE mod = nullptr;
        if (!GetModuleHandleExA(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                    GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCSTR>(&SelfModulePath), &mod))
            return "";
        char path[MAX_PATH] = {0};
        if (GetModuleFileNameA(mod, path, MAX_PATH) == 0)
            return "";
        return std::string(path);
#else
        Dl_info info{};
        if (dladdr(reinterpret_cast<void *>(&SelfModulePath), &info) == 0 ||
            !info.dli_fname)
            return "";
        return std::string(info.dli_fname);
#endif
    }
}
