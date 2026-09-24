// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using NativeCompatibility;
using Xunit;

namespace DemoTracer.Tests;

public sealed class PeImageFingerprintTests
{
    internal static byte[] Fixture()
    {
        byte[] data = new byte[0x900];
        void U16(int at, ushort n) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), n);
        void U32(int at, uint n) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), n);
        data[0] = (byte)'M'; data[1] = (byte)'Z'; U32(0x3c, 0x80);
        U32(0x80, 0x4550); U16(0x84, 0x8664); U16(0x86, 3); U16(0x94, 240); U16(0x96, 0x2022);
        const int opt = 0x98;
        U16(opt, 0x20b); U32(opt + 16, 0x1000); U32(opt + 32, 0x1000); U32(opt + 36, 0x200);
        U32(opt + 56, 0x4000); U32(opt + 60, 0x200); U32(opt + 108, 16);
        U32(opt + 144, 0x800); U32(opt + 148, 0x100);
        U32(opt + 160, 0x2000); U32(opt + 164, 28);
        string[] names = [".text", ".rdata", ".pdata"];
        for (int i = 0; i < 3; i++)
        {
            int s = opt + 240 + i * 40;
            System.Text.Encoding.ASCII.GetBytes(names[i]).CopyTo(data, s);
            U32(s + 8, 0x200); U32(s + 12, (uint)(0x1000 + i * 0x1000));
            U32(s + 16, 0x200); U32(s + 20, (uint)(0x200 + i * 0x200));
            U32(s + 36, i == 0 ? 0x60000020u : 0x40000040u);
        }
        data[0x200] = 0xc3;
        U32(0x400 + 12, 2); U32(0x400 + 16, 32); U32(0x400 + 20, 0x2040); U32(0x400 + 24, 0x440);
        "RSDS"u8.CopyTo(data.AsSpan(0x440)); U32(0x454, 1); "x.pdb\0"u8.CopyTo(data.AsSpan(0x458));
        return data;
    }

    [Fact]
    public void MetadataOnlyRebuildRetainsCompatibility()
    {
        byte[] a = Fixture(), b = (byte[])a.Clone();
        foreach (int offset in new[] { 0x50, 0x88, 0xd8, 0x404, 0x454, 0x820 }) b[offset] ^= 1;
        Assert.Equal(PeImageFingerprint.Compute(a), PeImageFingerprint.Compute(b));
    }

    [Theory]
    [InlineData(0x200)] // Code.
    [InlineData(0x4a0)] // Read-only state/schema/vtable data, not debug metadata.
    [InlineData(0x600)] // Unwind section.
    [InlineData(0x448)] // PDB GUID still identifies code, unlike age.
    [InlineData(0xd0)] // SizeOfImage/layout.
    public void FunctionalChangesAreRejected(int offset)
    {
        byte[] a = Fixture(), b = (byte[])a.Clone(); b[offset] ^= 1;
        Assert.NotEqual(PeImageFingerprint.Compute(a), PeImageFingerprint.Compute(b));
    }

    [Fact]
    public void CannotHideCodeChangesBehindDebugDirectory()
    {
        var data = Fixture();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x98 + 160), 0x1000);
        Assert.ThrowsAny<Exception>(() => PeImageFingerprint.Compute(data));
    }

    [Fact]
    public void TruncatedFileIsRejected()
        => Assert.ThrowsAny<Exception>(() => PeImageFingerprint.Compute(Fixture()[..0x210]));
}
