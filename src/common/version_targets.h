// version_targets.h

#pragma once

#include <cstddef>
#include <nlohmann/json.hpp>

namespace BotController::targets
{
    // ---- CCSBot ----

    // AI-ran-this-tick byte flag; decoded from CCSBot::Update.
    inline int kBot_AiTickedFlag = 0x610;
    // CCSBot -> pawn (CCSPlayerPawn*)
    inline int kBot_Pawn = 0x18;
    // Schema-backed perception state. Defaults document the 2026-08-04 layout;
    // plugin load replaces every one from the live server SchemaSystem.
    inline int kBot_Enemy = 0x59F0;                    // CHandle<CCSPlayerPawn>
    inline int kBot_IsEnemyVisible = 0x59F4;           // bool
    inline int kBot_VisibleEnemyParts = 0x59F5;        // uint8 bit mask
    inline int kBot_LastSawEnemyTimestamp = 0x5A04;    // float
    inline int kBot_FirstSawEnemyTimestamp = 0x5A08;   // float
    inline int kBot_CurrentEnemyAcquireTimestamp = 0x5A0C; // float
    inline int kBot_IsLastEnemyDead = 0x5A18;          // bool
    inline int kBot_NearbyEnemyCount = 0x5A1C;         // int32

    // ---- CBaseEntity / CEntityIdentity ----

    // entity -> CEntityIdentity*
    inline int kEnt_Identity = 0x10;
    // CEntityIdentity -> m_EHandle (low 15 bits = entity index)
    inline int kEntIdentity_EHandle = 0x10;
    // m_MoveType (MoveType_t, 1 byte) — restored each replay tick.
    inline int kEnt_MoveType = 0x2F3;
    // m_nActualMoveType (MoveType_t, 1 byte) — networked move type.
    inline int kEnt_ActualMoveType = 0x2F5;
    // m_fFlags (bit0 = FL_ONGROUND, bit1 = FL_DUCKING)
    inline int kEnt_Flags = 0x388;
    // m_fFlags bit masks restored on replay (constants, not offsets)
    inline constexpr unsigned kFL_OnGround = 1u << 0;
    inline constexpr unsigned kFL_Ducking = 1u << 1;
    // m_vecAbsVelocity
    inline int kEnt_AbsVelocity = 0x38C;
    // entity -> m_CBodyComponent -> m_pSceneNode -> m_vecAbsOrigin.
    inline int kEnt_BodyComponent = 0x30;
    inline int kBody_SceneNode = 0x08;
    // Legacy direct entity -> m_pGameSceneNode path, kept as a fallback for old builds.
    inline int kEnt_GameSceneNode = 0;
    inline int kNode_AbsOrigin = 0xC8;

    // ---- CCSPlayerPawn ----

    // m_pWeaponServices
    inline int kPawn_WeaponServices = 0xA00;
    // m_pMovementServices
    inline int kPawn_MovementServices = 0xA40;
    // m_pItemServices and replay-owned round-start equipment.
    inline int kPawn_ItemServices = 0;
    inline int kPawn_ArmorValue = 0;
    // m_hController (CHandle)
    inline int kPawn_Controller = 0xB80;
    // m_hOriginalController (CHandle)
    inline int kPawn_OriginalController = 0xCF4;
    // CCSPlayerPawn -> v_angle (QAngle)
    inline int kPawn_ViewAngle = 0xAB8;
    // v_anglePrevious (QAngle) — keep first-person spectator/camera history aligned
    inline int kPawn_ViewAnglePrevious = 0xAC4;
    // m_ServerViewAngleChanges — embedded network vector consumed by local/observer camera view.
    inline int kPawn_ServerViewAngleChanges = 0xA50;
    // m_angEyeAngles (QAngle) — written each replay tick alongside v_angle
    inline int kPawn_EyeAngles = 0x13B8;

    // ---- CCSPlayer_ItemServices / CCSPlayerController ----

    inline int kItemServices_HasHelmet = 0;
    inline int kItemServices_HasDefuser = 0;
    inline int kController_PawnArmor = 0;
    inline int kController_PawnHasHelmet = 0;
    inline int kController_PawnHasDefuser = 0;

    // ---- BuyState ----

    // m_isInitialDelay; rising edge each round = freshly entered BuyState
    inline int kBuy_InitialDelay = 0x08;
    // m_doneBuying; set 1 to make vanilla skip the rest of buying
    inline int kBuy_DoneBuying = 0x18;

    // ---- CCSPlayer_WeaponServices ----

    // m_hActiveWeapon (CHandle)
    inline int kWs_ActiveWeapon = 0x60;

    // ---- CBasePlayerWeapon ----

    // m_AttributeManager(0x958) -> m_Item(0x50) -> m_iItemDefinitionIndex(0x38),
    // all embedded; net direct add (no deref)
    inline int kWeapon_ItemDefIndex = 0x958 + 0x50 + 0x38; // 0x9E0

    // ---- CCSPlayer_MovementServices ----

    // CPlayerPawnComponent::Pawn pointer helper used by CounterStrikeSharp.
    inline int kServices_Pawn = 0x38;
    // m_nButtons.m_pButtonStates[0..2] — engine button state block (CInButtonState)
    inline int kServices_Buttons = 0x50 + 0x08;       // states[0] (pressed)
    inline int kServices_Buttons1 = 0x50 + 0x08 + 8;  // states[1]
    inline int kServices_Buttons2 = 0x50 + 0x08 + 16; // states[2]
    // m_vecOldViewAngles (QAngle)
    inline int kServices_OldViewAngles = 0x240;

    // duck/ladder state
    inline int kServices_LadderNormal = 0x3D0; // Vector m_vecLadderNormal
    inline int kServices_Ducked = 0x3E0;       // bool m_bDucked
    inline int kServices_DuckAmount = 0x3E4;   // float m_flDuckAmount
    inline int kServices_DuckSpeed = 0x3E8;    // float m_flDuckSpeed
    inline int kServices_DesiresDuck = 0x3ED;  // bool m_bDesiresDuck
    inline int kServices_Ducking = 0x3EE;      // bool m_bDucking

    // ---- CMoveData  ----

    // m_flForwardMove / m_flSideMove / m_flUpMove — movement input axes.
    inline int kMove_ForwardMove = 44;
    inline int kMove_SideMove = 48;
    inline int kMove_UpMove = 52;
    // m_vecVelocity — the velocity TryPlayerMove integrates into origin
    inline int kMove_Velocity = 56;
    // m_vecAbsOrigin — post-move origin written here before FinishMove commits
    inline int kMove_AbsOrigin = 200;

    // ---- vtable indices (CCSPlayer_MovementServices) ----

    inline int kVtIdx_PlayerRunCommand = 25;
    inline int kVtIdx_FinishMove = 38;

    // Load only private/non-schema offsets from platform-specific gamedata.
    void LoadFromGamedata(const nlohmann::json &gd);

    // Replace every schema-backed offset with the live server layout. Returns
    // false rather than allowing stale offsets to write unrelated fields.
    bool LoadFromSchema(char *errorOut, std::size_t errorOutLen);

} // namespace BotController::targets
