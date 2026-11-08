/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplayChatRoutingTests
{
    [Fact]
    public void ShortPrerollSkipsEarlierMessagesAndLiveKeepsItsCursor()
    {
        var messages = new[] { 1000, 1050, 1280 }.Select(tick =>
            new DemoTracerPlugin.ReplayChatMessage { Tick = tick, Text = "hello" });
        var frames = DemoTracerPlugin.BuildChatPlaybackFrames(messages, 1024, 64);
        Assert.Equal(new[] { 1050, 1280 }, frames.Select(frame => frame.DemoTick));
        var playback = new DemoTracerPlugin.ChatPlaybackState(0, 1024, frames) { NextFrameIndex = 1 };
        playback.AlignClock(10, 1280, 64);
        var next = playback.Frames[playback.NextFrameIndex];
        Assert.Equal(1280, next.DemoTick);
        Assert.Equal(10f - playback.StartTime, next.PlaybackSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PlayerMessagesRemainInThePlayerChatbox(int scopeValue)
    {
        var scope = (DemoTracerPlugin.ReplayChatScope)scopeValue;

        Assert.Equal(
            DemoTracerPlugin.ReplayChatOutput.PlayerChatCommand,
            DemoTracerPlugin.ReplayChatOutputFor(scope));
    }

    [Fact]
    public void ServerMessagesBroadcastRawTextToTheChatbox()
    {
        Assert.Equal(
            DemoTracerPlugin.ReplayChatOutput.ServerChatboxBroadcast,
            DemoTracerPlugin.ReplayChatOutputFor(DemoTracerPlugin.ReplayChatScope.Server));
    }

    [Theory]
    [InlineData("server")]
    [InlineData("admin")]
    public void ServerAndAdminEvidenceNormalizeToTheServerRoute(string scope)
    {
        Assert.Equal(
            DemoTracerPlugin.ReplayChatScope.Server,
            DemoTracerPlugin.NormalizeReplayChatScope(scope));
    }

    [Fact]
    public void SanitizedServerTextDoesNotGainAReplayPrefixOrSpeaker()
    {
        Assert.Equal(
            "ESL Console: Match paused",
            DemoTracerPlugin.SanitizeReplayChatText("  ESL Console:  Match paused  "));
    }
}
