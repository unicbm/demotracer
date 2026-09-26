/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Reflection;
using System.Text.Json;

namespace DemoTracer.Tests;

public sealed class ReplayPlayerColorTests
{
    [Fact]
    public void ReplacementFillsDepartedHumansVacancyWithoutDuplicatingDemoColor()
    {
        var occupied = new HashSet<int> { 0, 1, 3, 4 };
        Assert.Equal(2, ReplayPlayerColorPolicy.ChooseMissingColor(-1, 0, occupied));
        Assert.Equal(new HashSet<int> { 0, 1, 3, 4 }, occupied);
    }

    [Fact]
    public void ReplacementPrefersRecordedColorWhenSeveralColorsAreFree()
        => Assert.Equal(4, ReplayPlayerColorPolicy.ChooseMissingColor(-1, 4, new HashSet<int> { 0, 1 }));

    [Fact]
    public void MultipleReplacementsConsumeDifferentVacancies()
    {
        var occupied = new HashSet<int> { 0, 1, 3 };
        var first = ReplayPlayerColorPolicy.ChooseMissingColor(-1, 0, occupied);
        Assert.Equal(2, first);
        occupied.Add(first!.Value);
        Assert.Equal(4, ReplayPlayerColorPolicy.ChooseMissingColor(-1, 0, occupied));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ValidNativeColorsAreNotReplacedOrTreatedAsMissing(int current)
        => Assert.Null(ReplayPlayerColorPolicy.ChooseMissingColor(current, 4, new HashSet<int>()));

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void MissingOrInvalidRecordedColorDoesNotInventAnOverride(int recorded)
        => Assert.Null(ReplayPlayerColorPolicy.ChooseMissingColor(-1, recorded, new HashSet<int>()));

    [Fact]
    public void FullPaletteDoesNotRecolorAnotherPlayerOrCreateADuplicate()
        => Assert.Null(ReplayPlayerColorPolicy.ChooseMissingColor(-1, 2, new HashSet<int> { 0, 1, 2, 3, 4 }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContinuationRetainsColorWithoutImportingAnotherRoundsMatchStatistics(bool includeMatchStats)
    {
        var type = typeof(DemoTracerPlugin).GetNestedType("ReplayPlayerScoreboard", BindingFlags.NonPublic)!;
        var evidence = JsonSerializer.Deserialize("""
            { "player_color": " Yellow ", "player_user_id": 12, "player_entity_id": 15,
              "score": 20, "kills": 8, "deaths": 4, "assists": 2, "mvps": 1 }
            """, type)!;
        var result = typeof(DemoTracerPlugin).GetMethod("SelectReplayScoreboardEvidence",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [evidence, includeMatchStats])!;

        Assert.Equal("yellow", type.GetProperty("PlayerColor")!.GetValue(result));
        foreach (var field in new[] { "PlayerUserId", "PlayerEntityId", "Score", "Kills", "Deaths", "Assists", "MVPs" })
        {
            var actual = type.GetProperty(field)!.GetValue(result);
            if (includeMatchStats)
                Assert.Equal(type.GetProperty(field)!.GetValue(evidence), actual);
            else
                Assert.Null(actual);
        }
    }
}
