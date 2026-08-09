/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace DemoTracer;

internal static partial class BotControllerNative
{
    public const int ExpectedAbiVersion = 18;
    public const uint RecFormatVersion = 9;
    public const uint MinRecFormatVersion = 3;
    public const int MovementSnapshotByteSize = 92;
    public const int ReplayTickByteSize = 192;
    public const int SubtickMoveByteSize = 28;
    public const int ReplayCommandFrameByteSize = 68;
    public const int ReplayMovementExtraByteSize = 48;
    public const int ReplayInputHistoryTickByteSize = 16;
    public const int ReplayInputHistoryEntryByteSize = 128;
    public const int ProjectileBirthAlignStatusByteSize = 36;
    internal const uint CommandFieldLeftHand = 1U << 7;
    public const int ReplaySlotStateByteSize = 24;
    public const int MaxSlots = 64;
    public const int DemoTracerApiVersion = 7;

    internal const int LockKindAll = 0;
    internal const int LockKindAim = 1;
    internal const int LockKindWeapon = 2;

    private const ulong CapabilityReplaySlotState = 1UL << 0;
    private const ulong CapabilityStartReplayAt = 1UL << 1;
    private const ulong CapabilityStartReplayUntil = 1UL << 2;
    private const ulong CapabilityReplayTick = 1UL << 3;
    private const ulong CapabilityWeaponSwitchRead = 1UL << 4;
    private const ulong CapabilityPovMask = 1UL << 5;
    private const ulong CapabilityBuyPlan = 1UL << 6;
    private const ulong CapabilityControllerBotOffset = 1UL << 7;
    internal const ulong CapabilityExtendedReplay = 1UL << 8;
    internal const ulong CapabilityUsercmdMovementIntent = 1UL << 9;
    internal const ulong CapabilityVoiceSend = 1UL << 10;
    internal const ulong CapabilityNativePerception = 1UL << 11;
    internal const ulong CapabilityReleaseReplayBuffer = 1UL << 12;
    internal const ulong CapabilityButtonOnlyMovementIntent = 1UL << 13;
    internal const ulong CapabilityHandoffBestWeapon = 1UL << 14;
    internal const ulong CapabilityReplayInputHistory = 1UL << 15;
    internal const int MovementIntentPreserveMoveAxes = 1 << 0;

    public const ulong RequiredCapabilityMask =
        CapabilityReplaySlotState |
        CapabilityStartReplayAt |
        CapabilityStartReplayUntil |
        CapabilityReplayTick |
        CapabilityWeaponSwitchRead |
        CapabilityPovMask |
        CapabilityBuyPlan |
        CapabilityControllerBotOffset |
        CapabilityExtendedReplay |
        CapabilityHandoffBestWeapon |
        CapabilityReplayInputHistory;

    public static string RuntimePlatformName
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "windows-x64"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "linux-x64"
                : RuntimeInformation.OSDescription;

