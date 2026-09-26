// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// Resolve the live schema-backed entity -> body component -> scene node chain.
#pragma once

#include "ccsbot_slot.h"
#include "version_targets.h"

namespace BotController
{
    inline void *SceneNodeForEntity(const void *entity)
    {
        void *body = nullptr, *node = nullptr;
        return entity && targets::kEnt_BodyComponent > 0 && targets::kBody_SceneNode >= 0 &&
            SafeRead(entity, targets::kEnt_BodyComponent, body) && body &&
            SafeRead(body, targets::kBody_SceneNode, node) ? node : nullptr;
    }
}
