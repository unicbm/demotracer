/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text;
using DemoTracerBotHiderApi;

namespace DemoTracer.Tests;

public sealed class ReplayPresentationNameTests
{
    [Fact]
    public void MixedScriptNameIsTruncatedAtTextElementBoundary()
    {
        const string source = "Synthetic玩家اسمABCDEFGHIJK";

        var derived = DemoTracerPlugin.DeriveBotHiderPresentationName(source);

        Assert.Equal("Synthetic玩家اسمABCDEFGHIJ", derived);
        Assert.StartsWith(derived!, source, StringComparison.Ordinal);
        Assert.InRange(
            Encoding.UTF8.GetByteCount(derived!),
            1,
            DemoTracerBotHiderContract.MaxPlayerNameUtf8Bytes);
    }

    [Fact]
    public void TruncationKeepsAWholeFourByteRuneAtTheLimit()
    {
        var prefix = new string('a', 27);

        var derived = DemoTracerPlugin.DeriveBotHiderPresentationName($"{prefix}😀z");

        Assert.Equal($"{prefix}😀", derived);
        Assert.Equal(
            DemoTracerBotHiderContract.MaxPlayerNameUtf8Bytes,
            Encoding.UTF8.GetByteCount(derived!));
    }

    [Fact]
    public void InvisibleFormattingAndControlsAreRemovedFromPresentationOnly()
    {
        const string source = "\u200B\u2066Ali\r\nce\u2069\u200D";

        var derived = DemoTracerPlugin.DeriveBotHiderPresentationName(source);

        Assert.Equal("Alice", derived);
        Assert.Contains('\u200B', source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u200B\u2066\u2069")]
    public void EmptyPresentationLeavesThePersonaNameUntouched(string? source)
    {
        Assert.Null(DemoTracerPlugin.DeriveBotHiderPresentationName(source));
    }

    [Theory]
    [InlineData(28, "e\u0301")]
    [InlineData(20, "👩\u200D💻")]
    [InlineData(25, "✈️")]
    public void WholeGraphemeAtTheByteLimitIsPreserved(int prefixLength, string grapheme)
    {
        var prefix = new string('a', prefixLength);

        var derived = DemoTracerPlugin.DeriveBotHiderPresentationName($"{prefix}{grapheme}z");

        Assert.Equal($"{prefix}{grapheme}", derived);
        Assert.Equal(
            DemoTracerBotHiderContract.MaxPlayerNameUtf8Bytes,
            Encoding.UTF8.GetByteCount(derived!));
    }

    [Theory]
    [InlineData(29, "e\u0301")]
    [InlineData(21, "👩\u200D💻")]
    [InlineData(26, "✈️")]
    public void GraphemeThatDoesNotFitIsDroppedAsAWhole(int prefixLength, string grapheme)
    {
        var prefix = new string('a', prefixLength);

        var derived = DemoTracerPlugin.DeriveBotHiderPresentationName($"{prefix}{grapheme}");

        Assert.Equal(prefix, derived);
    }

    [Theory]
    [InlineData("\u200B \uFE0FZ", "Z")]
    [InlineData("Z \uFE0F", "Z")]
    public void EdgeWhitespaceIsRemovedAsAWholeTextElement(string source, string expected)
    {
        Assert.Equal(expected, DemoTracerPlugin.DeriveBotHiderPresentationName(source));
    }

    [Fact]
    public void OneLongNameCannotMakeTheDerivedBatchInvalid()
    {
        var sources = new[]
        {
            "normal",
            "Synthetic玩家اسمABCDEFGHIJK",
            new string('界', 40),
            "\u200B\u2066\u2069"
        };

        var derived = sources
            .Select(DemoTracerPlugin.DeriveBotHiderPresentationName)
            .ToArray();

        Assert.Null(derived[^1]);
        Assert.All(derived[..^1], playerName =>
        {
            Assert.False(string.IsNullOrWhiteSpace(playerName));
            Assert.InRange(
                Encoding.UTF8.GetByteCount(playerName!),
                1,
                DemoTracerBotHiderContract.MaxPlayerNameUtf8Bytes);
        });
    }
}
