#pragma once

#include <cstdint>

namespace BotController::ProjectileBirthAlign
{
#pragma pack(push, 4)
    struct Status
    {
        int32_t size;
        int32_t configured;
        int32_t pending;
        int32_t queued;
        int32_t applied;
        int32_t expired;
        int32_t failed;
        int32_t initialPositionOffset;
        int32_t initialVelocityOffset;
    };
#pragma pack(pop)

    static_assert(sizeof(Status) == 36);

    int ConfigureOffsets(int initialPositionOffset, int initialVelocityOffset);
    int Queue(
        uint64_t entityPtr,
        float posX,
        float posY,
        float posZ,
        float velX,
        float velY,
        float velZ);
    int Clear();
    int GetStatus(Status *out, int size);
    void ProcessPending();
} // namespace BotController::ProjectileBirthAlign
