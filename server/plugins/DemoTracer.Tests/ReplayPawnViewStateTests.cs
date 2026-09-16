/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using static DemoTracer.DemoTracerPlugin;

namespace DemoTracer.Tests;

public sealed class ReplayPawnViewStateTests
{
    private static readonly ReplayViewOwner Owner = new(0x8001, 0x800a, 0x1000);

    [Theory]
    [InlineData(0x10001, 0x800a, 0x1000)]
    [InlineData(0x8001, 0x1000a, 0x1000)]
    [InlineData(0x8001, 0x800a, 0x2000)]
    public void OldSnapshotCannotRestoreAcrossControllerOrPawnReplacement(uint controller, uint pawn, int address)
    {
        var state = new ReplayPawnViewState(Owner);
        state.Capture(new() { Fov = 60 }, new() { Fov = 68 });
        Assert.Null(state.GetRestore(new(controller, pawn, address), new() { Fov = 68 }));
    }

    [Fact]
    public void PartialEvidenceOnlyAcquiresItsOwnFieldsAndNeverHandOutput()
    {
        var state = new ReplayPawnViewState(Owner);
        state.Capture(new() { Fov = 60, OffsetX = 1, OffsetY = 2, OffsetZ = 3, LeftHanded = false },
            new() { OffsetX = 2.5f, LeftHanded = true });
        var restore = Assert.IsType<ReplayViewmodel>(state.GetRestore(Owner,
            new() { Fov = 70, OffsetX = 2.5f, OffsetY = 4, OffsetZ = 5, LeftHanded = true }));
        Assert.Equal(1, restore.OffsetX);
        Assert.Null(restore.Fov);
        Assert.Null(restore.OffsetY);
        Assert.Null(restore.OffsetZ);
        Assert.Null(restore.LeftHanded);
    }

    [Fact]
    public void LaterEvidenceCapturesNewFieldsWithoutLosingOriginalValues()
    {
        var state = new ReplayPawnViewState(Owner);
        state.Capture(new() { Fov = 60, OffsetX = 1 }, new() { Fov = 68 });
        state.Capture(new() { Fov = 68, OffsetX = 2 }, new() { Fov = 70, OffsetX = 3 });
        var restore = Assert.IsType<ReplayViewmodel>(state.GetRestore(Owner, new() { Fov = 70, OffsetX = 3 }));
        Assert.Equal(60, restore.Fov);
        Assert.Equal(2, restore.OffsetX);
    }

    [Fact]
    public void ReleasePreservesLaterExternalChanges()
    {
        var state = new ReplayPawnViewState(Owner);
        state.Capture(new() { Fov = 60, OffsetX = 1 }, new() { Fov = 68, OffsetX = 3 });
        var restore = Assert.IsType<ReplayViewmodel>(state.GetRestore(Owner, new() { Fov = 65, OffsetX = 3 }));
        Assert.Null(restore.Fov);
        Assert.Equal(1, restore.OffsetX);
    }
}
