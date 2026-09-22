// Typed hooks on Metamod's shared KHook engine.
#pragma once
#include "../../../common/khook.h"

#if defined(_MSC_VER)
#  define BC_FASTCALL __fastcall
#else
#  define BC_FASTCALL
#endif

namespace BotController
{
    template <typename Function> using Hook = DemoTracerHooks::Hook<Function>;
}
