/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Runtime.InteropServices;
namespace DemoTracer;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeReplaySourceStateChange
{
    public uint TickIndex, FieldId, ValueBits, Present;
}
internal static partial class DtrReplayReader
{
    private enum SourceKind : byte { F32, I32, U32, Bool }
    // Keep IDs/types aligned with replay-source-fields.v1.json.
    private static readonly SourceKind[] SourceKinds =
    [
        SourceKind.U32, // ServerTick
        SourceKind.U32, // PlayerTick
        SourceKind.F32, // DuckRoot
        SourceKind.F32, // DuckView
        SourceKind.F32, // LastDuckTime
        SourceKind.Bool, // DuckOverride
        SourceKind.F32, // Stamina
        SourceKind.F32, // DuckAmount
        SourceKind.F32, // DuckSpeed
        SourceKind.Bool, // Ducked
        SourceKind.Bool, // Ducking
        SourceKind.Bool, // DesiresDuck
        SourceKind.I32, // LastJumpTick
        SourceKind.F32, // LastJumpFrac
        SourceKind.F32, // LastJumpVelocityZ
        SourceKind.Bool, // GroundTopology
        SourceKind.F32, // GroundTopologySmoothing
        SourceKind.F32, // FrictionStashedSpeed
        SourceKind.Bool, // UseFrictionStashedSpeed
        SourceKind.F32, // FrictionStashedUntilFrac
        SourceKind.I32, // LadderSurface
        SourceKind.F32, // FallVelocity
        SourceKind.I32, // LastActualJumpPressTick
        SourceKind.F32, // LastActualJumpPressFrac
        SourceKind.I32, // LastUsableJumpPressTick
        SourceKind.F32, // LastUsableJumpPressFrac
        SourceKind.I32, // LastLandedTick
        SourceKind.F32, // LastLandedFrac
        SourceKind.F32, // LastLandedVelocityX
        SourceKind.F32, // LastLandedVelocityY
        SourceKind.F32, // LastLandedVelocityZ
        SourceKind.F32, // VelocityModifier
        SourceKind.F32, // Friction
        SourceKind.F32, // GravityScale
        SourceKind.Bool, // GravityDisabled
        SourceKind.I32, // ShotsFired
        SourceKind.Bool, // Scoped
        SourceKind.F32, // BaseVelocityX
        SourceKind.F32, // BaseVelocityY
        SourceKind.F32, // BaseVelocityZ
        SourceKind.F32, // PredictableAngleX
        SourceKind.F32, // PredictableAngleY
        SourceKind.F32, // PredictableAngleZ
        SourceKind.F32, // PredictableAngleVelX
        SourceKind.F32, // PredictableAngleVelY
        SourceKind.F32, // PredictableAngleVelZ
        SourceKind.F32, // UnpredictableAngleX
        SourceKind.F32, // UnpredictableAngleY
        SourceKind.F32, // UnpredictableAngleZ
        SourceKind.I32, // PredictableTick
        SourceKind.F32, // PredictableTickFrac
        SourceKind.I32, // UnpredictableTick
        SourceKind.I32, // Clip1
        SourceKind.I32, // Clip2
        SourceKind.Bool, // InReload
        SourceKind.I32, // NextPrimaryTick
        SourceKind.F32, // NextPrimaryFrac
        SourceKind.I32, // NextSecondaryTick
        SourceKind.F32, // NextSecondaryFrac
        SourceKind.F32, // RecoilIndex
        SourceKind.F32, // AccuracyPenalty
        SourceKind.F32, // LastShotTime
        SourceKind.I32, // BurstShotsRemaining
        SourceKind.F32, // NextAttack
        SourceKind.U32, // ActiveWeaponHandle
        SourceKind.I32, // ReserveAmmoPrimary
        SourceKind.I32, // ReserveAmmoSecondary
    ];

    private static NativeReplaySourceStateChange[] ReadSourceState(byte[] body, int count, int tickCount)
    {
        using var reader = new BinaryReader(new MemoryStream(body, writable: false));
        var changes = new NativeReplaySourceStateChange[count];
        (uint Tick, uint Field)? previous = null;
        for (var i = 0; i < count; ++i)
        {
            var change = new NativeReplaySourceStateChange
            {
                TickIndex = reader.ReadUInt32(),
                FieldId = reader.ReadUInt32(),
                ValueBits = reader.ReadUInt32(),
                Present = reader.ReadUInt32()
            };
            var key = (change.TickIndex, change.FieldId);
            if (change.TickIndex >= tickCount || change.FieldId >= SourceKinds.Length || change.Present > 1 ||
                (previous.HasValue && previous.Value.CompareTo(key) >= 0) ||
                (change.Present == 0 && change.ValueBits != 0) ||
                (change.Present == 1 && SourceKinds[change.FieldId] == SourceKind.F32 && !float.IsFinite(BitConverter.UInt32BitsToSingle(change.ValueBits))) ||
                (change.Present == 1 && SourceKinds[change.FieldId] == SourceKind.Bool && change.ValueBits > 1))
                throw new InvalidDataException("invalid or unordered source state change");
            changes[i] = change;
            previous = key;
        }
        return changes;
    }
}
