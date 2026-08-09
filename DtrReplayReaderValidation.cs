/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

internal static partial class DtrReplayReader
{
    // CS2's normal sv_maxvelocity ceiling is lower than this. Values beyond
    // the conservative limit are parser artifacts seen on the first shared
    // freeze-time snapshot after a spawn transition, not usable player motion.
    private const float MaxPlayerVelocityComponent = 4096.0f;

    private static void ValidateReplaySemantics(DtrReplayFile replay)
    {
        if (!float.IsFinite(replay.TickRate) || replay.TickRate <= 0.0f)
            throw new InvalidDataException("tick_rate must be finite and positive");

        for (var i = 0; i < replay.Ticks.Length; i++)
        {
            ValidateSnapshot(replay.Ticks[i].Pre, $"tick {i} pre");
            ValidateSnapshot(replay.Ticks[i].Post, $"tick {i} post");
            if (replay.Ticks[i].WeaponDefIndex < -1)
            {
                throw new InvalidDataException(
                    $"tick {i} weapon_def_index {replay.Ticks[i].WeaponDefIndex} is below -1");
            }
        }

        for (var i = 0; i < replay.Subticks.Length; i++)
        {
            var subtick = replay.Subticks[i];
            if (!float.IsFinite(subtick.When) || subtick.When < 0.0f || subtick.When >= 1.0f)
                throw new InvalidDataException($"subtick {i} when must be finite and in [0, 1)");
            RequireFinite(
                [subtick.Pressed, subtick.AnalogForward, subtick.AnalogLeft, subtick.PitchDelta, subtick.YawDelta],
                $"subtick {i}");
        }

        for (var i = 0; i < replay.CommandFrames.Length; i++)
        {
            var frame = replay.CommandFrames[i];
            var unknownFields = frame.Fields & ~CommandFieldsAll;
            if (unknownFields != 0)
                throw new InvalidDataException($"command frame {i} has unknown fields 0x{unknownFields:X8}");
            if (frame.LeftHandDesired > 1)
                throw new InvalidDataException($"command frame {i} left_hand_desired must be 0 or 1");
            if (frame.Pad0 != 0 || frame.Pad1 != 0 || frame.Pad2 != 0)
                throw new InvalidDataException($"command frame {i} padding must be zero");
            if (frame.WeaponSelect < -1)
            {
                throw new InvalidDataException(
                    $"command frame {i} weapon_select {frame.WeaponSelect} is below -1");
            }
            RequireFinite(
                [frame.ForwardMove, frame.LeftMove, frame.UpMove, frame.Pitch, frame.Yaw, frame.Roll],
                $"command frame {i}");
        }

        for (var i = 0; i < replay.MovementExtras.Length; i++)
        {
            var extra = replay.MovementExtras[i];
            RequireFinite(
                [
                    extra.JumpPressedTime,
                    extra.LastDuckTime,
                    extra.LastActualJumpPressFrac,
                    extra.LastUsableJumpPressFrac,
                    extra.LastLandedFrac,
                    extra.LastLandedVelocityX,
                    extra.LastLandedVelocityY,
                    extra.LastLandedVelocityZ
                ],
                $"movement extra {i}");
        }

        for (var i = 0; i < replay.InputHistoryEntries.Length; i++)
        {
            var entry = replay.InputHistoryEntries[i];
            var unknownFields = entry.Fields & ~InputHistoryFieldsAll;
            if (unknownFields != 0)
                throw new InvalidDataException(
                    $"input history entry {i} has unknown fields 0x{unknownFields:X8}");
            RequireFinite(
                [
                    entry.ViewPitch, entry.ViewYaw, entry.ViewRoll,
                    entry.RenderTickFraction, entry.PlayerTickFraction,
                    entry.ClInterpFraction, entry.SvInterp0Fraction,
                    entry.SvInterp1Fraction, entry.PlayerInterpFraction,
                    entry.ShootPositionX, entry.ShootPositionY, entry.ShootPositionZ,
                    entry.TargetHeadPosCheckX, entry.TargetHeadPosCheckY, entry.TargetHeadPosCheckZ,
                    entry.TargetAbsPosCheckX, entry.TargetAbsPosCheckY, entry.TargetAbsPosCheckZ,
                    entry.TargetAbsAngCheckX, entry.TargetAbsAngCheckY, entry.TargetAbsAngCheckZ
                ],
                $"input history entry {i}");
        }

        for (var i = 0; i < replay.Projectiles.Length; i++)
        {
            var projectile = replay.Projectiles[i];
            if (projectile.TickIndex >= replay.Ticks.Length)
            {
                throw new InvalidDataException(
                    $"projectile {i} tick_index {projectile.TickIndex} out of range for {replay.Ticks.Length} ticks");
            }
            if (projectile.Kind is < ReplayProjectileKind.Unknown or > ReplayProjectileKind.Decoy)
                throw new InvalidDataException($"projectile {i} kind is out of range");
            if (projectile.WeaponDefIndex < -1)
            {
                throw new InvalidDataException(
                    $"projectile {i} weapon_def_index {projectile.WeaponDefIndex} is below -1");
            }
            RequireFinite(
                [
                    projectile.InitialPosition.X,
                    projectile.InitialPosition.Y,
                    projectile.InitialPosition.Z,
                    projectile.InitialVelocity.X,
                    projectile.InitialVelocity.Y,
                    projectile.InitialVelocity.Z,
                    projectile.DetonationPosition.X,
                    projectile.DetonationPosition.Y,
                    projectile.DetonationPosition.Z,
                    projectile.EffectPosition.X,
                    projectile.EffectPosition.Y,
                    projectile.EffectPosition.Z,
                    projectile.EffectConfidence
                ],
                $"projectile {i}");
        }
    }

