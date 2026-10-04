#include "VoiceSender.h"

#include <eiface.h>
#include <inetchannel.h>
#include <netmessages.pb.h>
#include <networksystem/inetworkmessages.h>
#include <networksystem/netmessage.h>
#include <playerslot.h>

#include <cmath>

namespace BotController
{
    namespace VoiceSender
    {
        static IVEngineServer2 *g_engine = nullptr;
        static INetworkMessages *g_networkMessages = nullptr;
        static INetworkMessageInternal *g_voiceMessage = nullptr;

        void SetInterfaces(IVEngineServer2 *engine, INetworkMessages *networkMessages)
        {
            g_engine = engine;
            g_networkMessages = networkMessages;
            g_voiceMessage = nullptr;
        }

        static INetworkMessageInternal *FindVoiceMessage()
        {
            if (g_voiceMessage)
                return g_voiceMessage;
            if (!g_networkMessages)
                return nullptr;

            g_voiceMessage = g_networkMessages->FindNetworkMessageById(svc_VoiceData);
            if (!g_voiceMessage)
                g_voiceMessage = g_networkMessages->FindNetworkMessage("CSVCMsg_VoiceData");
            if (!g_voiceMessage)
                g_voiceMessage = g_networkMessages->FindNetworkMessage("svc_VoiceData");
            return g_voiceMessage;
        }

        int GetStatus()
        {
            if (!g_engine)
                return -1;
            if (!g_networkMessages)
                return -2;
            if (!FindVoiceMessage())
                return -3;
            return 0;
        }

        bool IsAvailable()
        {
            return GetStatus() == 0;
        }

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
            int audibleMask)
        {
            if (!g_engine || !g_networkMessages)
                return -1;
            if (recipientSlot < 0 || recipientSlot >= 64 || senderClient < 0 || !audio || audioBytes <= 0)
                return -2;
            if (packetOffsetCount < 0 || packetOffsetCount > 64 || (packetOffsetCount > 0 && !packetOffsets))
                return -2;

            INetChannelInfo *info = g_engine->GetPlayerNetInfo(CPlayerSlot(recipientSlot));
            if (!info)
                return -3;

            auto *channel = static_cast<INetChannel *>(info);
            auto *messageType = FindVoiceMessage();
            if (!messageType)
                return -4;

            CNetMessage *base = messageType->AllocateMessage();
            if (!base)
                return -5;

            auto *msg = base->ToPB<CSVCMsg_VoiceData>();
            auto *voice = msg->mutable_audio();
            voice->set_format(VOICEDATA_FORMAT_OPUS);
            voice->set_voice_data(audio, static_cast<size_t>(audioBytes));
            if (sampleRate > 0)
                voice->set_sample_rate(static_cast<uint32_t>(sampleRate));
            if (std::isfinite(voiceLevel))
                voice->set_voice_level(voiceLevel);
            if (sequenceBytes >= 0)
                voice->set_sequence_bytes(sequenceBytes);
            if (sectionNumber >= 0)
                voice->set_section_number(static_cast<uint32_t>(sectionNumber));
            if (uncompressedSampleOffset >= 0)
                voice->set_uncompressed_sample_offset(static_cast<uint32_t>(uncompressedSampleOffset));
            if (numPackets > 0)
                voice->set_num_packets(numPackets);
            for (int i = 0; i < packetOffsetCount; ++i)
                voice->add_packet_offsets(packetOffsets[i]);

            msg->set_client_deprecated(senderClient);
            // CPlayerSlot is zero-based, while CS2 player-controller entity
            // indices are one-based. The client resolves the visible voice
            // speaker through entity, so using the raw slot shifts identity
            // to the preceding controller.
            msg->set_entity(senderClient + 1);
            msg->set_proximity(false);
            if (senderXuid != 0)
                msg->set_xuid(senderXuid);
            if (audibleMask >= 0)
                msg->set_audible_mask(audibleMask);
            if (tick >= 0)
                msg->set_tick(static_cast<uint32_t>(tick));

            const bool ok = channel->SendNetMessage(base, BUF_VOICE);
            g_networkMessages->DeallocateNetMessageAbstract(messageType, base);
            return ok ? 0 : -6;
        }
    }
}
