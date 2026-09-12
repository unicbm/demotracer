// Shared data types for the BotController API.

using System.Runtime.InteropServices;

namespace BotControllerApi
{
    // Lock category.
    //   All    - freezes both CCSBot::Update and CCSBot::Upkeep
    //   Aim    - freezes CCSBot::Upkeep only
    //   Weapon - locks the bot's weapon to a specific engine slot
    public enum LockKind
    {
        All = 0,
        Aim = 1,
        Weapon = 2,
    }

    // Engine weapon slots.
    public enum LockTarget
    {
        None = 0,
        Slot1 = 1,
        Slot2 = 2,
        Slot3 = 3,
        Slot4 = 4,
        Slot5 = 5,
    }

    /** One boundary of a movement tick. Captured pre (before mover) and post (after) */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct MovementSnapshot
    {
        public float OriginX, OriginY, OriginZ;
        public float VelX, VelY, VelZ;
        public float Pitch, Yaw, Roll;
        public uint EntityFlags;
        public byte MoveType;
        public byte Pad0, Pad1, Pad2;
        public ulong Buttons;        // states[0] (pressed)
        public ulong Buttons1;       // states[1]
        public ulong Buttons2;       // states[2]
        public float DuckAmount;     // m_flDuckAmount (0=stand, 1=full crouch)
        public float DuckSpeed;      // m_flDuckSpeed
        public float LadderNormalX;  // m_vecLadderNormal
        public float LadderNormalY;
        public float LadderNormalZ;
        public byte Ducked;         // m_bDucked
        public byte Ducking;        // m_bDucking
        public byte DesiresDuck;    // m_bDesiresDuck
        public byte ActualMoveType; // m_nActualMoveType
    }

    /** One recorded server tick. Must match C++ ReplayTick byte layout exactly */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ReplayTick
    {
        public MovementSnapshot Pre;
        public MovementSnapshot Post;
        public int WeaponDefIndex;
        public uint NumSubtick;
        // Reserved ABI 20 layout tail: every field must be zero.
        // Native weapon-drop capture/replay is unsupported.
        public uint EventFlags;
        public int EventWeaponDefIndex;
        public uint EventDropVectorFlags;
        public float EventDropTargetX;
        public float EventDropTargetY;
        public float EventDropTargetZ;
        public float EventDropVelocityX;
        public float EventDropVelocityY;
        public float EventDropVelocityZ;
    }

    /** One subtick input step. Must match C++ SubtickMove byte layout exactly */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct SubtickMove
    {
        public float When;
        public uint Button;
        public float Pressed;
        public float AnalogForward;
        public float AnalogLeft;
        public float PitchDelta;
        public float YawDelta;
    }

    /** Optional replay usercmd frame. Must match C++ ReplayCommandFrameData */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ReplayCommandFrame
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

    /** Optional offset-backed replay movement state. Must match C++ ReplayMovementExtra */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ReplayMovementExtra
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

    public enum ReplayProjectileKind : byte
    {
        Unknown = 0,
        Smoke = 1,
        Flash = 2,
        He = 3,
        Molotov = 4,
        Decoy = 5,
    }

    public struct ReplayVector3
    {
        public float X;
        public float Y;
        public float Z;
    }

    public sealed class ReplayProjectileEvent
    {
        public uint TickIndex;
        public int WeaponDefIndex;
        public ReplayProjectileKind Kind;
        public ReplayVector3 InitialPosition;
        public ReplayVector3 InitialVelocity;
        public ReplayVector3 DetonationPosition;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ProjectileBirthAlignStatus
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

    public static class ReplayProjectileMatcher
    {
        private const int MaxCursorDistance = 96;

