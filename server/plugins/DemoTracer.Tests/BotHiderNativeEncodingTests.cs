/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text;
using System.Runtime.InteropServices;
using BotHiderImpl;

namespace DemoTracer.Tests;

public sealed class BotHiderNativeEncodingTests
{
    [Fact]
    public void NativeSlotLayoutMatchesPackedContract()
    {
        Assert.Equal(NativePresentationClient.SlotByteSize,
            Marshal.SizeOf<NativePresentationClient.Slot>());
        Assert.Equal(44, Marshal.OffsetOf<NativePresentationClient.Slot>("BaseName").ToInt32());
        Assert.Equal(108, Marshal.OffsetOf<NativePresentationClient.Slot>("Crosshair").ToInt32());
    }

    [Fact]
    public void DisposedTransportRejectsReadsAndWrites()
    {
        using var client = new NativePresentationClient();
        client.Dispose();
        Assert.Equal(0UL, client.Session);
        Assert.False(client.TryGetSlot(1, out _));
        Assert.False(client.PublishIdentity(1, 1, 1, 123, "bot"));
        Assert.False(client.RequestRebuild());
    }
    [Theory]
    [InlineData("abc", 4)]
    [InlineData("😀", 5)]
    [InlineData("选手", 7)]
    public void FixedUtf8FieldAcceptsCompleteNullTerminatedValues(
        string value,
        int fieldLength)
    {
        Assert.True(NativePresentationClient.TryEncodeFixedUtf8(
            value,
            fieldLength,
            out var buffer));
        Assert.Equal(fieldLength, buffer.Length);
        Assert.Equal(value, Encoding.UTF8.GetString(buffer).TrimEnd('\0'));
        Assert.Equal(0, buffer[^1]);
    }

    [Theory]
    [InlineData("abcd", 4)]
    [InlineData("😀", 4)]
    [InlineData("选手", 6)]
    [InlineData("a", 0)]
    [InlineData("a\0b", 8)]
    public void FixedUtf8FieldRejectsValuesWithoutNullTerminatorSpace(
        string value,
        int fieldLength)
    {
        Assert.False(NativePresentationClient.TryEncodeFixedUtf8(
            value,
            fieldLength,
            out _));
    }
}
