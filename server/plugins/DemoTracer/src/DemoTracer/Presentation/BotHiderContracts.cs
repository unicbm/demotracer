/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text;

namespace DemoTracerBotHiderApi;

public static class DemoTracerBotHiderContract
{
    public const int ApiVersion = BotHiderApi.BotHiderContract.ApiVersion;
    public const string DemoTracerOwner = "demotracer";
    public const int MaxPlayerNameUtf8Bytes = 31;
    public const int MaxCrosshairCodeUtf8Bytes = 63;
    public const int MaxClanTagUtf8Bytes = 127;

    public static bool IsValidClan(BotHiderClan? clan)
        => clan == null || (clan.Tag != null && !clan.Tag.Contains('\0') &&
                           Encoding.UTF8.GetByteCount(clan.Tag) <= MaxClanTagUtf8Bytes);

    public static bool TryNormalizeCrosshairCode(string? source, out string? normalized)
    {
        if (source == null)
        {
            normalized = null;
            return true;
        }

        normalized = source.Trim();
        if (!normalized.Contains('\0') && Encoding.UTF8.GetByteCount(normalized) <= MaxCrosshairCodeUtf8Bytes)
            return true;

        normalized = null;
        return false;
    }
}

public sealed class BotHiderProviderInfo
{
    public int ApiVersion { get; set; }

    public string ProviderEpoch { get; set; } = string.Empty;

    public ulong MapEpoch { get; set; }

    public bool Connected { get; set; }

    public bool Draining { get; set; }
}

public sealed class BotHiderManagedSlot
{
    public int Slot { get; set; }

    public ulong Incarnation { get; set; }

    public ulong BaseSteamId { get; set; }
    public ulong PublishedSteamId { get; set; }

    public string BasePlayerName { get; set; } = string.Empty;

    public int BasePing { get; set; }

    public string BaseCrosshairCode { get; set; } = string.Empty;

    public uint BaseScoreboardFlair { get; set; }
}

public sealed class BotHiderPresentationOverride
{
    public int Slot { get; set; }

    public ulong Incarnation { get; set; }

    public string? PlayerName { get; set; }

    public ulong? SteamId { get; set; }

    public uint? ScoreboardFlair { get; set; }

    // null keeps the current persona base; empty explicitly clears it.
    public string? CrosshairCode { get; set; }

    // null restores the captured controller base; empty tag/id 0 explicitly clears.
    public BotHiderClan? Clan { get; set; }
}

public sealed record BotHiderClan(string Tag, uint Id);

public sealed class BotHiderPresentationLeaseResult
{
    public bool Ok { get; set; }

    public string LeaseToken { get; set; } = string.Empty;

    public string ProviderEpoch { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public int[] Slots { get; set; } = [];
}

public sealed class BotHiderDiagnostics
{
    public bool Connected { get; set; }

    public int ManagedSlots { get; set; }

    public int ActiveLeases { get; set; }

    public int LeasedSlots { get; set; }

    public int RevokedLeases { get; set; }

    public int PublishedWrites { get; set; }

    public int ControllerRepairs { get; set; }

    public string[] Signatures { get; set; } = [];
}
