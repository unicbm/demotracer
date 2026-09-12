/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotControllerImpl;

namespace DemoTracer.Tests;

public sealed class PublicMotionRecordingTests
{
    [Fact]
    public void MotionJsonWithoutExperimentalEventsPreservesItsData()
    {
        var recording = LoadJson("""
            {"Tickrate":64,"Ticks":[{"WeaponDefIndex":7,"Pre":{"OriginX":128.25}}],
             "Subticks":[],"Commands":[{"ForwardMove":0.5}]}
            """);
        Assert.Single(recording.Ticks);
        Assert.Equal(7, recording.Ticks[0].WeaponDefIndex);
        Assert.Equal(128.25f, recording.Ticks[0].Pre.OriginX);
        Assert.Equal(0.5f, Assert.Single(recording.Commands).ForwardMove);
        Assert.Equal(0u, recording.Ticks[0].EventFlags);
    }

    [Theory]
    [InlineData("EventFlags")]
    [InlineData("EventWeaponDefIndex")]
    [InlineData("EventDropVectorFlags")]
    [InlineData("EventDropTargetX")]
    [InlineData("EventDropTargetY")]
    [InlineData("EventDropTargetZ")]
    [InlineData("EventDropVelocityX")]
    [InlineData("EventDropVelocityY")]
    [InlineData("EventDropVelocityZ")]
    public void UnsupportedEventPayloadReportsItsTickBeforeNativeLoad(string field)
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            LoadJson($$"""{"Ticks":[{}, {"{{field}}":1}],"Subticks":[]}"""));
        Assert.Contains("weapon-drop events are unsupported (tick 1)", exception.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"Ticks\":null,\"Subticks\":[]}")]
    [InlineData("{\"Ticks\":[],\"Subticks\":null}")]
    public void NullRecordingDataIsRejectedBeforeNativeLoad(string json)
        => Assert.Throws<InvalidDataException>(() => LoadJson(json));

    private static MotionRecording LoadJson(string json)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            return MotionStore.LoadFromFile(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
