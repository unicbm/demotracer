/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/


using System.Security.Cryptography;

namespace DemoTracer;

internal static class ReplayAvatarEvidence
{
    internal static bool Validate(ReadOnlySpan<byte> png, string sha256, out string error)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 13, 10, 26, 10];
        if (png.Length > 16 * 1024 || !png.StartsWith(signature))
        {
            error = "avatar must be a PNG of at most 16 KiB";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(sha256) &&
            !string.Equals(Convert.ToHexString(SHA256.HashData(png)), sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            error = "avatar PNG does not match manifest SHA-256 evidence";
            return false;
        }
        error = string.Empty;
        return true;
    }
}
