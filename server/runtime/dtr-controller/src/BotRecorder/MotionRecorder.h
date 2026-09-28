// Motion recording & replay

#pragma once

#include <cstdint>
#include "ReplaySourceState.h"

namespace BotController
{
    // State of the player at one boundary of a movement tick. Captured twice
    // per tick: pre (before the mover runs) and post (after).
#pragma pack(push, 4)
    struct MovementSnapshot
    {
        float originX, originY, originZ; // scene node m_vecAbsOrigin
        float velX, velY, velZ;          // m_vecAbsVelocity
        float pitch, yaw, roll;          // view angles
        uint32_t entityFlags;            // m_fFlags (bit0 = FL_ONGROUND, bit1 = FL_DUCKING)
        uint8_t moveType;                // m_MoveType (MoveType_t)
        uint8_t _pad[3];                 // keep 4-byte alignment explicit
        uint64_t buttons;                // services button state1 (held at command end)
        uint64_t buttons1;               // button state2 transition plane
        uint64_t buttons2;               // button state3 transition plane
        float duckAmount;                // m_flDuckAmount (0=stand, 1=full crouch)
        float duckSpeed;                 // m_flDuckSpeed
        float ladderNormalX;             // m_vecLadderNormal (ladder anim facing)
        float ladderNormalY;
        float ladderNormalZ;
        uint8_t ducked;         // m_bDucked
        uint8_t ducking;        // m_bDucking
        uint8_t desiresDuck;    // m_bDesiresDuck
        uint8_t actualMoveType; // m_nActualMoveType (networked, ladder anim)
    };

    // One recorded server tick. numSubtick subtick moves follow this tick in
    // the parallel SubtickMove buffer
    struct ReplayTick
    {
        MovementSnapshot pre;
        MovementSnapshot post;
        int32_t weaponDefIndex; // active weapon item-def index, -1 = none
        uint32_t numSubtick;    // subtick moves for this tick, 0..36
        // ABI 20 reserves this 36-byte tail for layout compatibility only.
        // All fields must be zero; native drop capture/replay is unsupported.
        uint32_t eventFlags;
        int32_t eventWeaponDefIndex;
        uint32_t eventDropVectorFlags;
        float eventDropTargetX, eventDropTargetY, eventDropTargetZ;
        float eventDropVelocityX, eventDropVelocityY, eventDropVelocityZ;
    };

    struct SubtickMove
    {
        float when;          // finite engine-relative phase below 1; negative = backdated
        uint32_t button;     // 0 = analog, else engine button bit
        float pressed;       // digital: 1=down 0=up (stored as float)
        float analogForward; // analog_forward_delta
        float analogLeft;    // analog_left_delta
        float pitchDelta;    // pitch_delta
        float yawDelta;      // yaw_delta
    };

    struct ReplayCommandFrameData
    {
        float forwardMove;
        float leftMove;
        float upMove;
        float pitch;
        float yaw;
        float roll;
        uint64_t buttons;
        uint64_t buttons1;
        uint64_t buttons2;
        int32_t mouseDx;
        int32_t mouseDy;
        int32_t weaponSelect;
        uint32_t fields;
        uint8_t leftHandDesired;
        uint8_t _pad[3];
    };

    struct ReplayMovementExtra
    {
        uint32_t fields;
        float jumpPressedTime;
        float lastDuckTime;
        int32_t lastActualJumpPressTick;
        float lastActualJumpPressFrac;
        int32_t lastUsableJumpPressTick;
        float lastUsableJumpPressFrac;
        int32_t lastLandedTick;
        float lastLandedFrac;
        float lastLandedVelocityX;
        float lastLandedVelocityY;
        float lastLandedVelocityZ;
    };

    struct ReplayInputHistoryTick
    {
        int32_t sourceClientTick;
        int32_t attack1StartHistoryIndex;
        int32_t attack2StartHistoryIndex;
        uint32_t numEntries;
    };

    struct ReplayInputHistoryEntry
    {
        uint32_t fields;
        float viewPitch, viewYaw, viewRoll;
        int32_t renderTickCount;
        float renderTickFraction;
        int32_t playerTickCount;
        float playerTickFraction;
        float clInterpFraction;
        int32_t svInterp0SrcTick, svInterp0DstTick;
        float svInterp0Fraction;
        int32_t svInterp1SrcTick, svInterp1DstTick;
        float svInterp1Fraction;
        int32_t playerInterpSrcTick, playerInterpDstTick;
        float playerInterpFraction;
        int32_t frameNumber;
        int32_t targetEntIndex;
        float shootPositionX, shootPositionY, shootPositionZ;
        float targetHeadPosCheckX, targetHeadPosCheckY, targetHeadPosCheckZ;
        float targetAbsPosCheckX, targetAbsPosCheckY, targetAbsPosCheckZ;
        float targetAbsAngCheckX, targetAbsAngCheckY, targetAbsAngCheckZ;
    };
#pragma pack(pop)

