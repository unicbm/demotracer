/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text;

namespace DemoTracer;

internal static class DemoTracerCrosshairCode
{
    private const int MaxPublishedBytes = 63;

    public static string? Normalize(string? code)
    {
        var trimmed = code?.Trim();
        return string.IsNullOrEmpty(trimmed) ||
               Encoding.UTF8.GetByteCount(trimmed) > MaxPublishedBytes
            ? null
            : trimmed;
    }
}
