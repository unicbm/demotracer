/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/


using System.Runtime.InteropServices;

namespace DemoTracer;

internal static partial class BotControllerNative
{
    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_PublishAvatarOverride(ulong steamId, [In] byte[] png, int length);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotController_ClearAvatarOverride(ulong steamId);

    [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
    private static extern void BotController_ClearAvatarOverrides();

    public static bool TryPublishAvatarOverride(ulong steamId, byte[] png, out string error)
    {
        try
        {
            var result = BotController_PublishAvatarOverride(steamId, png, png.Length);
            error = result >= 0 ? string.Empty : $"native avatar publication failed ({result})";
            return result >= 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryClearAvatarOverride(ulong steamId, out string error)
    {
        try
        {
            var result = BotController_ClearAvatarOverride(steamId);
            error = result >= 0 ? string.Empty : $"native avatar restoration failed ({result})";
            return result >= 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static void ClearAvatarOverrides()
    {
        try { BotController_ClearAvatarOverrides(); }
        catch (Exception ex) { LastLoadError = $"avatar cleanup: {ex.Message}"; }
    }
}
