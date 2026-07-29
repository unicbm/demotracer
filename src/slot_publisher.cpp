// slot_publisher.cpp
//
// See slot_shm.h

#include "slot_publisher.h"

#if defined(_WIN32)
#include <Windows.h>
#else
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

#include <cstring>

namespace cs2bh
{

    namespace
    {
        SlotPublisher g_Publisher;
    }

    SlotPublisher &Publisher() { return g_Publisher; }

    SlotPublisher::~SlotPublisher() { Shutdown(); }

    // Create the page-file-backed mapping and stamp the header once
    bool SlotPublisher::Init()
    {
        if (m_pView)
            return true;

#if defined(_WIN32)
        HANDLE h = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr,
                                      PAGE_READWRITE, 0, shm::kTotalSize,
                                      shm::kMappingName);
        if (!h)
            return false;

        auto *view = static_cast<unsigned char *>(
            MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, shm::kTotalSize));
        if (!view)
        {
            CloseHandle(h);
            return false;
        }

        m_hMapping = h;
#else
        int fd = shm_open(shm::kMappingName, O_CREAT | O_RDWR, 0666);
        if (fd < 0)
            return false;

        if (ftruncate(fd, shm::kTotalSize) != 0)
        {
            close(fd);
            shm_unlink(shm::kMappingName);
            return false;
        }

        auto *view = static_cast<unsigned char *>(
            mmap(nullptr, shm::kTotalSize, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0));
        close(fd);
        if (view == MAP_FAILED)
            return false;

        m_hMapping = reinterpret_cast<void *>(1);