    internal static void EnsureNativeLayout()
    {
        var snapshotSize = Marshal.SizeOf<NativeMovementSnapshot>();
        if (snapshotSize != MovementSnapshotByteSize)
            throw new InvalidOperationException($"MovementSnapshot layout is {snapshotSize}, expected {MovementSnapshotByteSize}");

        var tickSize = Marshal.SizeOf<NativeReplayTick>();
        if (tickSize != ReplayTickByteSize)
            throw new InvalidOperationException($"ReplayTick layout is {tickSize}, expected {ReplayTickByteSize}");

        var perceptionSize = Marshal.SizeOf<NativePerceptionState>();
        if (perceptionSize != NativePerceptionState.ByteSize)
            throw new InvalidOperationException($"NativePerceptionState layout is {perceptionSize}, expected {NativePerceptionState.ByteSize}");

        var subtickSize = Marshal.SizeOf<NativeSubtickMove>();
        if (subtickSize != SubtickMoveByteSize)
            throw new InvalidOperationException($"SubtickMove layout is {subtickSize}, expected {SubtickMoveByteSize}");

        var commandFrameSize = Marshal.SizeOf<NativeReplayCommandFrame>();
        if (commandFrameSize != ReplayCommandFrameByteSize)
            throw new InvalidOperationException($"ReplayCommandFrame layout is {commandFrameSize}, expected {ReplayCommandFrameByteSize}");

        var movementExtraSize = Marshal.SizeOf<NativeReplayMovementExtra>();
        if (movementExtraSize != ReplayMovementExtraByteSize)
            throw new InvalidOperationException($"ReplayMovementExtra layout is {movementExtraSize}, expected {ReplayMovementExtraByteSize}");

        var inputHistoryTickSize = Marshal.SizeOf<NativeReplayInputHistoryTick>();
        if (inputHistoryTickSize != ReplayInputHistoryTickByteSize)
            throw new InvalidOperationException($"ReplayInputHistoryTick layout is {inputHistoryTickSize}, expected {ReplayInputHistoryTickByteSize}");

        var inputHistoryEntrySize = Marshal.SizeOf<NativeReplayInputHistoryEntry>();
        if (inputHistoryEntrySize != ReplayInputHistoryEntryByteSize)
            throw new InvalidOperationException($"ReplayInputHistoryEntry layout is {inputHistoryEntrySize}, expected {ReplayInputHistoryEntryByteSize}");

        var projectileBirthAlignStatusSize = Marshal.SizeOf<NativeProjectileBirthAlignStatus>();
        if (projectileBirthAlignStatusSize != ProjectileBirthAlignStatusByteSize)
            throw new InvalidOperationException($"ProjectileBirthAlignStatus layout is {projectileBirthAlignStatusSize}, expected {ProjectileBirthAlignStatusByteSize}");
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct BotControllerAbiInfo
{
    public const int ByteSize = 44;

    public int AbiMajor;
    public int AbiMinor;
    public int MovementSnapshotSize;
    public int ReplayTickSize;
    public int SubtickMoveSize;
    public int ReplaySlotStateSize;
    public int MaxSlots;
    public ulong Capabilities;
    public int Reserved0;
    public int Reserved1;

    public static BotControllerAbiInfo Unavailable => new()
    {
        AbiMajor = -1,
        AbiMinor = 0,
        MovementSnapshotSize = BotControllerNative.MovementSnapshotByteSize,
        ReplayTickSize = BotControllerNative.ReplayTickByteSize,
        SubtickMoveSize = BotControllerNative.SubtickMoveByteSize,
        ReplaySlotStateSize = BotControllerNative.ReplaySlotStateByteSize,
        MaxSlots = BotControllerNative.MaxSlots,
        Capabilities = 0
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeProjectileBirthAlignStatus
{
    public int Size;
    public int Configured;
    public int Pending;
    public int Queued;
    public int Applied;
    public int Expired;
    public int Failed;
    public int InitialPositionOffset;
    public int InitialVelocityOffset;
}

internal readonly record struct ReplayFileMetadata(
    float TickRate,
    uint PlayStartTickIndex,
    int TickCount,
    ReplayProjectileEvent[] Projectiles,
    ReplayHighFidelityMetadata HighFidelity,
    int[] WeaponDefIndices,
    ReplayVector3? RoundStartOrigin)
{
    public static ReplayFileMetadata Empty { get; } = new(
        0.0f,
        0,
        0,
        [],
        ReplayHighFidelityMetadata.Empty,
        [],
        null);
}

internal readonly record struct ReplayState(
    int Cursor,
    int Total,
    bool Playing,
    int CurrentTickIndex,
    int WeaponDefIndex,
    int NumSubtick)
{
    public static ReplayState Empty { get; } = new(-1, 0, false, -1, -1, 0);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeReplaySlotState
{
    public int Playing;
    public int Cursor;
    public int Total;
    public int CurrentTickIndex;
    public int WeaponDefIndex;
    public int NumSubtick;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativePerceptionState
{
    public const int ByteSize = 44;

    public int Valid;
    public uint EnemyHandle;
    public int HasEnemy;
    public int EnemyVisible;
    public int VisibleEnemyParts;
    public int NearbyEnemyCount;
    public int LastEnemyDead;
    public float LastSawEnemyTimestamp;
    public float FirstSawEnemyTimestamp;
    public float CurrentEnemyAcquireTimestamp;
    public uint UpdateSerial;
}

internal enum ReplayProjectileKind : byte
{
    Unknown = 0,
    Smoke = 1,
    Flash = 2,
    He = 3,
    Molotov = 4,
    Decoy = 5
}

internal readonly record struct ReplayVector3(float X, float Y, float Z);

internal readonly record struct ReplayProjectileEvent(
    uint TickIndex,
    ReplayProjectileKind Kind,
    int WeaponDefIndex,
    ReplayVector3 InitialPosition,
    ReplayVector3 InitialVelocity,
    ReplayVector3 DetonationPosition,
    ReplayVector3 EffectPosition,
    int EffectTickIndex,
    string EffectSource,
    float EffectConfidence);

internal sealed class ReplayHighFidelityMetadata
{
    public static ReplayHighFidelityMetadata Empty { get; } = new();

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 4;

    [JsonPropertyName("round_start_balance")]
    public uint? RoundStartBalance { get; set; }

    [JsonPropertyName("events")]
    public ReplayHifiEvent[] Events { get; set; } = [];

    [JsonPropertyName("inventory_snapshots")]
    public ReplayInventorySnapshot[] InventorySnapshots { get; set; } = [];

    [JsonPropertyName("projectiles")]
    public ReplayProjectileMetadata[] Projectiles { get; set; } = [];
}

internal sealed class ReplayProjectileMetadata
{
    [JsonPropertyName("tick_index")]
    public uint TickIndex { get; set; }

    [JsonPropertyName("tick")]
    public int Tick { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("weapon_def_index")]
    public int WeaponDefIndex { get; set; }

    [JsonPropertyName("effect_tick_index")]
    public uint? EffectTickIndex { get; set; }

    [JsonPropertyName("effect_tick")]
    public int? EffectTick { get; set; }

    [JsonPropertyName("effect_position")]
    public float[]? EffectPosition { get; set; }

    [JsonPropertyName("effect_source")]
    public string EffectSource { get; set; } = string.Empty;

    [JsonPropertyName("effect_confidence")]
    public float EffectConfidence { get; set; }
}

internal sealed class ReplayHifiEvent
{
    [JsonPropertyName("tick_index")]
    public uint TickIndex { get; set; }

    [JsonPropertyName("tick")]
    public int Tick { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("actor_steam_id")]
    public ulong? ActorSteamId { get; set; }

    [JsonPropertyName("target_steam_id")]
    public ulong? TargetSteamId { get; set; }

    [JsonPropertyName("weapon_def_index")]
    public int? WeaponDefIndex { get; set; }

    [JsonPropertyName("item_name")]
    public string? ItemName { get; set; }

    [JsonPropertyName("entity_id")]
    public int? EntityId { get; set; }

    [JsonPropertyName("actor_count_after")]
    public int? ActorCountAfter { get; set; }

    [JsonPropertyName("target_count_after")]
    public int? TargetCountAfter { get; set; }

    [JsonPropertyName("damage")]
    public int? Damage { get; set; }

    [JsonPropertyName("health")]
    public int? Health { get; set; }
}

internal sealed class ReplayInventorySnapshot
{
    [JsonPropertyName("tick_index")]
    public uint TickIndex { get; set; }

    [JsonPropertyName("tick")]
    public int Tick { get; set; }

    [JsonPropertyName("steam_id")]
    public ulong SteamId { get; set; }

    [JsonPropertyName("weapon_def_counts")]
    public ReplayInventoryItemCount[] WeaponDefCounts { get; set; } = [];

    [JsonPropertyName("active_weapon_def_index")]
    public int ActiveWeaponDefIndex { get; set; }

    [JsonPropertyName("armor_value")]
    public int ArmorValue { get; set; }

    [JsonPropertyName("has_helmet")]
    public bool HasHelmet { get; set; }

    [JsonPropertyName("has_defuser")]
    public bool HasDefuser { get; set; }
}

internal sealed class ReplayInventoryItemCount
{
    [JsonPropertyName("weapon_def_index")]
    public int WeaponDefIndex { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeMovementSnapshot
{
    public float OriginX, OriginY, OriginZ;
    public float VelX, VelY, VelZ;
    public float Pitch, Yaw, Roll;
    public uint EntityFlags;
    public byte MoveType;
    public byte Pad0, Pad1, Pad2;
    public ulong Buttons;
    public ulong Buttons1;
    public ulong Buttons2;
    public float DuckAmount;
    public float DuckSpeed;
    public float LadderNormalX;
    public float LadderNormalY;
    public float LadderNormalZ;
    public byte Ducked;
    public byte Ducking;
    public byte DesiresDuck;
    public byte ActualMoveType;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeReplayTick
{
    public NativeMovementSnapshot Pre;
    public NativeMovementSnapshot Post;
    public int WeaponDefIndex;
    public uint NumSubtick;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeSubtickMove
{
    public float When;
    public uint Button;
    public float Pressed;
    public float AnalogForward;
    public float AnalogLeft;
    public float PitchDelta;
    public float YawDelta;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeReplayCommandFrame
{
    public float ForwardMove;
    public float LeftMove;
    public float UpMove;
    public float Pitch;
    public float Yaw;
    public float Roll;
    public ulong Buttons;
    public ulong Buttons1;
    public ulong Buttons2;
    public int MouseDx;
    public int MouseDy;
    public int WeaponSelect;
    public uint Fields;
    public byte LeftHandDesired;
    public byte Pad0;
    public byte Pad1;
    public byte Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeReplayMovementExtra
{
    public uint Fields;
    public float JumpPressedTime;
    public float LastDuckTime;
    public int LastActualJumpPressTick;
    public float LastActualJumpPressFrac;
    public int LastUsableJumpPressTick;
    public float LastUsableJumpPressFrac;
    public int LastLandedTick;
    public float LastLandedFrac;
    public float LastLandedVelocityX;
    public float LastLandedVelocityY;
    public float LastLandedVelocityZ;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeReplayInputHistoryTick
{
    public int SourceClientTick;
    public int Attack1StartHistoryIndex;
    public int Attack2StartHistoryIndex;
    public uint NumEntries;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeReplayInputHistoryEntry
{
    public uint Fields;
    public float ViewPitch, ViewYaw, ViewRoll;
    public int RenderTickCount;
    public float RenderTickFraction;
    public int PlayerTickCount;
    public float PlayerTickFraction;
    public float ClInterpFraction;
    public int SvInterp0SrcTick, SvInterp0DstTick;
    public float SvInterp0Fraction;
    public int SvInterp1SrcTick, SvInterp1DstTick;
    public float SvInterp1Fraction;
    public int PlayerInterpSrcTick, PlayerInterpDstTick;
    public float PlayerInterpFraction;
    public int FrameNumber;
    public int TargetEntIndex;
    public float ShootPositionX, ShootPositionY, ShootPositionZ;
    public float TargetHeadPosCheckX, TargetHeadPosCheckY, TargetHeadPosCheckZ;
    public float TargetAbsPosCheckX, TargetAbsPosCheckY, TargetAbsPosCheckZ;
    public float TargetAbsAngCheckX, TargetAbsAngCheckY, TargetAbsAngCheckZ;
}