    private static void ValidateSnapshot(NativeMovementSnapshot snapshot, string name)
    {
        RequireFinite(
            [
                snapshot.OriginX,
                snapshot.OriginY,
                snapshot.OriginZ,
                snapshot.VelX,
                snapshot.VelY,
                snapshot.VelZ,
                snapshot.Pitch,
                snapshot.Yaw,
                snapshot.Roll,
                snapshot.DuckAmount,
                snapshot.DuckSpeed,
                snapshot.LadderNormalX,
                snapshot.LadderNormalY,
                snapshot.LadderNormalZ
            ],
            name);
        if (snapshot.Pad0 != 0 || snapshot.Pad1 != 0 || snapshot.Pad2 != 0)
            throw new InvalidDataException($"{name} padding must be zero");
        if (snapshot.Ducked > 1 || snapshot.Ducking > 1 || snapshot.DesiresDuck > 1)
            throw new InvalidDataException($"{name} duck state bytes must be 0 or 1");
    }

    private static void RepairLaggedPlayerVelocities(
        NativeMovementSnapshot[] snapshots,
        float tickRate)
    {
        var hasImpossibleVelocity = false;
        for (var i = 0; i < snapshots.Length; i++)
        {
            var snapshot = snapshots[i];
            if (!float.IsFinite(snapshot.VelX) ||
                !float.IsFinite(snapshot.VelY) ||
                !float.IsFinite(snapshot.VelZ))
            {
                // Preserve malformed values for the strict semantic validator.
                return;
            }
            if (MathF.Abs(snapshot.VelX) > MaxPlayerVelocityComponent ||
                MathF.Abs(snapshot.VelY) > MaxPlayerVelocityComponent ||
                MathF.Abs(snapshot.VelZ) > MaxPlayerVelocityComponent)
            {
                hasImpossibleVelocity = true;
                break;
            }
        }
        if (!hasImpossibleVelocity)
            return;

        // The converter's historical derived-velocity lane was exactly one
        // sample late. An impossible spawn-transition value is an unambiguous
        // marker for that lane, so rebuild its entire point-state derivative
        // from the canonical origin chain instead of only clipping one sample.
        for (var i = 0; i < snapshots.Length; i++)
        {
            ref var snapshot = ref snapshots[i];
            if (i == 0 || !float.IsFinite(tickRate) || tickRate <= 0.0f)
            {
                snapshot.VelX = 0.0f;
                snapshot.VelY = 0.0f;
                snapshot.VelZ = 0.0f;
                continue;
            }

            var previous = snapshots[i - 1];
            var velX = (snapshot.OriginX - previous.OriginX) * tickRate;
            var velY = (snapshot.OriginY - previous.OriginY) * tickRate;
            var velZ = (snapshot.OriginZ - previous.OriginZ) * tickRate;
            if (!float.IsFinite(velX) ||
                !float.IsFinite(velY) ||
                !float.IsFinite(velZ) ||
                MathF.Abs(velX) > MaxPlayerVelocityComponent ||
                MathF.Abs(velY) > MaxPlayerVelocityComponent ||
                MathF.Abs(velZ) > MaxPlayerVelocityComponent)
            {
                velX = 0.0f;
                velY = 0.0f;
                velZ = 0.0f;
            }
            snapshot.VelX = velX;
            snapshot.VelY = velY;
            snapshot.VelZ = velZ;
        }
    }

