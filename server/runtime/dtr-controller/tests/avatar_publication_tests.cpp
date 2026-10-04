#include "avatar_overrides.h"
#include <networkstringtabledefs.h>
#include <convar.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

using namespace BotController::Avatars;
using Bytes = std::vector<unsigned char>;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "line %d: %s\n", __LINE__, #x); std::exit(1); } } while (0)

struct Table : INetworkStringTable
{
    struct Entry { std::string key; Bytes bytes; };
    std::vector<Entry> entries;
    bool rejectWrites = false;
    SetStringUserDataRequest_t read{};
    int FindStringIndex(const char *key) override
    {
        for (size_t i = 0; i < entries.size(); ++i)
            if (entries[i].key == key) return static_cast<int>(i);
        return -1;
    }
    const SetStringUserDataRequest_t *GetStringUserData(int index) override
    {
        auto &bytes = entries.at(index).bytes;
        read = {bytes.data(), static_cast<unsigned int>(bytes.size())};
        return &read;
    }
    int GetNumStrings() override { return static_cast<int>(entries.size()); }
    static Bytes Copy(const SetStringUserDataRequest_t *data)
    {
        if (!data->m_cbDataSize) return {};
        auto *start = static_cast<unsigned char *>(data->m_pRawData);
        return {start, start + data->m_cbDataSize};
    }
    int AddString(bool server, const char *key, const SetStringUserDataRequest_t *data) override
    {
        CHECK(server);
        if (rejectWrites) return -1;
        entries.push_back({key, Copy(data)});
        return static_cast<int>(entries.size()) - 1;
    }
    bool SetStringUserData(int index, const SetStringUserDataRequest_t *data, bool) override
    {
        if (rejectWrites) return false;
        entries.at(index).bytes = Copy(data);
        return true;
    }
    Bytes &At(const char *key) { return entries.at(FindStringIndex(key)).bytes; }
};

struct Tables : INetworkStringTableContainer
{
    Table table;
    bool available = true;
    INetworkStringTable *FindTable(const char *name) override
    {
        CHECK(std::strcmp(name, "ServerAvatarOverrides") == 0);
        return available ? &table : nullptr;
    }
};

int main()
{
    const Bytes original{1}, external{4};
    const Bytes a{0x89, 'P', 'N', 'G', 13, 10, 26, 10, 2};
    const Bytes b{0x89, 'P', 'N', 'G', 13, 10, 26, 10, 3};
    auto publish = [](uint64_t id, const Bytes &png) { return Publish(id, png.data(), static_cast<int>(png.size())); };
    Tables tables;
    Init(&tables);
    CHECK(publish(0, a) == -1);
    CHECK(publish(7, original) == -1);
    tables.available = false;
    CHECK(publish(7, a) == -2);
    tables.available = true;
    AvatarTest::available = false;
    CHECK(publish(7, a) == -6);
    AvatarTest::available = true;

    CHECK(publish(7, a) == 1);
    CHECK(AvatarTest::reliable);
    CHECK(tables.table.entries[0].bytes.empty()); // Reserve index-zero fallback.
    CHECK(tables.table.At("7") == a);
    CHECK(publish(7, a) == 0);
    CHECK(Clear(7) == 1);
    CHECK(tables.table.At("7").empty());
    CHECK(Clear(7) == 0);

    tables.table.At("7") = original;
    CHECK(publish(7, a) == 1);
    tables.table.rejectWrites = true;
    CHECK(publish(7, b) == -5);
    CHECK(Clear(7) == -5); // Failed restore retains ownership for retry.
    tables.table.rejectWrites = false;
    CHECK(publish(7, b) == 1);
    CHECK(Clear(7) == 1);
    CHECK(tables.table.At("7") == original); // Keep the first writer's baseline.

    CHECK(publish(7, a) == 1);
    tables.table.At("7") = external;
    CHECK(Clear(7) == 1);
    CHECK(tables.table.At("7") == external); // Preserve a later writer.

    CHECK(publish(7, a) == 1);
    OnLevelShutdown();
    tables.table.entries.clear(); // Reuse the same table object on a new map.
    CHECK(Clear(7) == 0);
    CHECK(publish(7, b) == 1);
    CHECK(Clear(7) == 1);
    CHECK(tables.table.At("7").empty()); // No earlier map's restore bytes.

    for (uint64_t id = 1; id <= 128; ++id) CHECK(publish(id, a) >= 0);
    CHECK(publish(129, a) == -4);
    CHECK(Clear(1) == 1);
    CHECK(publish(129, a) == 1); // Retired entries no longer consume capacity.
    tables.table.At("2") = external;
    Shutdown();
    CHECK(tables.table.At("2") == external);
    CHECK(tables.table.At("129").empty());
    CHECK(publish(7, a) == -2);
    CHECK(std::strstr(Status(), "active=0") != nullptr);

    Tables invalidFallback;
    invalidFallback.table.entries.push_back({"7", a});
    Init(&invalidFallback);
    CHECK(publish(8, b) == -5); // Never give an unknown SteamID another avatar.
    Shutdown();
    std::puts("server-only avatar publication lifecycle passed");
}
