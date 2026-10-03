/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core;

namespace BotRandomizer;

internal static class EconItemIdentity
{
    internal static void Apply(
        CEconItemView item,
        ReplayEconIdentity? identity,
        ulong playerSteamId,
        int defaultQuality)
    {
        var owner = identity?.OriginalOwnerSteamId ?? playerSteamId;
        item.AccountID = identity?.ItemAccountId ?? AccountIdFromSteamId(owner);
        item.EntityQuality = identity?.ResolveQuality(defaultQuality) ?? defaultQuality;
        var itemId = identity?.ItemId ?? 0;
        if (itemId == 0)
            itemId = EconItemIdAllocator.Next();
        item.ItemID = itemId;
        item.ItemIDLow = (uint)(itemId & uint.MaxValue);
        item.ItemIDHigh = (uint)(itemId >> 32);
        item.CustomName = identity?.CustomName ?? string.Empty;
    }

    private static uint AccountIdFromSteamId(ulong steamId)
    {
        const ulong steamId64AccountBase = 76_561_197_960_265_728;
        var accountId = steamId >= steamId64AccountBase ? steamId - steamId64AccountBase : steamId;
        return accountId <= uint.MaxValue ? (uint)accountId : 0;
    }
}