    private static void RequireFinite(ReadOnlySpan<float> values, string name)
    {
        foreach (var value in values)
        {
            if (!float.IsFinite(value))
                throw new InvalidDataException($"{name} contains a non-finite float");
        }
    }

    private static ReplayHighFidelityMetadata ReadHighFidelityMetadata(byte[] metadataJson)
    {
        var metadata = JsonSerializer.Deserialize<ReplayHighFidelityMetadata>(metadataJson, HifiJsonOptions)
            ?? ReplayHighFidelityMetadata.Empty;
        metadata.Events ??= [];
        metadata.InventorySnapshots ??= [];
        metadata.Projectiles ??= [];
        return metadata;
    }

    private static ReplayProjectileEvent[] MergeProjectileMetadata(
        ReplayProjectileEvent[] projectiles,
        ReplayHighFidelityMetadata highFidelity)
    {
        var metadata = highFidelity.Projectiles ?? [];
        if (projectiles.Length == 0 || metadata.Length == 0)
            return projectiles;

        var used = new bool[metadata.Length];
        var merged = new ReplayProjectileEvent[projectiles.Length];
        for (var i = 0; i < projectiles.Length; i++)
        {
            var projectile = projectiles[i];
            var match = -1;
            for (var j = 0; j < metadata.Length; j++)
            {
                if (used[j])
                    continue;
                if (!ProjectileMetadataMatches(projectile, metadata[j]))
                    continue;
                match = j;
                break;
            }

            if (match < 0)
            {
                merged[i] = projectile;
                continue;
            }

            used[match] = true;
            var item = metadata[match];
            merged[i] = projectile with
            {
                EffectPosition = ReadMetadataVector(item.EffectPosition),
                EffectTickIndex = ReadMetadataTickIndex(item.EffectTickIndex),
                EffectSource = item.EffectSource ?? string.Empty,
                EffectConfidence = item.EffectConfidence
            };
        }

        return merged;
    }

    private static bool ProjectileMetadataMatches(
        ReplayProjectileEvent projectile,
        ReplayProjectileMetadata metadata)
    {
        return metadata.TickIndex == projectile.TickIndex &&
               ProjectileKindFromString(metadata.Kind) == projectile.Kind &&
               metadata.WeaponDefIndex == projectile.WeaponDefIndex;
    }

    private static ReplayProjectileKind ProjectileKindFromString(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "smoke" => ReplayProjectileKind.Smoke,
            "flash" => ReplayProjectileKind.Flash,
            "he" => ReplayProjectileKind.He,
            "molotov" or "incgrenade" or "incendiary" => ReplayProjectileKind.Molotov,
            "decoy" => ReplayProjectileKind.Decoy,
            _ => ReplayProjectileKind.Unknown
        };
    }

    private static ReplayVector3 ReadMetadataVector(float[]? values)
    {
        return values is { Length: >= 3 }
            ? new ReplayVector3(values[0], values[1], values[2])
            : new ReplayVector3(0.0f, 0.0f, 0.0f);
    }

    private static int ReadMetadataTickIndex(uint? value)
        => value.HasValue && value.Value <= int.MaxValue ? (int)value.Value : -1;

    private static string ReadRecString(BinaryReader reader)
    {
        var len = reader.ReadUInt16();
        EnsureRemaining(reader, len, "string in .dtr");
        var bytes = reader.ReadBytes(len);
        if (bytes.Length != len)
            throw new EndOfStreamException("truncated string in .dtr");
        return Encoding.UTF8.GetString(bytes);
    }
}