        // Maps a projectile designer name to its replay kind and item definition
        public static bool TryGetKind(
            string? designerName,
            out ReplayProjectileKind kind,
            out int weaponDefIndex)
        {
            kind = ReplayProjectileKind.Unknown;
            weaponDefIndex = -1;
            if (string.IsNullOrWhiteSpace(designerName)) return false;

            if (designerName.Contains("smokegrenade_projectile", StringComparison.OrdinalIgnoreCase))
            {
                kind = ReplayProjectileKind.Smoke;
                weaponDefIndex = 45;
            }
            else if (designerName.Contains("flashbang_projectile", StringComparison.OrdinalIgnoreCase))
            {
                kind = ReplayProjectileKind.Flash;
                weaponDefIndex = 43;
            }
            else if (designerName.Contains("hegrenade_projectile", StringComparison.OrdinalIgnoreCase) ||
                     designerName.Contains("he_grenade_projectile", StringComparison.OrdinalIgnoreCase))
            {
                kind = ReplayProjectileKind.He;
                weaponDefIndex = 44;
            }
            else if (designerName.Contains("incgrenade_projectile", StringComparison.OrdinalIgnoreCase) ||
                     designerName.Contains("incendiarygrenade_projectile", StringComparison.OrdinalIgnoreCase))
            {
                kind = ReplayProjectileKind.Molotov;
                weaponDefIndex = 48;
            }
            else if (designerName.Contains("molotov_projectile", StringComparison.OrdinalIgnoreCase))
            {
                kind = ReplayProjectileKind.Molotov;
                weaponDefIndex = 46;
            }
            else if (designerName.Contains("decoy_projectile", StringComparison.OrdinalIgnoreCase))
            {
                kind = ReplayProjectileKind.Decoy;
                weaponDefIndex = 47;
            }

            return kind != ReplayProjectileKind.Unknown;
        }

        // Finds the closest unused projectile event around the live replay cursor
        public static int FindNext(
            IReadOnlyList<ReplayProjectileEvent> events,
            int start,
            int cursor,
            ReplayProjectileKind kind,
            int weaponDefIndex)
        {
            int best = -1;
            int bestDistance = int.MaxValue;
            for (int i = Math.Max(start, 0); i < events.Count; ++i)
            {
                ReplayProjectileEvent candidate = events[i];
                if (candidate.Kind != kind ||
                    !WeaponDefMatches(kind, weaponDefIndex, candidate.WeaponDefIndex))
                {
                    continue;
                }

                int distance = Math.Abs((int)candidate.TickIndex - cursor);
                if (distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
                if ((int)candidate.TickIndex > cursor + MaxCursorDistance) break;
            }
            return bestDistance <= MaxCursorDistance ? best : -1;
        }

        // Treats both faction fire grenades as the same replay projectile kind
        public static bool WeaponDefMatches(
            ReplayProjectileKind kind,
            int liveWeaponDefIndex,
            int replayWeaponDefIndex)
        {
            if (liveWeaponDefIndex <= 0 || replayWeaponDefIndex <= 0) return true;
            if (liveWeaponDefIndex == replayWeaponDefIndex) return true;
            return kind == ReplayProjectileKind.Molotov &&
                   liveWeaponDefIndex is 46 or 48 &&
                   replayWeaponDefIndex is 46 or 48;
        }

        // Returns whether every vector component is finite
        public static bool IsFinite(ReplayVector3 vector)
            => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);

        // Returns whether a finite vector contains a usable non-zero value
        public static bool IsMeaningful(ReplayVector3 vector)
            => IsFinite(vector) && vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z > 0.0001f;
    }

    /** Bot personality / aim / weapon preference. Mirrors C++ BotProfileData */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct BotProfileData
    {
        public float Aggression;    // 0..1
        public float Skill;         // 0..1
        public float Teamwork;      // 0..1
        public float ReactionTime;  // seconds
        public float AttackDelay;   // seconds
        public float LookAccelAtk;  // m_lookAngleMaxAccelAttacking
        public float LookStiffAtk;  // m_lookAngleStiffnessAttacking
        public float LookDampAtk;   // m_lookAngleDampingAttacking
        public int Cost;
        public int Difficulty;      // bitmask EASY/NORMAL/HARD/EXPERT
        public int WeaponPrefCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public ushort[] WeaponPref; // item def index, [0..WeaponPrefCount)
    }
}
