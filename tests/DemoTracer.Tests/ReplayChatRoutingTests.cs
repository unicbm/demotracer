/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplayChatRoutingTests
{
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
