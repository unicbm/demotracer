#include "projectile_birth_align.h"
#include "live_entities.h"
#include "ccsbot_slot.h"
#include "version_targets.h"

#include <array>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace
{
    std::array<std::byte, 256> entity{};
    std::array<std::byte, 32> body{};
    std::array<std::byte, 64> node{};
    uint32_t liveHandle = 0x8002;
    bool alive = true;
    void Check(bool ok, const char *message)
    {
        if (!ok) { std::fprintf(stderr, "%s\n", message); std::exit(1); }
    }
    template <typename T, size_t N> void Put(std::array<std::byte, N> &storage, int offset, T value)
    { std::memcpy(storage.data() + offset, &value, sizeof(value)); }
    template <typename T, size_t N> T Get(const std::array<std::byte, N> &storage, int offset)
    { T value{}; std::memcpy(&value, storage.data() + offset, sizeof(value)); return value; }
    int Queue()
    {
        return BotController::ProjectileBirthAlign::Queue(
            reinterpret_cast<uintptr_t>(entity.data()), 1, 2, 3, 4, 5, 6);
    }
    auto Status()
    {
        BotController::ProjectileBirthAlign::Status result{};
        Check(BotController::ProjectileBirthAlign::GetStatus(&result, sizeof(result)) == 0, "read queue status");
        return result;
    }
}

// Only replace entity lookup and memory reads. The real queue/drain/write path runs.
namespace BotController
{
    bool TryReadMemory(const void *base, int offset, void *out, size_t size)
    {
        if (!base || offset < 0 || !out) return false;
        std::memcpy(out, static_cast<const std::byte *>(base) + offset, size);
        return true;
    }
    namespace LiveEntities
    {
        void *FromHandle(uint32_t handle) { return alive && handle == liveHandle ? entity.data() : nullptr; }
        uint32_t HandleForEntity(const void *ptr) { return alive && ptr == entity.data() ? liveHandle : 0; }
    }
}

int main()
{
    namespace queue = BotController::ProjectileBirthAlign;
    namespace tg = BotController::targets;
    tg::kEnt_BodyComponent = 32;
    tg::kEnt_AbsVelocity = 96;
    tg::kNode_AbsOrigin = 16;
    Put(entity, tg::kEnt_BodyComponent, static_cast<void *>(body.data()));
    Put(body, tg::kBody_SceneNode, static_cast<void *>(node.data()));
    Check(queue::ConfigureOffsets(64, 80) == 0, "configure projectile fields");
    Check(Queue() == 0, "queue live projectile");
    queue::ProcessPending();
    Check(Get<float>(entity, 64) == 1 && Get<float>(entity, 80) == 4 &&
          Get<float>(entity, 96) == 4 && Get<float>(node, 16) == 1, "live birth alignment was not applied");
    Check(Status().applied == 1 && Status().pending == 0, "successful drain status");

    Check(Queue() == 0, "queue before destruction");
    alive = false;
    const auto savedEntity = entity;
    const auto savedNode = node;
    queue::ProcessPending();
    Check(entity == savedEntity && node == savedNode && Status().failed == 1, "destroyed entity was written");
    Check(Queue() < 0, "destroyed entity accepted by queue");

    alive = true;
    Check(Queue() == 0, "queue before address reuse");
    liveHandle = 0x10002; // Same address/index, different serial.
    queue::ProcessPending();
    Check(entity == savedEntity && node == savedNode && Status().failed == 2, "reused entity address was written");
    Check(Status().pending == 0, "rejected work stayed queued");

    for (int i = 0; i < 65; ++i) Check(Queue() == 0, "queue batch");
    Check(Status().pending == 64 && Status().expired == 1, "bounded queue did not retire oldest work");
    queue::ProcessPending();
    Check(Status().pending == 0 && Status().applied == 65, "batch did not drain each entry exactly once");
    Check(Queue() == 0 && queue::Clear() == 1 && Status().pending == 0, "explicit cleanup did not clear queue");
}
