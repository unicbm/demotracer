// CS2 runtime PlayerCommand layout

#pragma once

#include <cstdint>
#include "cs_usercmd.pb.h"

// Engine button state block
class CInButtonState
{
    virtual void Schema_DynamicBinding_Unused() {} // keep vtable slot, do not call

public:
    uint64_t m_pButtonStates[3];
};

// Leading host block: vptr + cmdNum + pad
class CUserCmdBase
{
public:
    int cmdNum;
    uint8_t unk[4];

    virtual ~CUserCmdBase();

private:
    virtual void unk0();
    virtual void unk1();
    virtual void unk2();
    virtual void unk3();
    virtual void unk4();
    virtual void unk5();
    virtual void unk6();
};

// Brings the protobuf message in as a base, not a member.
template <typename T>
class CUserCmdBaseHost : public CUserCmdBase, public T
{
};

class CUserCmd : public CUserCmdBaseHost<CSGOUserCmdPB>
{
};

class CUserCmdExtended : public CUserCmd
{
public:
    CInButtonState buttonstates;
    uint32_t unknown; // not part of the player message
};

class PlayerCommand : public CUserCmdExtended
{
public:
    uint32_t flags;
    PlayerCommand *unknowncmd;
    PlayerCommand *parentcmd;
};

#ifndef _WIN32
static_assert(sizeof(PlayerCommand) == 144, "Size of PlayerCommand is incorrect");
#else
static_assert(sizeof(PlayerCommand) == 152, "Size of PlayerCommand is incorrect");
#endif