    static_assert(sizeof(ReplayTick) == 228);
    static_assert(sizeof(ReplayCommandFrameData) == 68);
    static_assert(sizeof(ReplayMovementExtra) == 48);
    static_assert(sizeof(ReplayInputHistoryTick) == 16);
    static_assert(sizeof(ReplayInputHistoryEntry) == 128);

    namespace MotionRecorder
    {
        constexpr int kMaxSlots = 64;
        constexpr int kMaxSubtickPerTick = 36;
        constexpr uint32_t kCommandFieldForwardMove = 1u << 0;
        constexpr uint32_t kCommandFieldLeftMove = 1u << 1;
        constexpr uint32_t kCommandFieldUpMove = 1u << 2;
        constexpr uint32_t kCommandFieldViewAngles = 1u << 3;
        constexpr uint32_t kCommandFieldButtons = 1u << 4;
        constexpr uint32_t kCommandFieldMouse = 1u << 5;
        constexpr uint32_t kCommandFieldWeaponSelect = 1u << 6;
        constexpr uint32_t kCommandFieldLeftHand = 1u << 7;
        constexpr int kMaxInputHistoryPerTick = 64;
        constexpr uint32_t kInputHistoryFieldsAll = (1u << 21) - 1;

        enum class ReplayPerfCounter : int
        {
            ProcessMovementHook = 0,
            FinishMoveHook = 1,
            PlayerRunCommandHook = 2,
            PhysicsSimulateHook = 3,
            SyncReplayLocalView = 4,
            ReplayTickRead = 7,
            SubtickRebuild = 8,
            SubticksAdded = 9,
            ReplayCommandFrameRead = 10,
            SubtickClear = 11,
            SubtickNoopSkip = 12,
            ReplayMovementInput = 13,
            ReplayMovementInitialization = 14,
        };

        struct ReplayPerfCounters
        {
            uint64_t processMovementHooks;
            uint64_t finishMoveHooks;
            uint64_t playerRunCommandHooks;
            uint64_t physicsSimulateHooks;
            uint64_t syncReplayLocalViewCalls;
            uint64_t virtualQueryCalls; // Reserved ABI field; always zero.
            uint64_t replayTickReads;
            uint64_t subtickRebuilds;
            uint64_t subticksAdded;
            uint64_t replayCommandFrameReads;
            uint64_t subtickClears;
            uint64_t subtickNoopSkips;
            uint64_t movementInputs;
            uint64_t movementInitializations;
        };

        struct ReplaySlotState
        {
            int32_t playing;
            int32_t cursor;
            int32_t total;
            int32_t currentTickIndex;
            int32_t weaponDefIndex;
            int32_t numSubtick;
        };

        struct ReplayCommandFrame
        {
            const ReplayTick *tick;
            const SubtickMove *subticks;
            const ReplayCommandFrameData *command;
            int32_t subtickCount;
            int32_t weaponSelect;
            MovementSnapshot commandView;
            uint64_t buttons0;
            uint64_t buttons1;
            uint64_t buttons2;
            uint32_t commandFields;
            float forwardMove;
            float leftMove;
            float upMove;
            int32_t mouseDx;
            int32_t mouseDy;
            int32_t rawWeaponSelect;
            uint8_t leftHandDesired;
        };

        void SetReplayPerfEnabled(bool enabled);
        bool ReplayPerfEnabled();
        void ResetReplayPerfCounters();
        ReplayPerfCounters GetReplayPerfCounters();
        void AddReplayPerf(ReplayPerfCounter counter, uint64_t amount = 1);

        // ---- recording ----
        bool StartRecord(int slot); // clears old buffer, begins capture
        bool StopRecord(int slot);  // stops
        bool ClearRecordedMotion(int slot); // stops and releases recorded buffers
        bool IsRecording(int slot);
        int RecordedTickCount(int slot);    // <0 on bad slot
        int RecordedSubtickCount(int slot); // <0 on bad slot

        // ProcessMovement hook: capture pre snapshot
        void OnCapturePre(int slot, void *services, void *cmd);
        // ProcessMovement hook: capture post snapshot + commit the tick
        void OnCapturePost(int slot, void *services, void *cmd);
        // PlayerRunCommand hook: stash this tick's subtick moves (pending).
        void OnCaptureSubticks(int slot, const SubtickMove *moves, int count);

