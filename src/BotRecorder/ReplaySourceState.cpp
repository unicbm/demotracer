#include "ReplaySourceState.h"
#include "InputInjector.h"
#include "schema_resolver.h"
#include "ccsbot_slot.h"
#include "version_targets.h"
#include <initializer_list>
#include "platform.h"
#include <cstdio>

namespace BotController::ReplaySourceState {
namespace {
std::array<int, FieldCount> offsets;
int modernJumpOffset = -1, aimServicesOffset = -1;
int Find(std::initializer_list<const char *> classes, const char *field) {
    for (auto name : classes) { const int offset = Schema::GetFieldOffset(name, field); if (offset >= 0) return offset; }
    return -1;
}
}
bool InitializeOffsets() {
    modernJumpOffset = Schema::GetFieldOffset("CCSPlayer_MovementServices", "m_ModernJump");
    aimServicesOffset = Schema::GetFieldOffset("CCSPlayerPawn", "m_pAimPunchServices");
    bool ready = modernJumpOffset >= 0 && aimServicesOffset >= 0;
    for (uint32_t i = 0; i < FieldCount; ++i) {
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
        offsets[i] = offset < 0 ? -1 : offset + f.componentOffset;
        if (offset < 0 && f.target != Target::Clock && f.target != Target::Identity) {
            char message[160];
            std::snprintf(message, sizeof(message), "[BotController] source state field unavailable: %s\n", f.field);
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
    struct Write { void *base; int offset; uint32_t bits; size_t size; };
    std::vector<Write> writes;
    writes.reserve(FieldCount);
    bool pawnChanged = false, weaponChanged = false;
    for (uint32_t i = 0; i < FieldCount; ++i) {
        const auto &f = fields[i];
        const bool weaponField = f.target == Target::Weapon || f.target == Target::WeaponServices;
        if (weaponOnly != weaponField || !state[i] || offsets[i] < 0) continue;
        void *base = nullptr;
        switch (f.target) {
        case Target::Pawn: base = pawn; break;
        case Target::Movement: base = movement; break;
        case Target::ModernJump: if (modernJumpOffset >= 0) base = static_cast<char *>(movement) + modernJumpOffset; break;
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
        writes.push_back({base, offsets[i], *bits, f.kind == Kind::Bool ? size_t{1} : size_t{4}});
        if (f.target == Target::Weapon) weaponChanged = true;
        else pawnChanged = true;
    }
    for (const auto &write : writes) if (!TryWriteMemory(write.base, write.offset, &write.bits, write.size)) return false;
    if (pawnChanged) InputInjector::PublishReplayState(pawn);
    if (weaponChanged) InputInjector::PublishReplayState(weapon);
    return true;
}
}
