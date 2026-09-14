#pragma once
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <optional>
#include <vector>

namespace BotController::ReplaySourceState {
enum class Kind { F32, I32, U32, Bool };
enum class Target { Clock, Identity, Pawn, Movement, ModernJump, Aim, Weapon, WeaponServices };
enum class ClockKind { None, Tick, Seconds };
enum Field : uint32_t {
    ServerTick = 0,
    PlayerTick = 1,
    DuckRoot = 2,
    DuckView = 3,
    LastDuckTime = 4,
    DuckOverride = 5,
    Stamina = 6,
    DuckAmount = 7,
    DuckSpeed = 8,
    Ducked = 9,
    Ducking = 10,
    DesiresDuck = 11,
    LastJumpTick = 12,
    LastJumpFrac = 13,
    LastJumpVelocityZ = 14,
    GroundTopology = 15,
    GroundTopologySmoothing = 16,
    FrictionStashedSpeed = 17,
    UseFrictionStashedSpeed = 18,
    FrictionStashedUntilFrac = 19,
    LadderSurface = 20,
    FallVelocity = 21,
    LastActualJumpPressTick = 22,
    LastActualJumpPressFrac = 23,
    LastUsableJumpPressTick = 24,
    LastUsableJumpPressFrac = 25,
    LastLandedTick = 26,
    LastLandedFrac = 27,
    LastLandedVelocityX = 28,
    LastLandedVelocityY = 29,
    LastLandedVelocityZ = 30,
    VelocityModifier = 31,
    Friction = 32,
    GravityScale = 33,
    GravityDisabled = 34,
    ShotsFired = 35,
    Scoped = 36,
    BaseVelocityX = 37,
    BaseVelocityY = 38,
    BaseVelocityZ = 39,
    PredictableAngleX = 40,
    PredictableAngleY = 41,
    PredictableAngleZ = 42,
    PredictableAngleVelX = 43,
    PredictableAngleVelY = 44,
    PredictableAngleVelZ = 45,
    UnpredictableAngleX = 46,
    UnpredictableAngleY = 47,
    UnpredictableAngleZ = 48,
    PredictableTick = 49,
    PredictableTickFrac = 50,
    UnpredictableTick = 51,
    Clip1 = 52,
    Clip2 = 53,
    InReload = 54,
    NextPrimaryTick = 55,
    NextPrimaryFrac = 56,
    NextSecondaryTick = 57,
    NextSecondaryFrac = 58,
    RecoilIndex = 59,
    AccuracyPenalty = 60,
    LastShotTime = 61,
    BurstShotsRemaining = 62,
    NextAttack = 63,
    ActiveWeaponHandle = 64,
    ReserveAmmoPrimary = 65,
    ReserveAmmoSecondary = 66,
    FieldCount = 67
};
struct Descriptor { Target target; Kind kind; ClockKind clock; const char *field; int componentOffset; };
inline constexpr std::array<Descriptor, FieldCount> fields{{
    {Target::Clock, Kind::U32, ClockKind::None, "server_tick", 0},
    {Target::Clock, Kind::U32, ClockKind::None, "m_nTickBase", 0},
    {Target::Movement, Kind::F32, ClockKind::None, "m_flDuckRootOffset", 0},
    {Target::Movement, Kind::F32, ClockKind::None, "m_flDuckViewOffset", 0},
    {Target::Movement, Kind::F32, ClockKind::Seconds, "m_flLastDuckTime", 0},
    {Target::Movement, Kind::Bool, ClockKind::None, "m_bDuckOverride", 0},
    {Target::Movement, Kind::F32, ClockKind::None, "m_flStamina", 0},
    {Target::Movement, Kind::F32, ClockKind::None, "m_flDuckAmount", 0},
    {Target::Movement, Kind::F32, ClockKind::None, "m_flDuckSpeed", 0},
    {Target::Movement, Kind::Bool, ClockKind::None, "m_bDucked", 0},
    {Target::Movement, Kind::Bool, ClockKind::None, "m_bDucking", 0},
    {Target::Movement, Kind::Bool, ClockKind::None, "m_bDesiresDuck", 0},
    {Target::Movement, Kind::I32, ClockKind::Tick, "m_nLastJumpTick", 0},
    {Target::Movement, Kind::F32, ClockKind::None, "m_flLastJumpFrac", 0},
    {Target::Movement, Kind::F32, ClockKind::None, "m_flLastJumpVelocityZ", 0},
    {Target::Movement, Kind::Bool, ClockKind::None, "m_bUsingGroundTopologyOffset", 0},
    {Target::Movement, Kind::F32, ClockKind::None, "m_flUsingGroundTopologyOffsetTransitionSmoothing", 0},
    {Target::Movement, Kind::F32, ClockKind::None, "m_flFrictionStashedSpeed", 0},
    {Target::Movement, Kind::Bool, ClockKind::None, "m_bUseFrictionStashedSpeed", 0},
    {Target::Movement, Kind::F32, ClockKind::None, "m_flUseFrictionStashedSpeedUntilFrac", 0},
    {Target::Movement, Kind::I32, ClockKind::None, "m_nLadderSurfacePropIndex", 0},
    {Target::Movement, Kind::F32, ClockKind::None, "m_flFallVelocity", 0},
    {Target::ModernJump, Kind::I32, ClockKind::Tick, "m_nLastActualJumpPressTick", 0},
    {Target::ModernJump, Kind::F32, ClockKind::None, "m_flLastActualJumpPressFrac", 0},
    {Target::ModernJump, Kind::I32, ClockKind::Tick, "m_nLastUsableJumpPressTick", 0},
    {Target::ModernJump, Kind::F32, ClockKind::None, "m_flLastUsableJumpPressFrac", 0},
    {Target::ModernJump, Kind::I32, ClockKind::Tick, "m_nLastLandedTick", 0},
    {Target::ModernJump, Kind::F32, ClockKind::None, "m_flLastLandedFrac", 0},
    {Target::ModernJump, Kind::F32, ClockKind::None, "m_flLastLandedVelocityX", 0},
    {Target::ModernJump, Kind::F32, ClockKind::None, "m_flLastLandedVelocityY", 0},
    {Target::ModernJump, Kind::F32, ClockKind::None, "m_flLastLandedVelocityZ", 0},
    {Target::Pawn, Kind::F32, ClockKind::None, "m_flVelocityModifier", 0},
    {Target::Pawn, Kind::F32, ClockKind::None, "m_flFriction", 0},
    {Target::Pawn, Kind::F32, ClockKind::None, "m_flGravityScale", 0},
    {Target::Pawn, Kind::Bool, ClockKind::None, "m_bGravityDisabled", 0},
    {Target::Pawn, Kind::I32, ClockKind::None, "m_iShotsFired", 0},
    {Target::Pawn, Kind::Bool, ClockKind::None, "m_bIsScoped", 0},
    {Target::Pawn, Kind::F32, ClockKind::None, "m_vecBaseVelocity", 0},
    {Target::Pawn, Kind::F32, ClockKind::None, "m_vecBaseVelocity", 4},
    {Target::Pawn, Kind::F32, ClockKind::None, "m_vecBaseVelocity", 8},
    {Target::Aim, Kind::F32, ClockKind::None, "m_predictableBaseAngle", 0},
    {Target::Aim, Kind::F32, ClockKind::None, "m_predictableBaseAngle", 4},
    {Target::Aim, Kind::F32, ClockKind::None, "m_predictableBaseAngle", 8},
    {Target::Aim, Kind::F32, ClockKind::None, "m_predictableBaseAngleVel", 0},
    {Target::Aim, Kind::F32, ClockKind::None, "m_predictableBaseAngleVel", 4},
    {Target::Aim, Kind::F32, ClockKind::None, "m_predictableBaseAngleVel", 8},
    {Target::Aim, Kind::F32, ClockKind::None, "m_unpredictableBaseAngle", 0},
    {Target::Aim, Kind::F32, ClockKind::None, "m_unpredictableBaseAngle", 4},
    {Target::Aim, Kind::F32, ClockKind::None, "m_unpredictableBaseAngle", 8},
    {Target::Aim, Kind::I32, ClockKind::Tick, "m_predictableBaseTick", 0},
    {Target::Aim, Kind::F32, ClockKind::None, "m_predictableBaseTickInterpAmount", 0},
    {Target::Aim, Kind::I32, ClockKind::Tick, "m_unpredictableBaseTick", 0},
    {Target::Weapon, Kind::I32, ClockKind::None, "m_iClip1", 0},
    {Target::Weapon, Kind::I32, ClockKind::None, "m_iClip2", 0},
    {Target::Weapon, Kind::Bool, ClockKind::None, "m_bInReload", 0},
    {Target::Weapon, Kind::I32, ClockKind::Tick, "m_nNextPrimaryAttackTick", 0},
    {Target::Weapon, Kind::F32, ClockKind::None, "m_flNextPrimaryAttackTickRatio", 0},
    {Target::Weapon, Kind::I32, ClockKind::Tick, "m_nNextSecondaryAttackTick", 0},
    {Target::Weapon, Kind::F32, ClockKind::None, "m_flNextSecondaryAttackTickRatio", 0},
    {Target::Weapon, Kind::F32, ClockKind::None, "m_flRecoilIndex", 0},
    {Target::Weapon, Kind::F32, ClockKind::None, "m_fAccuracyPenalty", 0},
    {Target::Weapon, Kind::F32, ClockKind::Seconds, "m_fLastShotTime", 0},
    {Target::Weapon, Kind::I32, ClockKind::None, "m_iBurstShotsRemaining", 0},
    {Target::WeaponServices, Kind::F32, ClockKind::Seconds, "m_flNextAttack", 0},
    {Target::Identity, Kind::U32, ClockKind::None, "m_hActiveWeapon", 0},
    {Target::Weapon, Kind::I32, ClockKind::None, "m_pReserveAmmo", 0},
    {Target::Weapon, Kind::I32, ClockKind::None, "m_pReserveAmmo", 4},
}};
struct Change { uint32_t tickIndex, fieldId, valueBits, present; };
static_assert(sizeof(Change) == 16);
using Snapshot = std::array<std::optional<uint32_t>, FieldCount>;
inline float Float(uint32_t bits) { float value; std::memcpy(&value, &bits, 4); return value; }
inline uint32_t Bits(float value) { uint32_t bits; std::memcpy(&bits, &value, 4); return bits; }
class Timeline {
    std::array<std::vector<Change>, FieldCount> values;
public:
    bool Load(const Change *changes, int count, int tickCount) {
        if (count < 0 || tickCount < 0 || count > int64_t(tickCount) * FieldCount || (count && !changes)) return false;
        Timeline staged;
        uint64_t previous = 0;
        for (int i = 0; i < count; ++i) {
            const auto &c = changes[i];
            const uint64_t key = (uint64_t(c.tickIndex) << 32) | c.fieldId;
            if (c.fieldId >= FieldCount || c.tickIndex >= uint32_t(tickCount) || c.present > 1 ||
                (i && previous >= key) || (!c.present && c.valueBits) ||
                (c.present && fields[c.fieldId].kind == Kind::F32 && !std::isfinite(Float(c.valueBits))) ||
                (c.present && fields[c.fieldId].kind == Kind::Bool && c.valueBits > 1)) return false;
            previous = key;
            staged.values[c.fieldId].push_back(c);
        }
        values.swap(staged.values);
        return true;
    }
    std::optional<uint32_t> Get(uint32_t field, uint32_t tick) const {
        if (field >= FieldCount) return std::nullopt;
        const auto &v = values[field];
        auto it = std::upper_bound(v.begin(), v.end(), tick, [](uint32_t t, const Change &c) { return t < c.tickIndex; });
        if (it == v.begin()) return std::nullopt;
        --it;
        return it->present ? std::optional<uint32_t>(it->valueBits) : std::nullopt;
    }
    Snapshot At(uint32_t tick) const {
        Snapshot s;
        for (uint32_t i = 0; i < FieldCount; ++i) s[i] = Get(i, tick);
        return s;
    }
};
struct LiveClock { int32_t tick; float time; float interval; };
// Zero/negative values are native inactive/sentinel timestamps, not old events.
inline std::optional<uint32_t> Rebase(uint32_t bits, ClockKind kind, uint32_t sourceTick, float sourceRate, LiveClock live) {
    if (!std::isfinite(sourceRate) || sourceRate <= 0 || !std::isfinite(live.time) ||
        !std::isfinite(live.interval) || live.interval <= 0 || std::fabs(sourceRate * live.interval - 1.0f) > 0.0001f) return std::nullopt;
    if (kind == ClockKind::Tick && int32_t(bits) > 0) {
        const int64_t mapped = int64_t(int32_t(bits)) - sourceTick + live.tick;
        if (mapped < INT32_MIN || mapped > INT32_MAX) return std::nullopt;
        return uint32_t(mapped);
    }
    if (kind == ClockKind::Seconds && Float(bits) > 0) {
        const float mapped = Float(bits) - float(sourceTick) / sourceRate + live.time;
        if (!std::isfinite(mapped)) return std::nullopt;
        return Bits(mapped);
    }
    return bits;
}
bool InitializeOffsets();
bool Apply(void *pawn, void *movement, void *weaponServices, void *weapon,
           const Snapshot &state, float sourceRate, LiveClock clock, bool weaponOnly);
}
