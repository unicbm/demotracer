// personas.h

#pragma once

#include <array>
#include <deque>
#include <mutex>
#include <string>

namespace cs2bh
{

    class PersonaPool
    {
    public:
        PersonaPool();

        // Push a persona onto the FIFO
        // Called from Spawn() before bot_add
        void Push(const char *name);

        std::string Pop();

        size_t PendingCount() const;

        std::string PickFromRoster();

        // Mark a slot as managed
        void MarkSlotManaged(int slot, const char *name);
        void ClearSlot(int slot);
        bool IsSlotManaged(int slot) const;
        std::string GetSlotName(int slot) const;

        static constexpr int kMaxSlots = 64;

    private:
        mutable std::mutex m_Mutex;
        std::deque<std::string> m_Fifo;
        std::array<std::string, kMaxSlots> m_SlotNames;
        std::array<bool, kMaxSlots> m_SlotManaged{};
        uint64_t m_RosterRngState = 0;
    };

    PersonaPool &Personas();

} // namespace cs2bh
