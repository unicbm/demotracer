// Override structure offsets from gamedata.json (platform-aware)

#include "version_targets.h"
#include "schema_resolver.h"
#include "sig_scan.h"

#include <cstdio>
#include <string>
#include <vector>

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

    bool ResolveRuntimeSchemaOffsets(char *errorOut, std::size_t errorOutLen)
    {
        if (!Schema::Init())
        {
            std::snprintf(errorOut, errorOutLen,
                          "SchemaSystem unavailable; refusing unsafe raw field offsets");
            return false;
        }

        struct RequiredField
        {
            const char *className;
            const char *fieldName;
            int *destination;
        };

        const RequiredField fields[] = {
            {"CCSBot", "m_enemy", &kBot_Enemy},
            {"CCSBot", "m_isEnemyVisible", &kBot_IsEnemyVisible},
            {"CCSBot", "m_visibleEnemyParts", &kBot_VisibleEnemyParts},
            {"CCSBot", "m_lastSawEnemyTimestamp", &kBot_LastSawEnemyTimestamp},
            {"CCSBot", "m_firstSawEnemyTimestamp", &kBot_FirstSawEnemyTimestamp},
            {"CCSBot", "m_currentEnemyAcquireTimestamp", &kBot_CurrentEnemyAcquireTimestamp},
            {"CCSBot", "m_isLastEnemyDead", &kBot_IsLastEnemyDead},
            {"CCSBot", "m_nearbyEnemyCount", &kBot_NearbyEnemyCount},

            {"CBaseEntity", "m_MoveType", &kEnt_MoveType},
            {"CBaseEntity", "m_nActualMoveType", &kEnt_ActualMoveType},
            {"CBaseEntity", "m_fFlags", &kEnt_Flags},
            {"CBaseEntity", "m_vecAbsVelocity", &kEnt_AbsVelocity},
            {"CBaseEntity", "m_CBodyComponent", &kEnt_BodyComponent},
            {"CBodyComponent", "m_pSceneNode", &kBody_SceneNode},
            {"CGameSceneNode", "m_vecAbsOrigin", &kNode_AbsOrigin},

            {"CBasePlayerPawn", "m_pWeaponServices", &kPawn_WeaponServices},
            {"CBasePlayerPawn", "m_pMovementServices", &kPawn_MovementServices},
            {"CBasePlayerPawn", "m_hController", &kPawn_Controller},
            {"CCSPlayerPawnBase", "m_hOriginalController", &kPawn_OriginalController},
            {"CBasePlayerPawn", "v_angle", &kPawn_ViewAngle},
            {"CBasePlayerPawn", "v_anglePrevious", &kPawn_ViewAnglePrevious},
            {"CBasePlayerPawn", "m_ServerViewAngleChanges", &kPawn_ServerViewAngleChanges},
            {"CCSPlayerPawn", "m_angEyeAngles", &kPawn_EyeAngles},

            {"CPlayer_WeaponServices", "m_hActiveWeapon", &kWs_ActiveWeapon},
            {"CPlayer_MovementServices", "m_nButtons", &kServices_Buttons},
            {"CPlayer_MovementServices", "m_vecOldViewAngles", &kServices_OldViewAngles},
            {"CCSPlayer_MovementServices", "m_vecLadderNormal", &kServices_LadderNormal},
            {"CCSPlayer_MovementServices", "m_bDucked", &kServices_Ducked},
            {"CCSPlayer_MovementServices", "m_flDuckAmount", &kServices_DuckAmount},
            {"CCSPlayer_MovementServices", "m_flDuckSpeed", &kServices_DuckSpeed},
            {"CCSPlayer_MovementServices", "m_bDesiresDuck", &kServices_DesiresDuck},
            {"CCSPlayer_MovementServices", "m_bDucking", &kServices_Ducking},
        };

        std::vector<std::string> missing;
        for (const RequiredField &field : fields)
        {
            const int offset = Schema::GetFieldOffset(field.className, field.fieldName);
            if (offset < 0)
            {
                missing.emplace_back(std::string(field.className) + "::" + field.fieldName);
                continue;
            }
            *field.destination = offset;
        }

        const int attributeManager =
            Schema::GetFieldOffset("CEconEntity", "m_AttributeManager");
        const int item = Schema::GetFieldOffset("CAttributeContainer", "m_Item");
        const int itemDefinition =
            Schema::GetFieldOffset("CEconItemView", "m_iItemDefinitionIndex");
        if (attributeManager < 0)
            missing.emplace_back("CEconEntity::m_AttributeManager");
        if (item < 0)
            missing.emplace_back("CAttributeContainer::m_Item");
        if (itemDefinition < 0)
            missing.emplace_back("CEconItemView::m_iItemDefinitionIndex");

        if (!missing.empty())
        {
            std::string message = "Missing required server schema field";
            if (missing.size() != 1)
                message += "s";
            message += ": ";
            for (std::size_t i = 0; i < missing.size(); ++i)
            {
                if (i != 0)
                    message += ", ";
                message += missing[i];
            }
            std::snprintf(errorOut, errorOutLen, "%s", message.c_str());
            return false;
        }

        kServices_Buttons1 = kServices_Buttons + 8;
        kServices_Buttons2 = kServices_Buttons + 16;
        kWeapon_ItemDefIndex = attributeManager + item + itemDefinition;
        return true;
    }
}