#endif
        m_pView = view;

        // ReadIdx/WriteIdx start at 0
        std::memset(view, 0, shm::kTotalSize);
        *reinterpret_cast<uint32_t *>(view + shm::kOff_Magic) = shm::kMagic;
        *reinterpret_cast<uint32_t *>(view + shm::kOff_Version) = shm::kVersion;
        *reinterpret_cast<uint32_t *>(view + shm::kOff_MaxSlots) = shm::kMaxSlots;
        *reinterpret_cast<uint32_t *>(view + shm::kOff_DataGen) = 0;
        return true;
    }

    void SlotPublisher::Shutdown()
    {
        if (m_pView)
        {
#if defined(_WIN32)
            UnmapViewOfFile(m_pView);
#else
            munmap(m_pView, shm::kTotalSize);
            shm_unlink(shm::kMappingName);
#endif
            m_pView = nullptr;
        }
        if (m_hMapping)
        {
#if defined(_WIN32)
            CloseHandle(m_hMapping);
#endif
            m_hMapping = nullptr;
        }
    }

    // Internal pointer helpers

    unsigned char *SlotPublisher::SlotStatePtr() const
    {
        return m_pView + shm::kOff_SlotState;
    }

    uint64_t *SlotPublisher::SidPtr(int slot) const
    {
        return reinterpret_cast<uint64_t *>(
            m_pView + shm::kOff_SyntheticSid + slot * sizeof(uint64_t));
    }

    char *SlotPublisher::NamePtr(int slot) const
    {
        return reinterpret_cast<char *>(
            m_pView + shm::kOff_PersonaName + slot * shm::kNameLen);
    }

    uint64_t *SlotPublisher::BaseSidPtr(int slot) const
    {
        return reinterpret_cast<uint64_t *>(
            m_pView + shm::kOff_BaseSyntheticSid + slot * sizeof(uint64_t));
    }

    char *SlotPublisher::BaseNamePtr(int slot) const
    {
        return reinterpret_cast<char *>(
            m_pView + shm::kOff_BasePersonaName + slot * shm::kNameLen);
    }

    int *SlotPublisher::PingPtr(int slot) const
    {
        return reinterpret_cast<int *>(
            m_pView + shm::kOff_CurrentPing + slot * sizeof(int));
    }

    char *SlotPublisher::CrosshairPtr(int slot) const
    {
        return reinterpret_cast<char *>(
            m_pView + shm::kOff_Crosshair + slot * shm::kCrosshairLen);
    }

    uint32_t *SlotPublisher::ScoreboardFlairPtr(int slot) const
    {
        return reinterpret_cast<uint32_t *>(
            m_pView + shm::kOff_ScoreboardFlair + slot * sizeof(uint32_t));
    }

    void SlotPublisher::BumpGen()
    {
        auto *gen = reinterpret_cast<volatile uint32_t *>(m_pView + shm::kOff_DataGen);
        *gen = *gen + 1;
    }

    // Data-region writers

    void SlotPublisher::PublishAdopt(int slot, uint64_t syntheticSid,
                                     const char *personaName, const char *crosshairCode,
                                     uint32_t scoreboardFlair)
    {
        if (!m_pView || slot < 0 || slot >= shm::kMaxSlots)
            return;
        *SidPtr(slot) = syntheticSid;
        *BaseSidPtr(slot) = syntheticSid;
        char *dst = NamePtr(slot);
        std::memset(dst, 0, shm::kNameLen);
        if (personaName)
        {
            std::strncpy(dst, personaName, shm::kNameLen - 1);
        }
        char *baseName = BaseNamePtr(slot);
        std::memset(baseName, 0, shm::kNameLen);
        if (personaName)
        {
            std::strncpy(baseName, personaName, shm::kNameLen - 1);
        }
        char *cross = CrosshairPtr(slot);
        std::memset(cross, 0, shm::kCrosshairLen);
        if (crosshairCode)
        {
            std::strncpy(cross, crosshairCode, shm::kCrosshairLen - 1);
        }
        *ScoreboardFlairPtr(slot) = scoreboardFlair;
        *PingPtr(slot) = 0;
        SlotStatePtr()[slot] = 1;
        BumpGen();
    }

    void SlotPublisher::PublishRelease(int slot)
    {
        if (!m_pView || slot < 0 || slot >= shm::kMaxSlots)
            return;
        SlotStatePtr()[slot] = 0;
        *SidPtr(slot) = 0;
        *BaseSidPtr(slot) = 0;
        std::memset(NamePtr(slot), 0, shm::kNameLen);
        std::memset(BaseNamePtr(slot), 0, shm::kNameLen);
        std::memset(CrosshairPtr(slot), 0, shm::kCrosshairLen);
        *ScoreboardFlairPtr(slot) = 0;
        *PingPtr(slot) = 0;
        BumpGen();
    }

    void SlotPublisher::UpdatePing(int slot, int ping)
    {
        if (!m_pView || slot < 0 || slot >= shm::kMaxSlots)
            return;
        *PingPtr(slot) = ping;
        BumpGen();
    }

    void SlotPublisher::UpdateSyntheticSid(int slot, uint64_t sid)
    {
        if (!m_pView || slot < 0 || slot >= shm::kMaxSlots)
            return;
        *SidPtr(slot) = sid;
        BumpGen();
    }

    void SlotPublisher::UpdatePersonaName(int slot, const char *name)
    {
        if (!m_pView || slot < 0 || slot >= shm::kMaxSlots)
            return;
        char *dst = NamePtr(slot);
        std::memset(dst, 0, shm::kNameLen);
        if (name)
            std::strncpy(dst, name, shm::kNameLen - 1);
        BumpGen();
    }

    // Append a signature status entry at the current count slot
    void SlotPublisher::PublishSignature(const char *name, const void *addr)
    {
        if (!m_pView || !name)
            return;
        auto *count = reinterpret_cast<uint32_t *>(m_pView + shm::kOff_SigCount);
        if (*count >= static_cast<uint32_t>(shm::kMaxSigs))
            return;
        auto *entry = reinterpret_cast<shm::SigEntry *>(
            m_pView + shm::kOff_SigEntries + (*count) * shm::kSigEntrySize);
        std::memset(entry->Name, 0, shm::kSigNameLen);
        std::strncpy(entry->Name, name, shm::kSigNameLen - 1);
        entry->Addr = reinterpret_cast<uint64_t>(addr);
        ++(*count);
        BumpGen();
    }

    // CSS->C++

    void SlotPublisher::DrainCommands(const SteamIdSink &onSteamId,
                                      const PersonaSink &onPersona,
                                      const DisguiseSink &onDisguise,
                                      const RebuildSink &onRebuild,
                                      const NameSourceSink &onNameSource)
    {
        if (!m_pView)
            return;
        auto *writeIdx = reinterpret_cast<volatile uint32_t *>(m_pView + shm::kOff_WriteIdx);
        auto *readIdx = reinterpret_cast<volatile uint32_t *>(m_pView + shm::kOff_ReadIdx);
        auto *cmds = reinterpret_cast<shm::Command *>(m_pView + shm::kOff_Cmds);

        uint32_t w = *writeIdx;
        uint32_t r = *readIdx;
        // Guard against a runaway producer: process at most kCmdCount entries
        int budget = shm::kCmdCount;
        while (r != w && budget-- > 0)
        {
            const shm::Command &c = cmds[r % shm::kCmdCount];
            // Global commands (no per-slot target)
            if (c.Type == shm::kCmd_SetDisguise && onDisguise)
            {
                onDisguise(c.SteamId != 0);
                ++r;
                continue;
            }
            if (c.Type == shm::kCmd_Rebuild && onRebuild)
            {
                onRebuild();
                ++r;
                continue;
            }
            if (c.Type == shm::kCmd_SetNameSource && onNameSource)
            {
                onNameSource(c.SteamId != 0);
                ++r;
                continue;
            }
            int slot = c.Slot;
            if (slot >= 0 && slot < shm::kMaxSlots)
            {
                if (c.Type == shm::kCmd_SetSteamId && onSteamId)
                {
                    onSteamId(slot, c.SteamId);
                }
                else if (c.Type == shm::kCmd_SetPersona && onPersona)
                {
                    char name[shm::kNameLen];
                    std::memcpy(name, c.Name, shm::kNameLen);
                    name[shm::kNameLen - 1] = '\0';
                    onPersona(slot, name);
                }
            }
            ++r;
        }
        *readIdx = r;
    }

} // namespace cs2bh
