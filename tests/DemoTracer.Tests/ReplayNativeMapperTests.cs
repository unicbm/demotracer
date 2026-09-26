/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplayNativeMapperTests
{
    [Fact]
    public void InventoryPlanningKeepsFirstAppearanceOrderWithoutPerTickDuplicates()
    {
        var replay = new DtrReplayFile(11,
            new[] { -1, 7, 7, 43, 7, 43, 9 }.Select(def => new NativeReplayTick { WeaponDefIndex = def }).ToArray(),
            [], ReplayHighFidelityMetadata.Empty, [], [], [], [], [], 64, 2);
        var metadata = ReplayNativeMapper.BuildMetadata(replay);
        Assert.Equal(new[] { -1, 7, 43, 9 }, metadata.WeaponDefIndices);
        Assert.Equal(7, metadata.TickCount);
        Assert.Equal(2U, metadata.PlayStartTickIndex);
        var prepared = replay with { PreparedMetadata = metadata };
        Assert.Same(metadata.WeaponDefIndices, ReplayNativeMapper.BuildMetadata(prepared).WeaponDefIndices);
    }

    [Fact]
    public void PrefetchBudgetIncludesSourceStateAndRetainedInputHistory()
    {
        var empty = new DtrReplayFile(11, [], [], ReplayHighFidelityMetadata.Empty, [], [], [], [], [], 64, 0);
        var replay = empty with
        {
            SourceState = new NativeReplaySourceStateChange[10],
            InputHistoryTicks = new NativeReplayInputHistoryTick[2],
            InputHistoryEntries = new NativeReplayInputHistoryEntry[3],
        };
        Assert.Equal(10 * 16 + 2 * 16 + 3 * 128, DtrReplayPrefetch.EstimateReplayBytes(replay));
    }
}
