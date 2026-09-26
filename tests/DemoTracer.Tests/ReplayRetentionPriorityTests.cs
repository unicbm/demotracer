/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using DemoTracer;

namespace DemoTracer.Tests;

public sealed class ReplayRetentionPriorityTests
{
    [Fact]
    public void ParsesOrderedSteamIdGroup()
    {
        var ok = ReplayRetentionPriorityParser.TryParseGroup(
            "76561198000000003,76561198000000001,76561198000000002",
            out var steamIds,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(
            [76561198000000003UL, 76561198000000001UL, 76561198000000002UL],
            steamIds);
    }

    [Theory]
    [InlineData("76561198000000001,76561198000000001")]
    [InlineData("not-a-steamid")]
    [InlineData("1,2,3,4,5,6")]
    public void RejectsInvalidPriorityGroup(string value)
    {
        Assert.False(ReplayRetentionPriorityParser.TryParseGroup(value, out _, out _));
    }

    [Fact]
    public void AcceptsExplicitEmptyGroup()
    {
        Assert.True(ReplayRetentionPriorityParser.TryParseGroup("-", out var steamIds, out var error), error);
        Assert.Empty(steamIds);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("52", 52)]
    [InlineData("119", 119)]
    public void ParsesCompactPermutationCode(string value, int expected)
    {
        Assert.True(ReplayRetentionPriorityParser.TryParsePermutationCode(value, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("120")]
    [InlineData("1.0")]
    [InlineData("76561198000000001")]
    public void RejectsInvalidCompactPermutationCode(string value)
    {
        Assert.False(ReplayRetentionPriorityParser.TryParsePermutationCode(value, out _));
    }

    [Theory]
    [InlineData(0, new[] { 0, 1, 2, 3, 4 })]
    [InlineData(52, new[] { 2, 0, 4, 1, 3 })]
    [InlineData(119, new[] { 4, 3, 2, 1, 0 })]
    public void DecodesCompactPermutation(int code, int[] expected)
    {
        Assert.True(ReplayRetentionPriorityParser.TryDecodePermutation(code, out var indices));
        Assert.Equal(expected, indices);
    }

    [Fact]
    public void CompactCodesCoverEveryFivePlayerPermutation()
    {
        var decoded = Enumerable.Range(0, ReplayRetentionPriorityParser.PermutationCount)
            .Select(code =>
            {
                Assert.True(ReplayRetentionPriorityParser.TryDecodePermutation(code, out var indices));
                return string.Join(",", indices);
            })
            .ToArray();

        Assert.Equal(ReplayRetentionPriorityParser.PermutationCount, decoded.Distinct().Count());
    }

    [Fact]
    public void PartialRosterDropsLowestRetentionRankAndKeepsManifestOrder()
    {
        var selected = ReplayRetentionPriorityParser.SelectPreferredIndices([5, 1, 4, 2, 3], 4);

        Assert.Equal([1, 2, 3, 4], selected);
    }

    [Theory]
    [InlineData(10, 1, 9)]
    [InlineData(10, 2, 8)]
    [InlineData(1, 4, 0)]
    [InlineData(10, 0, 10)]
    public void ReservesOneBotQuotaSlotPerPendingHumanJoin(
        int baseline,
        int pendingHumanJoins,
        int expected)
    {
        Assert.Equal(
            expected,
            ReplayRetentionPriorityParser.ReservedBotQuota(baseline, pendingHumanJoins));
    }
}
