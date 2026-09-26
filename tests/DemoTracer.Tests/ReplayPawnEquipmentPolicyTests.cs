/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplayPawnEquipmentPolicyTests
{
    [Fact]
    public void RequiresPawnItemServicesAndControllerMirrorsToMatch()
    {
        Assert.True(ReplayRuntimePolicy.PawnEquipmentStateMatches(
            expectedArmor: 100,
            expectedHelmet: true,
            expectedDefuser: true,
            pawnArmor: 100,
            itemServicesAvailable: true,
            itemServicesHelmet: true,
            itemServicesDefuser: true,
            controllerArmor: 100,
            controllerHelmet: true,
            controllerDefuser: true));

        Assert.False(ReplayRuntimePolicy.PawnEquipmentStateMatches(
            expectedArmor: 100,
            expectedHelmet: true,
            expectedDefuser: true,
            pawnArmor: 100,
            itemServicesAvailable: true,
            itemServicesHelmet: true,
            itemServicesDefuser: true,
            controllerArmor: 0,
            controllerHelmet: false,
            controllerDefuser: false));
    }
}
