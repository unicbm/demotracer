#include "avatar_overrides.h"
#include "avatar_publication.h"
#include "ccsbot_slot.h"
#include "hook.h"
#include "sig_scan.h"
#include "../../../common/khook_signature.h"

#include <networkstringtabledefs.h>
#include <convar.h>
#include <tier0/dbg.h>
#include <tier0/threadtools.h>
#include <atomic>
#include <array>
#include <charconv>
#include <cstring>
#include <mutex>
#include <string>
#include <optional>
#if defined(_WIN32)
#include <windows.h>
#endif

namespace BotController::Avatars
{
    namespace
    {
        constexpr const char *kTableName = "ServerAvatarOverrides";
        constexpr size_t kMaxPng = 16 * 1024;
        constexpr unsigned char kPng[] = {0x89, 'P', 'N', 'G', 13, 10, 26, 10};
        using Changed = void (BC_FASTCALL *)(void *, INetworkStringTable *, int,
                                             const char *, const SetStringUserDataRequest_t *);
        struct Delegate { void *context; Changed function; };
        using SetCallback = void (BC_FASTCALL *)(INetworkStringTable *, const Delegate *, bool);
        using RemoveTables = void (BC_FASTCALL *)(INetworkStringTableContainer *);
        using CreateEvent = void *(BC_FASTCALL *)(void **, void *, const char *, const char **);
        using QueueEvent = void (BC_FASTCALL *)(void *, float, void **);

        INetworkStringTableContainer *serverTables = nullptr;
        INetworkStringTableContainer *clientTables = nullptr;
        INetworkStringTable *boundTable = nullptr;
        Delegate previousDelegate{};
        std::recursive_mutex stateMutex;
        std::recursive_mutex publicationMutex;
        Publications publications;
        uint64_t clientEpoch = 1;
        uint64_t refreshes = 0;
        Hook<SetCallback> callbackHook;
        Hook<RemoveTables> removeHook;
        SetCallback setCallback = nullptr;
        RemoveTables removeTables = nullptr;
        CreateEvent createEvent = nullptr;
        QueueEvent queueEvent = nullptr;
        void **uiEngine = nullptr;
        std::atomic<bool> ready{false};
        std::string status = "not initialized";

        Sig::ModuleInfo ReadableModule(const char *name)
        {
            auto module = Sig::ModuleFromName(name);
#if defined(_WIN32)
            // Discardable PE sections may no longer be mapped after loading.
            module.Segments.clear();
            auto *cursor = module.Base;
            while (cursor && cursor < module.Base + module.Size)
            {
                MEMORY_BASIC_INFORMATION page{};
                if (!VirtualQuery(cursor, &page, sizeof(page)))
                    break;
                auto *end = std::min(static_cast<unsigned char *>(page.BaseAddress) + page.RegionSize,
                                     module.Base + module.Size);
                if (end <= cursor)
                    break;
                if (page.State == MEM_COMMIT && !(page.Protect & (PAGE_NOACCESS | PAGE_GUARD)))
                    module.Segments.push_back({cursor, static_cast<size_t>(end - cursor)});
                cursor = end;
            }
#endif
            return module;
        }

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
            return index > 0 && ReadBytes(table, key, actual) && SameBytes(actual, bytes);
        }

        bool DispatchReload(uint64_t steamId)
        {
            void *engine = nullptr;
            void *vtable = nullptr;
            void *asyncFunction = nullptr;
            if (!ready || !SafeRead(uiEngine, 0, engine) || !engine ||
                !SafeRead(engine, 0, vtable) ||
                !SafeRead(vtable, 0x180, asyncFunction) || asyncFunction != reinterpret_cast<void *>(queueEvent))
                return false;

            // Valve's uint64 event factory owns the allocation and vtable.
            // Async dispatch transfers it to Valve's UI queue: no DTR callback
            // or context survives plugin unload. Synchronous dispatch rejects
            // non-main-thread callers and must not be used here.
            char argument[32];
            std::snprintf(argument, sizeof(argument), "%llu)", static_cast<unsigned long long>(steamId));
            const char *end = nullptr;
            void *event = nullptr;
            createEvent(&event, nullptr, argument, &end);
            if (!event)
                return false;
            queueEvent(engine, 0.0f, &event);
            ++refreshes;
            return true;
        }

