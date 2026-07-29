// version_targets.h

#pragma once

#include <nlohmann/json.hpp>

namespace BotController::targets
{
    // ---- CCSBot ----

    // AI-ran-this-tick byte flag; set to 1 to fake a completed tick
    inline int kBot_AiTickedFlag = 0x610;
    // CCSBot -> pawn (CCSPlayerPawn*)
    inline int kBot_Pawn = 0x18;
    // Native perception state. These are server-only CCSBot fields recovered
    // from the current server.dll field-description table and vision code.
    inline int kBot_Enemy = 0x5A08;                    // CHandle<CCSPlayerPawn>
    inline int kBot_IsEnemyVisible = 0x5A0C;           // bool
    inline int kBot_VisibleEnemyParts = 0x5A0D;        // uint8 bit mask
    inline int kBot_LastSawEnemyTimestamp = 0x5A1C;    // float
    inline int kBot_FirstSawEnemyTimestamp = 0x5A20;   // float
    inline int kBot_CurrentEnemyAcquireTimestamp = 0x5A24; // float
    inline int kBot_IsLastEnemyDead = 0x5A30;          // bool
    inline int kBot_NearbyEnemyCount = 0x5A34;         // int32

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
    inline int kPawn_WeaponServices = 0xA30;
    // m_pMovementServices
    inline int kPawn_MovementServices = 0xA70;
    // m_hController (CHandle)
    inline int kPawn_Controller = 0xBB0;
    // m_hOriginalController (CHandle)
    inline int kPawn_OriginalController = 0xD24;
    // CCSPlayerPawn -> v_angle (QAngle)
    inline int kPawn_ViewAngle = 0xAE8;
    // v_anglePrevious (QAngle) — keep first-person spectator/camera history aligned
    inline int kPawn_ViewAnglePrevious = 0xAF4;
    // m_ServerViewAngleChanges — embedded network vector consumed by local/observer camera view.
    inline int kPawn_ServerViewAngleChanges = 0xA80;
    // m_angEyeAngles (QAngle) — written each replay tick alongside v_angle
    inline int kPawn_EyeAngles = 0x1368;

    // ---- BuyState ----

    // m_isInitialDelay; rising edge each round = freshly entered BuyState
    inline int kBuy_InitialDelay = 0x08;
    // m_doneBuying; set 1 to make vanilla skip the rest of buying
    inline int kBuy_DoneBuying = 0x18;

    // ---- CCSPlayer_WeaponServices ----

    // m_hActiveWeapon (CHandle)
    inline int kWs_ActiveWeapon = 0x60;

    // ---- CBasePlayerWeapon ----

    // m_AttributeManager(0x978) -> m_Item(0x50) -> m_iItemDefinitionIndex(0x38),
    // all embedded; net direct add (no deref)
    inline int kWeapon_ItemDefIndex = 0x978 + 0x50 + 0x38; // 0xA00

    // ---- CCSPlayer_MovementServices ----

    // CPlayerPawnComponent::Pawn pointer helper used by CounterStrikeSharp.
    inline int kServices_Pawn = 0x38;
    // m_nButtons.m_pButtonStates[0..2] — engine button state block (CInButtonState)
    inline int kServices_Buttons = 0x58;       // states[0] (pressed)
    inline int kServices_Buttons1 = 0x58 + 8;  // states[1]
    inline int kServices_Buttons2 = 0x58 + 16; // states[2]
    // m_vecOldViewAngles (QAngle)
    inline int kServices_OldViewAngles = 0x240;

    // duck/ladder state
    inline int kServices_LadderNormal = 0x3F8; // Vector m_vecLadderNormal
    inline int kServices_Ducked = 0x408;       // bool m_bDucked
    inline int kServices_DuckAmount = 0x40C;   // float m_flDuckAmount
    inline int kServices_DuckSpeed = 0x410;    // float m_flDuckSpeed
    inline int kServices_DesiresDuck = 0x415;  // bool m_bDesiresDuck
    inline int kServices_Ducking = 0x416;      // bool m_bDucking

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

    // Override the above from gamedata[name].offsets[platform]; missing keeps default
    void LoadFromGamedata(const nlohmann::json &gd);

} // namespace BotController::targets
