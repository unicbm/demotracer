// ping_display.h

#pragma once

#include <array>
#include <cstdint>

namespace cs2bh
{

    class PingDisplay
    {
    public:
        static constexpr int kWindowTicks = 64;
        static constexpr int kWriteEveryTicks = 32;

        void RecordSample(int latencyMs);

        // Returns -1 when no value should be written this tick
        int MaybeProduce();

        void Reset();

        int LastWrittenPing() const { return m_LastWritten; }
        int CurrentAverage() const;

    private:
        std::array<int, kWindowTicks> m_Samples{};
        int m_Idx = 0;
        int m_Sum = 0;
        int m_Filled = 0;
        int m_TicksSinceWrite = 0;
        int m_LastWritten = 0;
    };

    class PingJitter
    {
    public:
        explicit PingJitter(int baselineMs);
        int NextSample();

    private:
        int m_Baseline;
        uint64_t m_State;
    };

} // namespace cs2bh
