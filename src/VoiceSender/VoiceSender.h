#pragma once

#include <cstdint>

class IVEngineServer2;
class INetworkMessages;

namespace BotController
{
    namespace VoiceSender
    {
        void SetInterfaces(IVEngineServer2 *engine, INetworkMessages *networkMessages);
        bool IsAvailable();
        int GetStatus();

        int SendVoiceFrame(
            int recipientSlot,
            int senderClient,
            uint64_t senderXuid,
            const uint8_t *audio,
            int audioBytes,
            int sampleRate,
            float voiceLevel,
            int sequenceBytes,
            int sectionNumber,
            int uncompressedSampleOffset,
            uint32_t numPackets,
            const uint32_t *packetOffsets,
            int packetOffsetCount,
            int tick,
            int audibleMask);
    }
}
