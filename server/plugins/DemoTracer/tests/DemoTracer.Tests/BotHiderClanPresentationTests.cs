/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text.Json;
using DtrHider;
using DtrHiderApi;

namespace DemoTracer.Tests;

public sealed class BotHiderClanPresentationTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tag\":\"old\"}")]
    [InlineData("{\"id\":0}")]
    [InlineData("{\"tag\":null,\"id\":0}")]
    public void IncompleteManifestEvidenceDoesNotClearBase(string json)
        => Assert.Null(DemoTracerPlugin.NormalizeReplayClan(
            JsonSerializer.Deserialize<DemoTracerPlugin.ReplayClan>(json)));

    [Fact]
    public void ExplicitEmptyAndUnicodeArePreserved()
    {
        foreach (var pair in new[] { new BotHiderClan("", 0), new BotHiderClan("o'O 组\u0301", 4775497) })
        {
            var json = JsonSerializer.Serialize(new { tag = pair.Tag, id = pair.Id });
            Assert.Equal(pair, DemoTracerPlugin.NormalizeReplayClan(
                JsonSerializer.Deserialize<DemoTracerPlugin.ReplayClan>(json)));
        }
        Assert.False(DtrHiderContract.IsValidClan(new("x\0y", 1)));
        Assert.False(DtrHiderContract.IsValidClan(new(new string('组', 43), 1)));
        Assert.True(DtrHiderContract.IsValidClan(new(new string('x', 127), uint.MaxValue)));
    }

    [Fact]
    public void MissingOverrideDoesNotReadUnsupportedSchema()
    {
        var state = new ClanPresentationState();
        Assert.False(state.Apply(null, () => throw new Exception("unexpected read"),
            _ => throw new Exception("unexpected write"), () => throw new Exception("unexpected publish")));
    }

    [Fact]
    public void ReplacementAndExplicitClearRestoreOriginalPair()
    {
        var original = new BotHiderClan("base", 42);
        var current = original;
        var notifications = 0;
        var state = new ClanPresentationState();
        void Apply(BotHiderClan? clan) => state.Apply(clan, () => current,
            value => current = value, () => notifications++);

        Apply(new("team_B1ad3", 38084528));
        Apply(new("", 0));
        Assert.Equal(new BotHiderClan("", 0), current);
        Apply(null);
        Assert.Equal(original, current);
        Assert.False(state.HasOverride);
        Assert.Equal(3, notifications);
        // Subsequent base changes must be captured anew.
        original = current = new("new base", 99);
        Apply(new("next", 11));
        Apply(null);
        Assert.Equal(original, current);
    }

    [Theory]
    [InlineData("demo team", 42u)]
    [InlineData("", 0u)]
    public void UnsetNativeTagRestoresAsEmptyInsteadOfPassingNullToCss(string tag, uint id)
    {
        // CSS's string reader returns null for an unset m_szClan despite its
        // non-nullable signature. Its native setter would call strdup(NULL).
        var current = new BotHiderClan(null!, 0);
        var state = new ClanPresentationState();
        void Write(BotHiderClan value)
        {
            Assert.NotNull(value.Tag);
            current = value;
        }

        var requested = new BotHiderClan(tag, id);
        state.Apply(requested, () => current, Write, () => { });
        Assert.Equal(requested, current);
        state.Apply(null, () => current, Write, () => { });

        Assert.Equal(new BotHiderClan("", 0), current);
        Assert.False(state.HasOverride);
    }

    [Fact]
    public void FailedNotificationRetriesEvenWhenReadbackAlreadyMatches()
    {
        var current = new BotHiderClan("base", 42);
        var wanted = new BotHiderClan("next", 22);
        var state = new ClanPresentationState();
        Assert.Throws<InvalidOperationException>(() => state.Apply(wanted, () => current,
            value => current = value, () => throw new InvalidOperationException("notification failed")));
        Assert.Equal(wanted, current);
        var notified = false;
        state.Apply(wanted, () => current, _ => throw new Exception("unchanged clan was rewritten"), () => notified = true);
        Assert.True(notified);
        state.Apply(wanted, () => current, _ => throw new Exception("unchanged clan was rewritten"),
            () => throw new Exception("unchanged clan was republished"));
        state.Apply(null, () => current, value => current = value, () => { });
        Assert.Equal(new BotHiderClan("base", 42), current);
    }

    [Fact]
    public void FailedPartialPairWriteCanBeRolledBack()
    {
        var original = new BotHiderClan("base", 42);
        var current = original;
        var state = new ClanPresentationState();
        Assert.Throws<InvalidOperationException>(() => state.Apply(new("next", 22), () => current,
            value => { current = current with { Tag = value.Tag }; throw new InvalidOperationException(); },
            () => { }));
        state.Apply(null, () => current, value => current = value, () => { });
        Assert.Equal(original, current);
    }
}
