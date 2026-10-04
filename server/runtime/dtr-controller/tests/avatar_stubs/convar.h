// Only the server avatar setting is exposed to the production test target.
#pragma once
#include <cstring>

namespace AvatarTest
{
    inline bool reliable = false;
    inline bool available = true;
}

class ConVarRefAbstract
{
public:
    explicit ConVarRefAbstract(const char *name)
        : valid(std::strcmp(name, "sv_reliableavatardata") == 0) {}
    bool IsValidRef() const { return valid && AvatarTest::available; }
    bool IsConVarDataAvailable() const { return IsValidRef(); }
    bool GetBool() const { return AvatarTest::reliable; }
    void SetBool(bool value) { AvatarTest::reliable = value; }
private:
    bool valid;
};
