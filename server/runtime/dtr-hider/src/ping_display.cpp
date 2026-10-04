// ping_display.cpp

#include "ping_display.h"

#include <chrono>

namespace cs2bh
{

    void PingDisplay::RecordSample(int latencyMs)
    {
        int clamped = latencyMs;
        if (clamped < 0)
            clamped = 0;
        if (clamped > 999)
            clamped = 999;

        int oldest = m_Samples[m_Idx];
        m_Samples[m_Idx] = clamped;
        m_Sum += clamped - oldest;
        m_Idx = (m_Idx + 1) % kWindowTicks;
        if (m_Filled < kWindowTicks)
            ++m_Filled;
    }

    int PingDisplay::CurrentAverage() const
    {
        if (m_Filled == 0)
            return 0;
        return m_Sum / m_Filled;
    }

    int PingDisplay::MaybeProduce()
    {
        ++m_TicksSinceWrite;
        if (m_TicksSinceWrite < kWriteEveryTicks)
            return -1;
        m_TicksSinceWrite = 0;
        if (m_Filled == 0)
            return -1;
        int avg = CurrentAverage();
        if (avg == m_LastWritten)
            return -1;
        m_LastWritten = avg;
        return avg;
    }

    void PingDisplay::Reset()
    {
        m_Samples.fill(0);
        m_Idx = m_Sum = m_Filled = m_TicksSinceWrite = 0;
        m_LastWritten = 0;
    }

    // ─────────────────────────────────────────────────────────────────────

    PingJitter::PingJitter(int baselineMs)
        : m_Baseline(baselineMs < 5 ? 5 : (baselineMs > 250 ? 250 : baselineMs))
    {
        m_State =
            static_cast<uint64_t>(std::chrono::steady_clock::now().time_since_epoch().count()) ^ (static_cast<uint64_t>(baselineMs) * 0x9E3779B97F4A7C15ULL) ^ 0xA0761D6478BD642FULL;
        if (m_State == 0)
            m_State = 0xDEADBEEFCAFEBABEULL;
    }

    int PingJitter::NextSample()
    {
        // xorshift64*
        uint64_t x = m_State;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        m_State = x;
        uint64_t r = x * 0x2545F4914F6CDD1DULL;

        // +/- 10%
        int span = (m_Baseline + 9) / 10;
        int delta = static_cast<int>(r % (2 * span + 1)) - span;
        int v = m_Baseline + delta;
        if (v < 1)
            v = 1;
        if (v > 999)
            v = 999;
        return v;
    }

} // namespace cs2bh
