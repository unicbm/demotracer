using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using System.Globalization;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const uint PlaybackPresetWeapons = 1U << 0;
    private const uint PlaybackPresetCosmetics = 1U << 1;
    private const uint PlaybackPresetSteamIdentity = 1U << 2;
    private const uint PlaybackPresetAvatar = 1U << 3;
    private const uint PlaybackPresetVoice = 1U << 4;
    private const uint PlaybackPresetPlayoff = 1U << 5;
    private const uint PlaybackPresetAllowedMask =
        PlaybackPresetWeapons |
        PlaybackPresetCosmetics |
        PlaybackPresetSteamIdentity |
        PlaybackPresetAvatar |
        PlaybackPresetVoice |
        PlaybackPresetPlayoff;

    [ConsoleCommand("dtr_preset", "dtr_preset [status|0xMASK]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void PlaybackPresetCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;

        if (command.ArgCount < 2 ||
            command.GetArg(1).Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            ReplyPlaybackPresetStatus(command.ReplyToCommand);
            return;
        }

        if (!TryParsePlaybackPresetMask(command.GetArg(1), out var mask))
        {
            command.ReplyToCommand("[DTR ERR] invalid playback preset mask; expected hexadecimal such as 0x15");
            ReplyPlaybackPresetUsage(command.ReplyToCommand);
            return;
        }

        var unknownBits = mask & ~PlaybackPresetAllowedMask;
        if (unknownBits != 0)
        {
            command.ReplyToCommand($"[DTR ERR] playback preset contains unknown bits: 0x{unknownBits:X}");
            ReplyPlaybackPresetUsage(command.ReplyToCommand);
            return;
        }

        if ((mask & PlaybackPresetAvatar) != 0 &&
            (mask & PlaybackPresetSteamIdentity) == 0)
        {
            command.ReplyToCommand("[DTR ERR] avatar sync requires Steam identity sync (0x08 requires 0x04)");
            return;
        }

        if ((mask & PlaybackPresetCosmetics) != 0 &&
            (mask & PlaybackPresetWeapons) == 0)
        {
            command.ReplyToCommand("[DTR ERR] cosmetic sync requires weapon/loadout sync (0x02 requires 0x01)");
            return;
        }

        ApplyPlaybackPreset(mask);
        ReplyPlaybackPresetStatus(command.ReplyToCommand);
        if ((mask & PlaybackPresetCosmetics) != 0)
            command.ReplyToCommand(CosmeticRiskNotice);
    }

    private void ApplyPlaybackPreset(uint mask)
    {
        SetWeaponAlignEnabled((mask & PlaybackPresetWeapons) != 0);
        ApplyCosmeticPreset((mask & PlaybackPresetCosmetics) != 0
            ? CosmeticPreset.Full
            : CosmeticPreset.Off);

        _replayIdentityMode = (mask & PlaybackPresetAvatar) != 0
            ? ReplayIdentityMode.Avatar
            : (mask & PlaybackPresetSteamIdentity) != 0
                ? ReplayIdentityMode.Steam
                : ReplayIdentityMode.Off;
        _voiceAutoEnabled = (mask & PlaybackPresetVoice) != 0;
        _playoffEnabled = (mask & PlaybackPresetPlayoff) != 0;
        ApplyRuntimeConfigSideEffects();
    }

    private uint CurrentPlaybackPresetMask()
    {
        var mask = 0U;
        if (_weaponAlignEnabled)
            mask |= PlaybackPresetWeapons;
        if (_cosmeticAlignEnabled)
            mask |= PlaybackPresetCosmetics;
        if (_replayIdentityMode is ReplayIdentityMode.Steam or ReplayIdentityMode.Avatar)
            mask |= PlaybackPresetSteamIdentity;
        if (_replayIdentityMode == ReplayIdentityMode.Avatar)
            mask |= PlaybackPresetAvatar;
        if (_voiceAutoEnabled)
            mask |= PlaybackPresetVoice;
        if (_playoffEnabled)
            mask |= PlaybackPresetPlayoff;
        return mask;
    }

    private void ReplyPlaybackPresetStatus(Action<string> reply)
    {
        var mask = CurrentPlaybackPresetMask();
        reply(
            $"[DTR PRESET] v1 mask=0x{mask:X2} weapons={FormatOnOff(_weaponAlignEnabled)} cosmetics={FormatOnOff(_cosmeticAlignEnabled)} identity={ReplayIdentityModeName()} voice={FormatOnOff(_voiceAutoEnabled)} playoff={FormatOnOff(_playoffEnabled)}");
        reply("[DTR PRESET] bits 0x01=weapons 0x02=cosmetics 0x04=steam 0x08=avatar 0x10=voice 0x20=playoff");
    }

    private static void ReplyPlaybackPresetUsage(Action<string> reply)
        => reply("usage: dtr_preset [status|0x00..0x3F]");

    private static bool TryParsePlaybackPresetMask(string raw, out uint mask)
    {
        mask = 0;
        var value = raw.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        if (value.Length is 0 or > 8)
            return false;
        return uint.TryParse(
            value,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out mask);
    }
}
