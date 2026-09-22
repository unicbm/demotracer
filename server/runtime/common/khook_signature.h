// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
#pragma once
#include <khook.hpp>
#include <string>
#include <vector>

namespace DemoTracerHooks
{
    inline std::string Signature(const std::vector<uint8_t> &bytes, const std::vector<bool> &wild)
    {
        constexpr char hex[] = "0123456789ABCDEF";
        std::string result;
        for (size_t i = 0; i < bytes.size(); ++i)
        {
            if (i) result += ' ';
            if (wild[i]) result += '?';
            else { result += hex[bytes[i] >> 4]; result += hex[bytes[i] & 15]; }
        }
        return result;
    }

    inline void *FindSignature(unsigned char *base, size_t size, size_t patternSize, const std::string &signature)
    {
        if (!base || !patternSize || size < patternSize) return nullptr;
        // KHook's size counts candidate start addresses. Keep the full pattern
        // inside the readable segment, including when the last bytes don't match.
        return KHook::LookupSignature(base, size - patternSize + 1, signature.c_str());
    }
}
