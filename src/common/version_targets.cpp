// Override structure offsets from gamedata.json (platform-aware)

#include "version_targets.h"
#include "sig_scan.h"

namespace BotController::targets
{
    // Each offset: gamedata[name].offsets[platform], else keep code default
    void LoadFromGamedata(const nlohmann::json &gd)
    {
        kBot_AiTickedFlag        = Sig::FindPlatformOffset(gd, "CCSBot::AiTickedFlag", kBot_AiTickedFlag);
        kBot_Pawn                = Sig::FindPlatformOffset(gd, "CCSBot::Pawn", kBot_Pawn);
        kBot_Enemy               = Sig::FindPlatformOffset(gd, "CCSBot::Enemy", kBot_Enemy);
        kBot_IsEnemyVisible      = Sig::FindPlatformOffset(gd, "CCSBot::IsEnemyVisible", kBot_IsEnemyVisible);
        kBot_VisibleEnemyParts   = Sig::FindPlatformOffset(gd, "CCSBot::VisibleEnemyParts", kBot_VisibleEnemyParts);
        kBot_LastSawEnemyTimestamp = Sig::FindPlatformOffset(gd, "CCSBot::LastSawEnemyTimestamp", kBot_LastSawEnemyTimestamp);
        kBot_FirstSawEnemyTimestamp = Sig::FindPlatformOffset(gd, "CCSBot::FirstSawEnemyTimestamp", kBot_FirstSawEnemyTimestamp);
        kBot_CurrentEnemyAcquireTimestamp = Sig::FindPlatformOffset(gd, "CCSBot::CurrentEnemyAcquireTimestamp", kBot_CurrentEnemyAcquireTimestamp);
        kBot_IsLastEnemyDead     = Sig::FindPlatformOffset(gd, "CCSBot::IsLastEnemyDead", kBot_IsLastEnemyDead);
        kBot_NearbyEnemyCount    = Sig::FindPlatformOffset(gd, "CCSBot::NearbyEnemyCount", kBot_NearbyEnemyCount);
        kEnt_Identity            = Sig::FindPlatformOffset(gd, "CBaseEntity::Identity", kEnt_Identity);
        kEntIdentity_EHandle     = Sig::FindPlatformOffset(gd, "CEntityIdentity::EHandle", kEntIdentity_EHandle);
        kEnt_MoveType            = Sig::FindPlatformOffset(gd, "CBaseEntity::MoveType", kEnt_MoveType);
        kEnt_ActualMoveType      = Sig::FindPlatformOffset(gd, "CBaseEntity::ActualMoveType", kEnt_ActualMoveType);
        kEnt_Flags               = Sig::FindPlatformOffset(gd, "CBaseEntity::Flags", kEnt_Flags);
        kEnt_AbsVelocity         = Sig::FindPlatformOffset(gd, "CBaseEntity::AbsVelocity", kEnt_AbsVelocity);
        kEnt_BodyComponent       = Sig::FindPlatformOffset(gd, "CBaseEntity::BodyComponent", kEnt_BodyComponent);
        kBody_SceneNode          = Sig::FindPlatformOffset(gd, "CBodyComponent::SceneNode", kBody_SceneNode);
        kEnt_GameSceneNode       = Sig::FindPlatformOffset(gd, "CBaseEntity::GameSceneNode", kEnt_GameSceneNode);
        kNode_AbsOrigin          = Sig::FindPlatformOffset(gd, "CGameSceneNode::AbsOrigin", kNode_AbsOrigin);
        kPawn_WeaponServices     = Sig::FindPlatformOffset(gd, "CCSPlayerPawn::WeaponServices", kPawn_WeaponServices);
        kPawn_MovementServices   = Sig::FindPlatformOffset(gd, "CBasePlayerPawn::MovementServices", kPawn_MovementServices);
        kPawn_Controller         = Sig::FindPlatformOffset(gd, "CCSPlayerPawn::Controller", kPawn_Controller);
        kPawn_OriginalController = Sig::FindPlatformOffset(gd, "CCSPlayerPawn::OriginalController", kPawn_OriginalController);
        kPawn_ViewAngle          = Sig::FindPlatformOffset(gd, "CCSPlayerPawn::ViewAngle", kPawn_ViewAngle);
        kPawn_ViewAnglePrevious  = Sig::FindPlatformOffset(gd, "CCSPlayerPawn::ViewAnglePrevious", kPawn_ViewAnglePrevious);
        kPawn_ServerViewAngleChanges = Sig::FindPlatformOffset(gd, "CCSPlayerPawn::ServerViewAngleChanges", kPawn_ServerViewAngleChanges);
        kPawn_EyeAngles          = Sig::FindPlatformOffset(gd, "CCSPlayerPawn::EyeAngles", kPawn_EyeAngles);
        kBuy_InitialDelay        = Sig::FindPlatformOffset(gd, "BuyState::InitialDelay", kBuy_InitialDelay);
        kBuy_DoneBuying          = Sig::FindPlatformOffset(gd, "BuyState::DoneBuying", kBuy_DoneBuying);
        kWs_ActiveWeapon         = Sig::FindPlatformOffset(gd, "CCSPlayer_WeaponServices::ActiveWeapon", kWs_ActiveWeapon);
        kWeapon_ItemDefIndex     = Sig::FindPlatformOffset(gd, "CBasePlayerWeapon::ItemDefIndex", kWeapon_ItemDefIndex);
        kServices_Pawn           = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::Pawn", kServices_Pawn);
        kServices_Buttons        = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::Buttons", kServices_Buttons);
        kServices_Buttons1       = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::Buttons1", kServices_Buttons1);
        kServices_Buttons2       = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::Buttons2", kServices_Buttons2);
        kServices_OldViewAngles  = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::OldViewAngles", kServices_OldViewAngles);
        kServices_LadderNormal   = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::LadderNormal", kServices_LadderNormal);
        kServices_Ducked         = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::Ducked", kServices_Ducked);
        kServices_DuckAmount     = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::DuckAmount", kServices_DuckAmount);
        kServices_DuckSpeed      = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::DuckSpeed", kServices_DuckSpeed);
        kServices_DesiresDuck    = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::DesiresDuck", kServices_DesiresDuck);
        kServices_Ducking        = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::Ducking", kServices_Ducking);
        kMove_ForwardMove        = Sig::FindPlatformOffset(gd, "CMoveData::ForwardMove", kMove_ForwardMove);
        kMove_SideMove           = Sig::FindPlatformOffset(gd, "CMoveData::SideMove", kMove_SideMove);
        kMove_UpMove             = Sig::FindPlatformOffset(gd, "CMoveData::UpMove", kMove_UpMove);
        kMove_Velocity           = Sig::FindPlatformOffset(gd, "CMoveData::Velocity", kMove_Velocity);
        kMove_AbsOrigin          = Sig::FindPlatformOffset(gd, "CMoveData::AbsOrigin", kMove_AbsOrigin);
        kVtIdx_PlayerRunCommand  = Sig::FindPlatformOffset(gd, "vtidx::PlayerRunCommand", kVtIdx_PlayerRunCommand);
        kVtIdx_FinishMove        = Sig::FindPlatformOffset(gd, "vtidx::FinishMove", kVtIdx_FinishMove);
    }
}
