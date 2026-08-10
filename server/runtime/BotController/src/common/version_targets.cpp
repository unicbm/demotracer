// Resolve structure offsets from live SchemaSystem and private gamedata.

#include "version_targets.h"
#include "schema_resolver.h"
#include "sig_scan.h"

#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

namespace BotController::targets
{
    // Only genuinely private/non-schema layout remains in gamedata.
    void LoadFromGamedata(const nlohmann::json &gd)
    {
        kEntIdentity_EHandle     = Sig::FindPlatformOffset(gd, "CEntityIdentity::EHandle", kEntIdentity_EHandle);
        kBuy_InitialDelay        = Sig::FindPlatformOffset(gd, "BuyState::InitialDelay", kBuy_InitialDelay);
        kBuy_DoneBuying          = Sig::FindPlatformOffset(gd, "BuyState::DoneBuying", kBuy_DoneBuying);
        kServices_Pawn           = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::Pawn", kServices_Pawn);
        kMove_ForwardMove        = Sig::FindPlatformOffset(gd, "CMoveData::ForwardMove", kMove_ForwardMove);
        kMove_SideMove           = Sig::FindPlatformOffset(gd, "CMoveData::SideMove", kMove_SideMove);
        kMove_UpMove             = Sig::FindPlatformOffset(gd, "CMoveData::UpMove", kMove_UpMove);
        kMove_Velocity           = Sig::FindPlatformOffset(gd, "CMoveData::Velocity", kMove_Velocity);
        kMove_AbsOrigin          = Sig::FindPlatformOffset(gd, "CMoveData::AbsOrigin", kMove_AbsOrigin);
        kVtIdx_PlayerRunCommand  = Sig::FindPlatformOffset(gd, "vtidx::PlayerRunCommand", kVtIdx_PlayerRunCommand);
        kVtIdx_FinishMove        = Sig::FindPlatformOffset(gd, "vtidx::FinishMove", kVtIdx_FinishMove);
    }

    bool LoadFromSchema(char *errorOut, std::size_t errorOutLen)
    {
        struct RequiredField
        {
            const char *className;
            const char *fieldName;
            int *destination;
        };

        const RequiredField fields[] = {
            {"CBot", "m_pPlayer", &kBot_Pawn},
            {"CCSBot", "m_enemy", &kBot_Enemy},
            {"CCSBot", "m_isEnemyVisible", &kBot_IsEnemyVisible},
            {"CCSBot", "m_visibleEnemyParts", &kBot_VisibleEnemyParts},
            {"CCSBot", "m_lastSawEnemyTimestamp", &kBot_LastSawEnemyTimestamp},
            {"CCSBot", "m_firstSawEnemyTimestamp", &kBot_FirstSawEnemyTimestamp},
            {"CCSBot", "m_currentEnemyAcquireTimestamp", &kBot_CurrentEnemyAcquireTimestamp},
            {"CCSBot", "m_isLastEnemyDead", &kBot_IsLastEnemyDead},
            {"CCSBot", "m_nearbyEnemyCount", &kBot_NearbyEnemyCount},

            {"CEntityInstance", "m_pEntity", &kEnt_Identity},
            {"CBaseEntity", "m_MoveType", &kEnt_MoveType},
            {"CBaseEntity", "m_nActualMoveType", &kEnt_ActualMoveType},
            {"CBaseEntity", "m_fFlags", &kEnt_Flags},
            {"CBaseEntity", "m_vecAbsVelocity", &kEnt_AbsVelocity},
            {"CBaseEntity", "m_CBodyComponent", &kEnt_BodyComponent},
            {"CBodyComponent", "m_pSceneNode", &kBody_SceneNode},
            {"CGameSceneNode", "m_vecAbsOrigin", &kNode_AbsOrigin},

            {"CBasePlayerPawn", "m_pWeaponServices", &kPawn_WeaponServices},
            {"CBasePlayerPawn", "m_pMovementServices", &kPawn_MovementServices},
            {"CBasePlayerPawn", "m_pItemServices", &kPawn_ItemServices},
            {"CBasePlayerPawn", "m_hController", &kPawn_Controller},
            {"CCSPlayerPawnBase", "m_hOriginalController", &kPawn_OriginalController},
            {"CBasePlayerPawn", "v_angle", &kPawn_ViewAngle},
            {"CBasePlayerPawn", "v_anglePrevious", &kPawn_ViewAnglePrevious},
            {"CBasePlayerPawn", "m_ServerViewAngleChanges", &kPawn_ServerViewAngleChanges},
            {"CCSPlayerPawn", "m_angEyeAngles", &kPawn_EyeAngles},
            {"CCSPlayerPawn", "m_ArmorValue", &kPawn_ArmorValue},

            {"CCSPlayer_ItemServices", "m_bHasHelmet", &kItemServices_HasHelmet},
            {"CCSPlayer_ItemServices", "m_bHasDefuser", &kItemServices_HasDefuser},
            {"CCSPlayerController", "m_iPawnArmor", &kController_PawnArmor},
            {"CCSPlayerController", "m_bPawnHasHelmet", &kController_PawnHasHelmet},
            {"CCSPlayerController", "m_bPawnHasDefuser", &kController_PawnHasDefuser},

            {"CPlayer_WeaponServices", "m_hActiveWeapon", &kWs_ActiveWeapon},
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

        const int buttonState =
            Schema::GetFieldOffset("CPlayer_MovementServices", "m_nButtons");
        const int buttonStates =
            Schema::GetFieldOffset("CInButtonState", "m_pButtonStates");
        if (buttonState < 0)
            missing.emplace_back("CPlayer_MovementServices::m_nButtons");
        if (buttonStates < 0)
            missing.emplace_back("CInButtonState::m_pButtonStates");

        if (!missing.empty())
        {
            std::string message = "Missing required Schema field";
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

        kServices_Buttons = buttonState + buttonStates;
        kServices_Buttons1 =
            kServices_Buttons + static_cast<int>(sizeof(std::uint64_t));
        kServices_Buttons2 =
            kServices_Buttons1 + static_cast<int>(sizeof(std::uint64_t));
        kWeapon_ItemDefIndex = attributeManager + item + itemDefinition;
        return true;
    }
}
