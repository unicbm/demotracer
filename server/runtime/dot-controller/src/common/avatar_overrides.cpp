#include "avatar_overrides.h"

#include <networkstringtabledefs.h>
#include <convar.h>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace BotController::Avatars
{
    namespace
    {
        constexpr const char *kTableName = "ServerAvatarOverrides";
        constexpr size_t kMaxPng = 16 * 1024;
        constexpr unsigned char kPng[] = {0x89, 'P', 'N', 'G', 13, 10, 26, 10};
        using Bytes = std::vector<unsigned char>;
        struct Publication
        {
            Bytes desired;
            Bytes restore;
        };

        INetworkStringTableContainer *serverTables = nullptr;
        std::recursive_mutex publicationMutex;
        std::unordered_map<uint64_t, Publication> publications;

        bool ReadBytes(INetworkStringTable *table, const char *key, Bytes &bytes)
        {
            bytes.clear();
            const int index = table->FindStringIndex(key);
            if (index < 0)
                return true;
            const auto *data = table->GetStringUserData(index);
            if (!data || data->m_cbDataSize == 0)
                return true;
            if (!data->m_pRawData || data->m_cbDataSize > kMaxPng)
                return false;
            const auto *start = static_cast<const unsigned char *>(data->m_pRawData);
            bytes.assign(start, start + data->m_cbDataSize);
            return true;
        }

        bool WriteBytes(INetworkStringTable *table, const char *key, const Bytes &bytes)
        {
            if (table->GetNumStrings() == 0)
            {
                SetStringUserDataRequest_t empty{};
                if (table->AddString(true, "__dtr_no_avatar__", &empty) != 0)
                    return false;
            }
            // The client falls back to index zero for unknown SteamIDs.
            const auto *fallback = table->GetStringUserData(0);
            if (fallback && fallback->m_cbDataSize != 0)
                return false;
            int index = table->FindStringIndex(key);
            if (index == 0)
                return false;
            if (index < 0 && bytes.empty())
                return true;
            SetStringUserDataRequest_t data{const_cast<unsigned char *>(bytes.data()),
                                           static_cast<unsigned int>(bytes.size())};
            if (index < 0)
                index = table->AddString(true, key, &data);
            else
                table->SetStringUserData(index, &data, false);
            Bytes actual;
            return index > 0 && ReadBytes(table, key, actual) && actual == bytes;
        }
    }

    void Init(INetworkStringTableContainer *server)
    {
        std::lock_guard lock(publicationMutex);
        publications.clear();
        serverTables = server;
    }

    void OnLevelShutdown()
    {
        std::lock_guard lock(publicationMutex);
        // The server destroys its tables at this boundary. Never restore
        // an earlier map's data into the next map, even if pointers are reused.
        publications.clear();
    }

    int Publish(uint64_t id, const unsigned char *png, int length)
    {
        if (!id || !png || length < 8 || length > static_cast<int>(kMaxPng) || std::memcmp(png, kPng, 8) != 0)
            return -1;
        std::lock_guard lock(publicationMutex);
        auto *table = serverTables ? serverTables->FindTable(kTableName) : nullptr;
        if (!table)
            return -2;
        ConVarRefAbstract reliable("sv_reliableavatardata");
        if (!reliable.IsValidRef() || !reliable.IsConVarDataAvailable())
            return -6;
        const auto key = std::to_string(id);
        Bytes previous;
        if (!ReadBytes(table, key.c_str(), previous))
            return -3;
        const Bytes desired(png, png + length);
        const auto existing = publications.find(id);
        if (existing == publications.end() && publications.size() >= 128)
            return -4;
        if (!WriteBytes(table, key.c_str(), desired))
            return -5;
        if (existing == publications.end())
            publications.emplace(id, Publication{desired, previous});
        else
            existing->second.desired = desired;

        // Publish before enabling the server's replicated avatar setting.
        // Client cache invalidation remains entirely owned by the game.
        if (!reliable.GetBool())
            reliable.SetBool(true);
        if (!reliable.GetBool())
            return -6;
        return previous == desired ? 0 : 1;
    }

    int Clear(uint64_t id)
    {
        std::lock_guard lock(publicationMutex);
        const auto it = publications.find(id);
        if (it == publications.end())
            return 0;
        auto *table = serverTables ? serverTables->FindTable(kTableName) : nullptr;
        const auto key = std::to_string(id);
        Bytes actual;
        if (table && !ReadBytes(table, key.c_str(), actual))
            return -3;
        // Preserve a later writer's data; retain ownership if restoration fails.
        if (table && actual == it->second.desired && actual != it->second.restore &&
            !WriteBytes(table, key.c_str(), it->second.restore))
            return -5;
        publications.erase(it);
        return 1;
    }

    void ClearAll()
    {
        std::lock_guard lock(publicationMutex);
        std::vector<uint64_t> ids;
        for (const auto &[id, publication] : publications)
            ids.push_back(id);
        for (const auto id : ids)
            Clear(id);
    }

    void Shutdown()
    {
        std::lock_guard lock(publicationMutex);
        ClearAll();
        publications.clear();
        serverTables = nullptr;
    }

    const char *Status()
    {
        static thread_local std::string summary;
        std::lock_guard lock(publicationMutex);
        summary = std::string(serverTables ? "server publication only" : "server publication unavailable") +
                  "; active=" + std::to_string(publications.size());
        return summary.c_str();
    }
}
