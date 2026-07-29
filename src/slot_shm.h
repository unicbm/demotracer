// slot_shm.h
//
// Shared-memory wire format between BotHider (CSS and C++)

#pragma once

#include <cstdint>

namespace cs2bh::shm
{

    // Windows named mapping / POSIX shm name.
#if defined(_WIN32)
    inline constexpr const char *kMappingName = "Local\\CS2BotHider_Slots";
#else
    inline constexpr const char *kMappingName = "/CS2BotHider_Slots";
#endif

    inline constexpr uint32_t kMagic = 0x44494842; // 'BHID'
    inline constexpr uint32_t kVersion = 2;
    inline constexpr int kMaxSlots = 64;
    inline constexpr int kNameLen = 32;  // persona name buffer (incl. NUL)
    inline constexpr int kCmdCount = 64; // ring-buffer entries

    // Data region
    inline constexpr int kOff_Magic = 0;         // uint32
    inline constexpr int kOff_Version = 4;       // uint32
    inline constexpr int kOff_MaxSlots = 8;      // uint32
    inline constexpr int kOff_DataGen = 12;      // uint32, bumped on each write
    inline constexpr int kOff_SlotState = 16;    // byte[64]   0=unmanaged 1=managed
    inline constexpr int kOff_SyntheticSid = 80; // uint64[64]
    inline constexpr int kOff_PersonaName = 592; // char[64][32]

    // Command region
    inline constexpr int kOff_WriteIdx = 2640; // uint32, C# Interlocked bump
    inline constexpr int kOff_ReadIdx = 2644;  // uint32, C++ bump
    inline constexpr int kOff_Cmds = 2648;     // Command[64], 48B each
    // Ends at 2648 + 64*48 = 5720

    // Extra data region
    inline constexpr int kCrosshairLen = 64;      // crosshair code buffer
    inline constexpr int kOff_CurrentPing = 5720; // int32[64]  jittered ping
    inline constexpr int kOff_Crosshair = 5976;   // char[64][64]
    // Ends at 5976 + 64*64 = 10072

    // Signature/hook status region
    inline constexpr int kMaxSigs = 8;            // capacity
    inline constexpr int kSigNameLen = 32;        // name buffer (incl. NUL)
    inline constexpr int kSigEntrySize = 40;      // char[32] name + uint64 addr
    inline constexpr int kOff_SigCount = 10072;   // uint32, number of valid entries
    inline constexpr int kOff_SigEntries = 10080; // SigEntry[kMaxSigs], 8-byte aligned
    // Ends at 10080 + 8*40 = 10400

    // Scoreboard flair region
    inline constexpr int kOff_ScoreboardFlair = 10400; // uint32[64]
    // Ends at 10400 + 64*4 = 10656

    // Immutable persona base for the current managed-slot incarnation. The
    // older SyntheticSid/PersonaName fields remain the effective values that
    // native SET commands publish to the engine.
    inline constexpr int kOff_BaseSyntheticSid = 10656; // uint64[64]
    inline constexpr int kOff_BasePersonaName = 11168;  // char[64][32]
    // Ends at 11168 + 64*32 = 13216

    inline constexpr int kTotalSize = 16384; // 4 pages

    // Command opcodes.
    enum CmdType : uint8_t
    {
        kCmd_None = 0,
        kCmd_SetSteamId = 1, // exact value validated by the presentation lease API
        kCmd_SetPersona = 2,
        kCmd_SetDisguise = 3, // global toggle, on/off carried in Command.SteamId
        kCmd_Rebuild = 4,     // global clean-rebuild on same-map rematch
        // 5 (KickAll) and 6 (Refill) match-end clean-rebuild removed
        kCmd_SetNameSource = 7, // global toggle, name source carried in Command.SteamId (1=bot_info 0=botprofile)
    };

    // Sentinel slot for global commands
    inline constexpr uint8_t kSlot_All = 255;

// One ring-buffer command.
#pragma pack(push, 1)
    struct Command
    {
        uint8_t Type;        // CmdType
        uint8_t Slot;        // target slot
        uint8_t Pad[6];      // align SteamId to 8
        uint64_t SteamId;    // payload for kCmd_SetSteamId
        char Name[kNameLen]; // payload for kCmd_SetPersona
    };
#pragma pack(pop)

    static_assert(sizeof(Command) == 48, "Command must be 48 bytes");

// One signature/hook status entry. addr==0 means unresolved
#pragma pack(push, 1)
    struct SigEntry
    {
        char Name[kSigNameLen]; // signature name
        uint64_t Addr;          // resolved address, 0 if unresolved
    };
#pragma pack(pop)

    static_assert(sizeof(SigEntry) == kSigEntrySize, "SigEntry must be 40 bytes");

} // namespace cs2bh::shm