        // Track which WeaponServices* maps to this recording slot
        void SetLiveWs(int slot, void *ws);
        void *LiveWs(int slot);
        // SelectItem tap: update the slot's current weapon def index.
        void SetCurrentDef(int slot, int defIndex);

        // Copy recorded data out to caller buffers; returns elements written.
        int CopyTicks(int slot, ReplayTick *out, int maxTicks);
        int CopySubticks(int slot, SubtickMove *out, int maxSubticks);
        void OnCaptureCommand(int slot, const ReplayCommandFrameData &command);
        int RecordedCommandCount(int slot);
        int CopyCommands(int slot, ReplayCommandFrameData *out, int maxCommands);

        // ---- replay ----
        // Load parallel arrays into a slot's replay buffer
        bool LoadReplay(int slot, const ReplayTick *ticks, int tickCount,
                        const SubtickMove *subs, int subCount) noexcept;
        bool LoadReplayExtended(int slot, const ReplayTick *ticks, int tickCount,
                                const SubtickMove *subs, int subCount,
                                const ReplayCommandFrameData *commands,
                                int commandCount,
                                const ReplayMovementExtra *movementExtras,
                                int movementExtraCount) noexcept;
        bool LoadReplayWithInputHistory(
            int slot, const ReplayTick *ticks, int tickCount,
            const SubtickMove *subs, int subCount,
            const ReplayCommandFrameData *commands, int commandCount,
            const ReplayMovementExtra *movementExtras, int movementExtraCount,
            const ReplayInputHistoryTick *inputHistoryTicks, int inputHistoryTickCount,
            const ReplayInputHistoryEntry *inputHistoryEntries, int inputHistoryEntryCount) noexcept;
        bool LoadReplaySourceState(int slot, const ReplaySourceState::Change *changes, int count, float tickRate, float liveTickInterval);
        bool StartReplay(int slot, bool loop); // play from tick 0
        bool StartReplayAt(int slot, bool loop, int startIndex);
        bool StartReplayUntil(int slot, bool loop, int startIndex, int holdBeforeIndex);
        // End active execution once; an already stopped slot's newer inputs
        // and equipment belong to its next owner and must remain untouched.
        bool StopReplay(int slot);
        // Stop active execution if needed, then return buffer capacity to the
        // allocator. Disposing an already stopped buffer only changes storage.
        bool ReleaseReplayBuffer(int slot);
        bool IsReplaying(int slot);
        int ReplayCursor(int slot); // current tick index, <0 if idle
        int ReplayTotal(int slot);  // loaded tick count
        bool GetReplaySlotState(int slot, ReplaySlotState &out);

        // Current tick being applied this server tick
        bool ReplayTickForSimulation(int slot, ReplayTick &out);
        bool ReplayCommandFrameForSimulation(int slot, ReplayCommandFrame &out);
        // Snapshot to return from replay-owned eye-angle getters. This uses
        // the last post view prepared by FinishMove when available, so camera
        // readers do not jump one tick ahead after cursor advance.
        bool ReplaySpectatorView(int slot, MovementSnapshot &out);
        // Last tick already applied; used by external status readers.
        bool CurrentReplayTick(int slot, ReplayTick &out);
        // Initialize movement once at a start/seek/loop boundary, then prepare
        // the command view. Returns false without injecting a command on failure.
        bool OnReplayCommandPre(int slot, void *services, const ReplayTick &tick,
                                const MovementSnapshot &commandView);
        // PlayerRunCommand (post): if FinishMove naturally stopped replay inside
        // the command, scrub any replay state restored by the remaining tail.
        void OnReplayCommandPost(int slot, void *services, bool wasReplaying);

        // Switch a bot to the weapon with this def index.
        bool SwitchBotWeaponByDef(int slot, int defIndex);

        // Def index of the weapon the bot currently holds (live engine read),
        // same normalization as recorded WeaponDefIndex (knife -> kKnifeDef).
        // -1 if no ws / no active weapon. For C# to reconcile replay weapon.
        int BotActiveWeaponDef(int slot);

        // ---- replay write hooks ----
        // SetupMove (post): supply the demo's pre kinematics once, before
        // engine movement/subticks. FinishMove owns the resulting pawn state.
        void OnReplaySetupMove(int slot, void *moveData);
        // FinishMove (post): prepare the getter for the engine network publication.
        void OnReplayFinalView(int slot, void *services);
        // After command publication: advance the cursor without replacing
        // engine-computed movement, ground, duck or ladder state.
        void OnReplayCommit(int slot, void *services);

        void ClearAll(); // wipe all record + replay buffers (on unload)
    }
}
