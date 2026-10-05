/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DtrHider;

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
        Assert.False(client.PublishPing(1, 1, 1, 0x8005));
        Assert.False(client.SetDisguise(true));
        Assert.False(client.SetNameSource(true));
    }
    [Fact]
    public void MissingNativeBackendRetriesOnlyOnExplicitConnect()
    {
        // The managed test host has no CS2 native runtime. Count first-chance
        // exceptions because the transport intentionally catches loader errors.
        int thread = Environment.CurrentManagedThreadId;
        int loaderFailures = 0;
        void OnException(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (Environment.CurrentManagedThreadId == thread &&
                args.Exception is DllNotFoundException &&
                args.Exception.Message.Contains("dtr-hider", StringComparison.Ordinal))
                loaderFailures++;
        }

        AppDomain.CurrentDomain.FirstChanceException += OnException;
        try
        {
            using var client = new NativePresentationClient();
            var service = new BotHiderPresentationService(client);
            Assert.False(client.TryConnect());
            Assert.Equal(1, loaderFailures);
            for (int i = 0; i < 256; i++)
            {
                Assert.Equal(0UL, client.Session);
                Assert.False(client.IsConnected());
                Assert.False(client.IsManagedBot(1));
                Assert.False(service.IsManagedBot(1));
                Assert.False(service.GetProviderInfo().Connected);
            }
            Assert.Equal(1, loaderFailures);

            // Models the later native-ready lifecycle notification. A failed
            // retry is cached again; disposing must never reopen the backend.
            Assert.False(client.TryConnect());
            Assert.Equal(2, loaderFailures);
            Assert.Equal(0UL, client.Session);
            client.Dispose();
            Assert.False(client.TryConnect());
            Assert.Equal(2, loaderFailures);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnException;
        }
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
