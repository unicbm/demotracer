/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/


using System.Security.Cryptography;

namespace DemoTracer.Tests;

public sealed class ReplayAvatarEvidenceTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 13, 10, 26, 10, 1, 2, 3];

    [Fact]
    public void ReplacementOfTheSameLengthCannotReuseManifestEvidence()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Png));
        Assert.True(ReplayAvatarEvidence.Validate(Png, hash.ToLowerInvariant(), out _));
        var changed = (byte[])Png.Clone();
        changed[^1] ^= 1;
        Assert.False(ReplayAvatarEvidence.Validate(changed, hash, out _));
    }

    [Fact]
    public void MissingOptionalHashStillRequiresPngAndEngineSizeLimit()
    {
        Assert.True(ReplayAvatarEvidence.Validate(Png, "", out _));
        Assert.False(ReplayAvatarEvidence.Validate([], "", out _));
        Assert.False(ReplayAvatarEvidence.Validate("not a png"u8, "", out _));
        var oversized = new byte[16 * 1024 + 1];
        Png.CopyTo(oversized, 0);
        Assert.False(ReplayAvatarEvidence.Validate(oversized, "", out _));
        Assert.False(ReplayAvatarEvidence.Validate(Png, "bad-hash", out _));
    }
}