        void Observe(INetworkStringTable *table, const char *key)
        {
            uint64_t id = 0;
            if (!key)
                return;
            const auto length = std::strlen(key);
            const auto result = std::from_chars(key, key + length, id);
            if (result.ec != std::errc{} || result.ptr != key + length || id == 0)
                return;
            std::lock_guard lock(stateMutex);
            if (!ready || table != boundTable || !publications.entries.contains(id))
                return;
            Bytes actual;
            if (ReadBytes(table, key, actual) && publications.Observe(id, actual, clientEpoch))
            {
                if (!DispatchReload(id))
                    publications.RetryObservation(id);
            }
        }

        void BC_FASTCALL OnChanged(void *, INetworkStringTable *table, int index,
                                    const char *key, const SetStringUserDataRequest_t *data)
        {
            Delegate prior{};
            {
                std::lock_guard lock(stateMutex);
                if (table == boundTable)
                    prior = previousDelegate;
            }
            if (prior.function && prior.function != OnChanged)
                prior.function(prior.context, table, index, key, data);
            Observe(table, key);
        }

        KHook::Return<void> BC_FASTCALL OnSetCallback(INetworkStringTable *table, const Delegate *delegate, bool invoke)
        {
            if (ready && clientTables && table &&
                std::strcmp(table->GetTableName(), kTableName) == 0 &&
                clientTables->FindTable(kTableName) == table)
            {
                {
                    std::lock_guard lock(stateMutex);
                    if (boundTable != table)
                        ++clientEpoch;
                    boundTable = table;
                    if (delegate->function != OnChanged)
                        previousDelegate = *delegate;
                }
                const Delegate bridge{nullptr, OnChanged};
                callbackHook.Continue(table, &bridge, invoke);
                return {KHook::Action::Ignore};
            }
            callbackHook.Continue(table, delegate, invoke);
            return {KHook::Action::Ignore};
        }

        KHook::Return<void> BC_FASTCALL OnRemoveTables(INetworkStringTableContainer *container)
        {
            {
                std::lock_guard lock(stateMutex);
                if (container == clientTables)
                {
                    boundTable = nullptr;
                    previousDelegate = {};
                    ++clientEpoch;
                }
                if (container == serverTables)
                    publications.entries.clear();
            }
            removeHook.Continue(container);
            return {KHook::Action::Ignore};
        }

        void BindCurrentTable()
        {
            if (!ready || !clientTables || !ThreadInMainThread())
                return;
            auto *table = clientTables->FindTable(kTableName);
            if (!table)
                return;
            Delegate existing{};
            if (!SafeRead(table, 0x48, existing))
                return;
            callbackHook.Invoke(table, &existing, true);
        }

        void *Relative(const Sig::ModuleInfo &module, unsigned char *instruction, int displacement, int size)
        {
            int32_t delta = 0;
            if (!SafeRead(instruction, displacement, delta))
                return nullptr;
            auto *target = instruction + size + delta;
            return target >= module.Base && target < module.Base + module.Size ? target : nullptr;
        }

        void *ResolveUnique(const nlohmann::json &gd, const Sig::ModuleInfo &module, const char *name)
        {
            std::vector<uint8_t> bytes;
            std::vector<bool> wild;
            if (!Sig::ParseSigString(Sig::FindPlatformSig(gd, name), bytes, wild))
                return nullptr;
            void *found = nullptr;
            const auto signature = DemoTracerHooks::Signature(bytes, wild);
            for (const auto &segment : module.Segments)
            {
                size_t offset = 0;
                while (offset < segment.Size)
                {
                    auto *match = static_cast<unsigned char *>(DemoTracerHooks::FindSignature(
                        segment.Base + offset, segment.Size - offset, bytes.size(), signature));
                    if (!match)
                        break;
                    if (found)
                        return nullptr;
                    found = match;
                    offset = static_cast<size_t>(match - segment.Base) + 1;
                }
            }
            return found;
        }

