/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using DtrHider;
using DtrHiderApi;

namespace DemoTracer.Tests;

public sealed class BotHiderCrosshairPresentationTests
{
    [Fact]
    public void CsPrefixedCodeIsPublishedWithoutReencodingOrTruncation()
    {
        const string code = "CSG4pWURDBtO7JeYvrNjoewqFQ9rGdZuDRmzyy5QLDFNrh";
        Assert.True(DtrHiderContract.TryNormalizeCrosshairCode("  " + code + "  ", out var normalized));
        Assert.Equal(code, normalized);
        var actual = "CSGO-previous";
        var notifications = 0;
        Assert.True(BotHiderPresentationService.TryWriteNetworkedCrosshair(
            normalized!, false, () => actual, value => actual = value,
            () => { notifications++; return true; }, out var changed, out var published));
        Assert.True(changed);
        Assert.True(published);
        Assert.Equal(1, notifications);
        Assert.Equal(code, actual);
    }

    [Fact]
    public void ContractRejectsCrosshairPastUtf8Limit()
    {
        var source = new string('x', DtrHiderContract.MaxCrosshairCodeUtf8Bytes + 1);

        Assert.False(DtrHiderContract.TryNormalizeCrosshairCode(source, out var normalized));
        Assert.Null(normalized);
    }

    [Fact]
    public void ContractRejectsEmbeddedNull()
    {
        Assert.False(DtrHiderContract.TryNormalizeCrosshairCode("CSGO-x\0y", out var normalized));
        Assert.Null(normalized);
    }

    [Fact]
    public void NetworkedCrosshairDoesNotRepublishMatchingAppliedValue()
    {
        var actual = "CSGO-test";
        var writes = 0;
        var publications = 0;

        var retained = BotHiderPresentationService.TryWriteNetworkedCrosshair(
            "CSGO-test",
            forcePublication: false,
            () => actual,
            _ => writes++,
            () =>
            {
                publications++;
                return true;
            },
            out var changed,
            out var published);

        Assert.True(retained);
        Assert.False(changed);
        Assert.False(published);
        Assert.Equal(0, writes);
        Assert.Equal(0, publications);
    }

    [Fact]
    public void NetworkedCrosshairPublishesMatchingValueForNewIncarnation()
    {
        var actual = "CSGO-test";
        var writes = 0;
        var publications = 0;

        var retained = BotHiderPresentationService.TryWriteNetworkedCrosshair(
            "CSGO-test",
            forcePublication: true,
            () => actual,
            _ => writes++,
            () =>
            {
                publications++;
                return true;
            },
            out var changed,
            out var published);

        Assert.True(retained);
        Assert.False(changed);
        Assert.True(published);
        Assert.Equal(0, writes);
        Assert.Equal(1, publications);
    }

    [Fact]
    public void PendingNotificationRetriesWithoutRewritingRetainedValue()
    {
        var actual = string.Empty;
        var writes = 0;
        var attempts = 0;
        bool Publish() => ++attempts > 1;
        void Write(string value) { actual = value; writes++; }

        var pending = !BotHiderPresentationService.TryWriteNetworkedCrosshair(
            "CSGO-test", false, () => actual, Write, Publish, out _, out _);
        Assert.True(pending);
        Assert.False(BotHiderPresentationService.RequestedCrosshairMatches("CSGO-test", actual, pending));
        Assert.True(BotHiderPresentationService.RequestedCrosshairMatches(null, actual, pending));

        pending = !BotHiderPresentationService.TryWriteNetworkedCrosshair(
            "CSGO-test", pending, () => actual, Write, Publish, out var changed, out var published);
        Assert.False(pending);
        Assert.False(changed);
        Assert.True(published);
        Assert.True(BotHiderPresentationService.RequestedCrosshairMatches("CSGO-test", actual, pending));
        Assert.Equal(1, writes);
        Assert.Equal(2, attempts);
    }

}
