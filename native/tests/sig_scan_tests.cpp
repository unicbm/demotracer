// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
#include "../sig_scan.h"
#include <khook.hpp>
#include <array>
#include <cstdio>
#include <stdexcept>
#if defined(_WIN32)
#include <Windows.h>
#endif

namespace Sig = DemoTracerRuntime::Sig;

namespace
{
    void Check(bool condition, const char *message)
    {
        if (!condition)
            throw std::runtime_error(message);
    }

    void PatternsAndSegments()
    {
        std::array<unsigned char, 12> data{
            0xAA, 0xAA, 0xAA, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xAA, 0xBB, 0xCC, 0x99};
        Sig::ModuleInfo module{data.data(), data.size(), {{data.data(), 4}, {data.data() + 4, 8}}};
        std::vector<uint8_t> bytes;
        std::vector<bool> wild;
        Check(Sig::ParseSigString("AA AA", bytes, wild), "parse exact signature");
        const auto overlapping = Sig::FindPatternMatchesIn(module, bytes, wild);
        Check(overlapping == std::vector<void *>{data.data(), data.data() + 1},
              "multi-match scan must retain overlapping hits");
        Check(Sig::ParseSigString("AA ? CC", bytes, wild), "parse wildcard signature");
        Check(Sig::FindPatternMatchesIn(module, bytes, wild) ==
              std::vector<void *>{data.data() + 4, data.data() + 8},
              "multi-match scan lost wildcard hits");
        module.Segments = {{data.data() + 8, 3}};
        Check(Sig::FindPatternIn(module, bytes, wild) == data.data() + 8,
              "last complete candidate must remain searchable");
        module.Segments = {{data.data() + 8, 2}};
        Check(!Sig::FindPatternIn(module, bytes, wild), "scan exceeded segment end");
        module.Segments = {{data.data() + 4, 1}, {data.data() + 5, 3}};
        Check(Sig::FindPatternMatchesIn(module, bytes, wild).empty(),
              "scan crossed a segment boundary");
        Check(!Sig::FindPatternIn(module, bytes, {}), "scan accepted mismatched pattern vectors");
        for (const char *invalid : {"", "GG", "123"})
            Check(!Sig::ParseSigString(invalid, bytes, wild), "invalid pattern accepted");
    }

    void Gamedata()
    {
        nlohmann::json data;
        data["target"]["signatures"][Sig::PlatformName()] = "AA BB";
        data["target"]["offsets"][Sig::PlatformName()] = 42;
        Check(Sig::FindPlatformSig(data, "target") == "AA BB", "wrong platform signature");
        Check(Sig::FindPlatformOffset(data, "target", -1) == 42, "wrong platform offset");
        Check(Sig::FindPlatformSig(data, "missing").empty(), "missing signature fallback");
        Check(Sig::FindPlatformOffset(data, "missing", -1) == -1, "missing offset fallback");
        data["target"]["offsets"][Sig::PlatformName()] = "42";
        Check(Sig::FindPlatformOffset(data, "target", -1) == -1, "noninteger offset accepted");
    }

#if defined(_WIN32)
    void ModuleBounds()
    {
        const auto image = Sig::ModuleFromName("kernel32.dll");
        const auto code = Sig::ModuleCodeFromName("kernel32.dll");
        Check(image && code && image.Base == code.Base && image.Size == code.Size,
              "code scan must retain its containing image bounds");
        Check(code.Segments.size() == 1 && code.Segments[0].Base > image.Base &&
              code.Segments[0].Size <= image.Size - (code.Segments[0].Base - image.Base),
              "code-only scan must stay inside its mapped PE section");
        struct Interface { void *vtable; } object{image.Base};
        Check(Sig::ModuleFromInterfacePtr(&object).Base == image.Base,
              "interface lookup did not resolve vtable's containing module");
        Check(!Sig::ModuleFromInterfacePtr(nullptr) &&
              !Sig::ModuleFromInterfacePtr(reinterpret_cast<void *>(1)),
              "interface lookup accepted an invalid pointer");
        void *unreadable = VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_NOACCESS);
        Check(unreadable != nullptr, "could not allocate unreadable test page");
        const bool rejected = !Sig::ModuleFromInterfacePtr(unreadable);
        VirtualFree(unreadable, 0, MEM_RELEASE);
        Check(rejected, "interface lookup dereferenced an unreadable page");
    }
#endif
}

int main()
{
    int result = 0;
    try
    {
        PatternsAndSegments();
        Gamedata();
#if defined(_WIN32)
        ModuleBounds();
#endif
    }
    catch (const std::exception &error)
    {
        std::fprintf(stderr, "%s\n", error.what());
        result = 1;
    }
    // The standalone test engine starts worker threads even for lookup-only
    // clients. Join them before its static thread objects are destroyed.
    KHook::Shutdown();
    return result;
}