        CreateEvent ResolveReloadFactory(const Sig::ModuleInfo &client)
        {
            // Resolve the named event's registration, then its native uint64
            // factory. Validate the instruction layout and shared event ID;
            // never use a build-specific absolute RVA or event number.
            constexpr char name[] = "ReloadAvatarImage";
            unsigned char *text = nullptr;
            for (const auto &segment : client.Segments)
            for (size_t i = 0; i + sizeof(name) <= segment.Size; ++i)
                if (std::memcmp(segment.Base + i, name, sizeof(name)) == 0)
                {
                    if (text)
                        return nullptr;
                    text = segment.Base + i;
                }
            if (!text)
                return nullptr;
            CreateEvent result = nullptr;
            for (const auto &segment : client.Segments)
            for (size_t i = 0; i + 0x40 < segment.Size; ++i)
            {
                auto *p = segment.Base + i;
                if (std::memcmp(p, "\x48\x8d\x15", 3) != 0 || Relative(client, p, 3, 7) != text)
                    continue;
                if (std::memcmp(p + 11, "\x48\x8d\x0d", 3) != 0 ||
                    std::memcmp(p + 0x31, "\x48\x8d\x05", 3) != 0)
                    return nullptr;
                auto *factory = static_cast<unsigned char *>(Relative(client, p + 0x31, 3, 7));
                std::array<unsigned char, 12> prolog{};
                if (!factory || !SafeRead(factory, 0, prolog) ||
                    std::memcmp(prolog.data(), "\x40\x53\x48\x83\xec\x20\x48\x8b\xd9\x0f\xb7\x0d", 12) != 0 ||
                    Relative(client, factory + 9, 3, 7) != Relative(client, p + 11, 3, 7) || result)
                    return nullptr;
                result = reinterpret_cast<CreateEvent>(factory);
            }
            return result;
        }
    }

    void Init(INetworkStringTableContainer *server, INetworkStringTableContainer *client,
              const nlohmann::json &gd)
    {
        serverTables = server;
        clientTables = client;
        status = "server publication only (no local client)";
#if defined(_WIN32)
        const auto clientModule = ReadableModule("client.dll");
        const auto engineModule = ReadableModule("engine2.dll");
        const auto panoramaModule = ReadableModule("panorama.dll");
        if (!client || !clientModule || !panoramaModule)
            return;
        auto *setter = ResolveUnique(gd, engineModule, "AvatarHud::SetStringChangedCallback");
        auto *remover = ResolveUnique(gd, engineModule, "AvatarHud::RemoveAllTables");
        auto *command = static_cast<unsigned char *>(ResolveUnique(gd, panoramaModule, "AvatarHud::DispatchEventCommand"));
        queueEvent = reinterpret_cast<QueueEvent>(ResolveUnique(gd, panoramaModule, "AvatarHud::DispatchEventAsync"));
        createEvent = ResolveReloadFactory(clientModule);
        void *containerVtable = nullptr;
        void *clearTarget = nullptr;
        if (!setter || !remover || !command || !queueEvent || !createEvent ||
            std::memcmp(command + 0x38, "\x48\x8b\x0d", 3) != 0 ||
            !SafeRead(client, 0, containerVtable) || !SafeRead(containerVtable, 0x68, clearTarget) || clearTarget != remover)
        {
            status = "native HUD bridge unavailable: client ABI validation failed";
            Warning("[BC avatar] %s\n", status.c_str());
            return;
        }
        uiEngine = static_cast<void **>(Relative(panoramaModule, command + 0x38, 3, 7));
        if (!uiEngine ||
            !callbackHook.Create(setter, OnSetCallback, &setCallback) ||
            !removeHook.Create(remover, OnRemoveTables, &removeTables) ||
            !removeHook.Enable() || !callbackHook.Enable())
        {
            callbackHook.Remove();
            removeHook.Remove();
            status = "native HUD bridge unavailable: hook installation failed";
            Warning("[BC avatar] %s\n", status.c_str());
            return;
        }
        ready = true;
        BindCurrentTable();
        status = "local client data callback -> native async ReloadAvatarImage";
        Msg("[BC avatar] %s\n", status.c_str());
#endif
    }

    int Publish(uint64_t id, const unsigned char *png, int length)
    {
        if (!id || !png || length < 8 || length > static_cast<int>(kMaxPng) || std::memcmp(png, kPng, 8) != 0)
            return -1;
        std::lock_guard publishing(publicationMutex);
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
        std::optional<Publication> rollback;
        {
            std::lock_guard lock(stateMutex);
            if (auto it = publications.entries.find(id); it != publications.entries.end())
                rollback = it->second;
            // Bound retired entries, without evicting any active owner.
            if (publications.entries.size() >= 128)
                std::erase_if(publications.entries, [](const auto &p) {
                    return !p.second.owned && (!ready || p.second.observedRevision == p.second.revision);
                });
            if (!rollback && publications.entries.size() >= 128)
                return -4;
            publications.Prepare(id, desired, previous);
        }
        if (!WriteBytes(table, key.c_str(), desired))
        {
            std::lock_guard lock(stateMutex);
            if (rollback)
                publications.entries[id] = std::move(*rollback);
            else
                publications.entries.erase(id);
            return -5;
        }
        // Install the evidence before a replicated cvar can cause a reload.
        if (!reliable.GetBool())
            reliable.SetBool(true);
        if (!reliable.GetBool())
            return -6;
        BindCurrentTable();
        if (ready && ThreadInMainThread())
        {
            auto *local = clientTables->FindTable(kTableName);
            if (local)
                Observe(local, key.c_str());
        }
        return SameBytes(previous, desired) ? 0 : 1;
    }

    int Clear(uint64_t id)
    {
        std::lock_guard publishing(publicationMutex);
        Publication current;
        {
            std::lock_guard lock(stateMutex);
            const auto it = publications.entries.find(id);
            if (it == publications.entries.end() || !it->second.owned)
                return 0;
            current = it->second;
        }
        auto *table = serverTables ? serverTables->FindTable(kTableName) : nullptr;
        const auto key = std::to_string(id);
        Bytes actual;
        if (table && !ReadBytes(table, key.c_str(), actual))
            return -3;
        // Preserve a later writer's data. Otherwise restore the pre-DTR value.
        const Bytes restored = SameBytes(actual, current.desired) ? current.restore : actual;
        {
            std::lock_guard lock(stateMutex);
            publications.Retire(id, restored);
        }
        if (table && !SameBytes(actual, restored) && !WriteBytes(table, key.c_str(), restored))
        {
            std::lock_guard lock(stateMutex);
            publications.entries[id] = std::move(current);
            return -5;
        }
        if (ready && ThreadInMainThread())
        {
            auto *local = clientTables->FindTable(kTableName);
            if (local)
                Observe(local, key.c_str());
        }
        return 1;
    }

    void ClearAll()
    {
        std::vector<uint64_t> ids;
        {
            std::lock_guard lock(stateMutex);
            for (const auto &[id, p] : publications.entries)
                if (p.owned)
                    ids.push_back(id);
        }
        for (const auto id : ids)
            Clear(id);
    }

    bool Shutdown()
    {
        // Local table restoration and delegate removal must be serialized
        // with the engine's client lifecycle, which runs on the main thread.
        if (ready && !ThreadInMainThread())
            return false;
        ClearAll();
        if (ready && clientTables)
        {
            auto *local = clientTables->FindTable(kTableName);
            std::lock_guard lock(stateMutex);
            if (local && local == boundTable)
            {
                // Complete local restoration before detaching. Only touch
                // entries still showing this publisher's retired image.
                for (const auto &[id, p] : publications.entries)
                {
                    const auto key = std::to_string(id);
                    const int index = local->FindStringIndex(key.c_str());
                    Bytes actual;
                    if (index > 0 && !p.owned && ReadBytes(local, key.c_str(), actual) &&
                        SameBytes(actual, p.retired))
                    {
                        SetStringUserDataRequest_t data{const_cast<unsigned char *>(p.desired.data()),
                                                       static_cast<unsigned int>(p.desired.size())};
                        local->SetStringUserData(index, &data, false);
                        Observe(local, key.c_str());
                    }
                }
                setCallback(local, &previousDelegate, false);
            }
        }
        ready = false;
        callbackHook.Remove();
        removeHook.Remove();
        std::lock_guard lock(stateMutex);
        publications.entries.clear();
        boundTable = nullptr;
        previousDelegate = {};
        serverTables = clientTables = nullptr;
        uiEngine = nullptr;
        status = "detached";
        return true;
    }

    const char *Status()
    {
        static thread_local std::string summary;
        std::lock_guard lock(stateMutex);
        size_t active = 0, pending = 0;
        for (const auto &[id, p] : publications.entries)
        {
            active += p.owned;
            pending += p.observedRevision != p.revision || p.observedEpoch != clientEpoch;
        }
        summary = status + "; refresh_events=" + std::to_string(refreshes) +
                  "; active=" + std::to_string(active) + "; pending=" + std::to_string(pending) +
                  "; tracked=" + std::to_string(publications.entries.size());
        return summary.c_str();
    }
}
