#include "ReplaySourceState.h"
#include "ReplayNetworkState.h"
#include "schema_resolver.h"
#include "ccsbot_slot.h"
#include <initializer_list>
#include "platform.h"
#include <cstdio>

namespace BotController::ReplaySourceState {
namespace {
std::array<int, FieldCount> offsets;
int aimServicesOffset = -1;
int Find(std::initializer_list<const char *> classes, const char *field) {
    for (auto name : classes) { const int offset = Schema::GetFieldOffset(name, field); if (offset >= 0) return offset; }
    return -1;
}
}
bool InitializeOffsets() {
    const int modernJumpOffset = Schema::GetFieldOffset("CCSPlayer_MovementServices", "m_ModernJump");
    aimServicesOffset = Schema::GetFieldOffset("CCSPlayerPawn", "m_pAimPunchServices");
    bool ready = modernJumpOffset >= 0 && aimServicesOffset >= 0;
    for (uint32_t i = 0; i < FieldCount; ++i) {
        // Ladder contact detection resets and recomputes this index before use.
        // Keep its recorded ID readable, but do not restore it into simulation.
        if (i == LadderSurface || IsRuntimeOwnedAmmo(i)) { offsets[i] = -1; continue; }
        const auto &f = fields[i]; int offset = -1;
        switch (f.target) {
        case Target::Pawn: offset = Find({"CCSPlayerPawn", "CCSPlayerPawnBase", "CBasePlayerPawn", "CBaseEntity"}, f.field); break;
        case Target::Movement: offset = Find({"CCSPlayer_MovementServices", "CPlayer_MovementServices_Humanoid", "CPlayer_MovementServices"}, f.field); break;
        case Target::ModernJump: offset = Schema::GetFieldOffset("CCSPlayerModernJump", f.field); break;
        case Target::Aim: offset = Schema::GetFieldOffset("CCSPlayer_AimPunchServices", f.field); break;
        case Target::Weapon: offset = Find({"CCSWeaponBaseGun", "CCSWeaponBase", "CBasePlayerWeapon"}, f.field); break;
        case Target::WeaponServices: offset = Find({"CCSPlayer_WeaponServices", "CPlayer_WeaponServices"}, f.field); break;
        default: break;
        }
        offsets[i] = offset;
        if (f.target == Target::ModernJump && offset >= 0 && modernJumpOffset >= 0)
            offsets[i] += modernJumpOffset;
        if (offset < 0 && f.target != Target::Clock && f.target != Target::Identity) {
            char message[160];
            std::snprintf(message, sizeof(message), "[dtr-controller] source state field unavailable: %s\n", f.field);
            DebugOut(message);
        }
        // Optional historical weapon members can disappear between builds.
        // Missing data is never written through a guessed offset.
        if (f.target == Target::Movement || f.target == Target::ModernJump || f.target == Target::Aim)
            ready = ready && offset >= 0;
    }
    return ready;
}
bool Apply(void *pawn, void *movement, void *weaponServices, void *weapon,
           const Snapshot &state, float sourceRate, LiveClock clock, bool weaponOnly) {
    if (!pawn || !movement) return false;
    void *aim = nullptr;
    if (aimServicesOffset >= 0) SafeRead(pawn, aimServicesOffset, aim);
    // Player tickbase is the command simulation clock; never use the demo file tick.
    const auto sourceTick = state[PlayerTick];
    struct Write {
        void *owner;
        int fieldOffset, valueOffset;
        uint32_t bits;
        size_t size;
        bool pawnComponent;
    };
    std::array<Write, FieldCount> writes;
    size_t writeCount = 0;
    for (uint32_t i = 0; i < FieldCount; ++i) {
        const auto &f = fields[i];
        const bool weaponField = f.target == Target::Weapon || f.target == Target::WeaponServices;
        if (IsRuntimeOwnedAmmo(i) || weaponOnly != weaponField || !state[i] || offsets[i] < 0) continue;
        void *base = nullptr;
        switch (f.target) {
        case Target::Pawn: base = pawn; break;
        case Target::Movement:
        case Target::ModernJump: base = movement; break;
        case Target::Aim: base = aim; break;
        case Target::Weapon: base = weapon; break;
        case Target::WeaponServices: base = weaponServices; break;
        default: break;
        }
        if (!base) return false;
        auto bits = state[i];
        if (f.clock != ClockKind::None) {
            const bool active = f.clock == ClockKind::Seconds ? Float(*bits) > 0 : int32_t(*bits) > 0;
            if (!sourceTick && active) return false;
            bits = Rebase(*bits, f.clock, sourceTick.value_or(0), sourceRate, clock);
            if (!bits) return false;
        }
        const size_t size = f.kind == Kind::Bool ? 1 : 4;
        const int valueOffset = offsets[i] + f.componentOffset;
        uint32_t current = 0;
        if (!TryReadMemory(base, valueOffset, &current, size)) return false;
        if (current != *bits)
            writes[writeCount++] = {base, offsets[i], valueOffset, *bits, size,
                                    f.target != Target::Pawn && f.target != Target::Weapon};
    }
    size_t applied = 0;
    for (; applied < writeCount; ++applied) {
        const auto &write = writes[applied];
        if (!TryWriteMemory(write.owner, write.valueOffset, &write.bits, write.size)) break;
    }
    // Components of one recorded vector are contiguous and share one network field.
    // Publish successful writes even if a later write failed.
    for (size_t i = 0; i < applied; ++i) {
        const auto &write = writes[i];
        if (i > 0 && writes[i - 1].owner == write.owner && writes[i - 1].fieldOffset == write.fieldOffset)
            continue;
        if (write.pawnComponent)
            ReplayNetworkState::PawnComponentFieldChanged(write.owner, write.fieldOffset);
        else
            ReplayNetworkState::EntityFieldChanged(write.owner, write.fieldOffset);
    }
    return applied == writeCount;
}
}
