/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text;
using System.Text.Json;

namespace DemoTracer;

internal static partial class DtrReplayReader
{
    private static void ValidateReplaySemantics(DtrReplayFile replay)
    {
        if (!float.IsFinite(replay.TickRate) || replay.TickRate <= 0.0f)
            throw new InvalidDataException("tick_rate must be finite and positive");

        for (var i = 0; i < replay.Ticks.Length; i++)
        {
            ValidateSnapshot(in replay.Ticks[i].Pre, i, " pre");
            ValidateSnapshot(in replay.Ticks[i].Post, i, " post");
            if (replay.Ticks[i].WeaponDefIndex < -1)
            {
                throw new InvalidDataException(
                    $"tick {i} weapon_def_index {replay.Ticks[i].WeaponDefIndex} is below -1");
            }
        }

        for (var i = 0; i < replay.Subticks.Length; i++)
        {
            var subtick = replay.Subticks[i];
            if (!float.IsFinite(subtick.When) || subtick.When >= 1.0f)
                throw new InvalidDataException($"subtick {i} when must be finite and below 1");
            RequireFinite(
                [subtick.Pressed, subtick.AnalogForward, subtick.AnalogLeft, subtick.PitchDelta, subtick.YawDelta],
                "subtick", i);
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
                "command frame", i);
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
                ],
                "projectile", i);
        }
    }

    private static void ValidateMovementExtra(in NativeReplayMovementExtra extra, int index)
    {
        RequireFinite(
            [
                extra.JumpPressedTime, extra.LastDuckTime,
                extra.LastActualJumpPressFrac, extra.LastUsableJumpPressFrac,
                extra.LastLandedFrac, extra.LastLandedVelocityX,
                extra.LastLandedVelocityY, extra.LastLandedVelocityZ
            ],
            "movement extra", index);
    }

    private static void ValidateSnapshot(in NativeMovementSnapshot snapshot, int index, string phase)
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
            "tick", index, phase);
        if (snapshot.Pad0 != 0 || snapshot.Pad1 != 0 || snapshot.Pad2 != 0)
            throw new InvalidDataException($"tick {index}{phase} padding must be zero");
        if (snapshot.Ducked > 1 || snapshot.Ducking > 1 || snapshot.DesiresDuck > 1)
            throw new InvalidDataException($"tick {index}{phase} duck state bytes must be 0 or 1");
    }

    private static void RequireFinite(ReadOnlySpan<float> values, string kind, int index, string suffix = "")
    {
        foreach (var value in values)
        {
            if (!float.IsFinite(value))
                throw new InvalidDataException($"{kind} {index}{suffix} contains a non-finite float");
        }
    }

    private static ReplayHighFidelityMetadata ReadHighFidelityMetadata(byte[] metadataJson, int tickCount)
    {
        var metadata = JsonSerializer.Deserialize<ReplayHighFidelityMetadata>(metadataJson, HifiJsonOptions)
            ?? ReplayHighFidelityMetadata.Empty;
        if (metadata.SchemaVersion is not 4 and not ReplayHighFidelityMetadata.CurrentSchemaVersion)
            throw new InvalidDataException("unsupported replay metadata schema; reconvert the demo with the current GUI");
        metadata.Events ??= [];
        metadata.InventorySnapshots ??= [];
        ValidateInventorySnapshots(metadata.InventorySnapshots, tickCount);
        metadata.InventorySnapshots = metadata.InventorySnapshots.OrderBy(snapshot => snapshot.TickIndex)
            .ThenBy(snapshot => snapshot.Tick).ToArray();
        if (metadata.SchemaVersion == 4)
        {
            CompileInventoryAcquisitions(metadata.InventorySnapshots);
            metadata.SchemaVersion = ReplayHighFidelityMetadata.CurrentSchemaVersion;
        }
        metadata.Events = metadata.Events.OrderBy(item => item.TickIndex).ThenBy(item => item.Tick).ToArray();
        return metadata;
    }

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
