/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class DtrRoundBannerPlaybackTests
{
    [Fact]
    public void SendsOnEveryTickForFiveSeconds()
    {
        var playback = new DtrRoundBannerPlayback(
            [new DtrRoundBannerRecipient(2, 20), new DtrRoundBannerRecipient(7, 70)]);
        var sends = 0;

        while (playback.TryBeginTick())
            sends++;

        Assert.Equal(320, sends);
        Assert.Equal(DemoTracerPlugin.DtrRoundBannerTotalTicks, playback.SentTicks);
        Assert.True(playback.IsComplete);
        Assert.False(playback.TryBeginTick());
    }

    [Fact]
    public void UsesPinnedDtrAssetAndStableRecipientSnapshot()
    {
        var playback = new DtrRoundBannerPlayback(
            [
                new DtrRoundBannerRecipient(7, 70),
                new DtrRoundBannerRecipient(2, 20),
                new DtrRoundBannerRecipient(7, 700),
            ]);

        Assert.Equal([2, 7], playback.Recipients.Select(recipient => recipient.Slot));
        Assert.Equal([20, 70], playback.Recipients.Select(recipient => recipient.UserId));
        Assert.Equal(5, DemoTracerPlugin.DtrRoundBannerDurationSeconds);
        Assert.Contains("@c999941/", DemoTracerPlugin.DtrRoundBannerImageUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://cdn.jsdelivr.net/gh/", DemoTracerPlugin.DtrRoundBannerImageUrl, StringComparison.Ordinal);
    }

}
