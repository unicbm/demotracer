/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using DemoTracer;

namespace DemoTracer.Tests;

public sealed class NativePerceptionHandoffTests
{
    [Fact]
    public void StaleVisibleEnemyCannotTriggerHandoff()
    {
        var state = VisibleEnemy(updateSerial: 41);

        Assert.False(DemoTracerPlugin.IsFreshNativeVisibleEnemy(state, baselineSerial: 41));
    }

    [Fact]
    public void FreshVisibleLiveEnemyCanTriggerHandoff()
    {
        var state = VisibleEnemy(updateSerial: 42);

        Assert.True(DemoTracerPlugin.IsFreshNativeVisibleEnemy(state, baselineSerial: 41));
    }

    [Fact]
    public void FreshUpdateWithoutCurrentVisibilityCannotTriggerHandoff()
    {
        var state = VisibleEnemy(updateSerial: 42);
        state.EnemyVisible = 0;

        Assert.False(DemoTracerPlugin.IsFreshNativeVisibleEnemy(state, baselineSerial: 41));
    }

    [Fact]
    public void DeadEnemyCannotTriggerHandoff()
    {
        var state = VisibleEnemy(updateSerial: 42);
        state.LastEnemyDead = 1;

        Assert.False(DemoTracerPlugin.IsFreshNativeVisibleEnemy(state, baselineSerial: 41));
    }

    [Fact]
    public void SerialWrapStillAcceptsFreshUpdate()
    {
        var state = VisibleEnemy(updateSerial: 0);

        Assert.True(DemoTracerPlugin.IsFreshNativeVisibleEnemy(state, uint.MaxValue));
    }

    private static NativePerceptionState VisibleEnemy(uint updateSerial)
    {
        return new NativePerceptionState
        {
            Valid = 1,
            EnemyHandle = 123,
            HasEnemy = 1,
            EnemyVisible = 1,
            VisibleEnemyParts = 3,
            LastEnemyDead = 0,
            UpdateSerial = updateSerial
        };
    }
}
