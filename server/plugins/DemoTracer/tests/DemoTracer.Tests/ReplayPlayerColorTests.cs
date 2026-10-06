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

    [Fact]
    public void ManifestRetainsPlayerIdentityWithoutReadingMatchStatistics()
    {
        var manifestType = typeof(DemoTracerPlugin).GetNestedType("ConversionManifest", BindingFlags.NonPublic)!;
        var manifest = JsonSerializer.Deserialize("""
            { "rounds": [{ "round": 1, "scoreboard": { "t_score": "ignored", "ct_score": {} } }],
              "files": [{ "round": 1, "side": "t", "scoreboard": {
                "player_color": " Yellow ", "player_user_id": 12, "player_entity_id": 15,
                "score": "ignored", "kills": {}, "deaths": [], "assists": true, "mvps": -1
              } }] }
            """, manifestType, DemoTracerPlugin.ManifestJsonOptions)!;
        var files = (System.Collections.IList)manifestType.GetProperty("Files")!.GetValue(manifest)!;
        var file = files[0]!;
        var identity = file.GetType().GetProperty("PlayerIdentity")!.GetValue(file)!;
        var identityType = identity.GetType();
        Assert.Equal(12, identityType.GetProperty("PlayerUserId")!.GetValue(identity));
        Assert.Equal(15, identityType.GetProperty("PlayerEntityId")!.GetValue(identity));
        var color = typeof(DemoTracerPlugin).GetMethod("NormalizeReplayPlayerColor",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                [identityType.GetProperty("PlayerColor")!.GetValue(identity)]);
        Assert.Equal("yellow", color);
    }
}
