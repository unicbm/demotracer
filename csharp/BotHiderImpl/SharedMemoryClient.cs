using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BotHiderImpl;

// Reads BotHider's shared-memory data region and posts write commands
// src/slot_shm.h
public sealed class SharedMemoryClient : IDisposable
{
    private const string MappingName = "CS2BotHider_Slots";
    private const string PosixMappingPath = "/dev/shm/CS2BotHider_Slots";
    private const uint Magic = 0x44494842; // 'BHID'
    private const uint Version = 2;
    private const int MaxSlots = 64;
    private const int NameLen = 32;
    private const int CmdCount = 64;
    private const int TotalSize = 16384;

    // Data region offsets
    private const int OffMagic = 0;
    private const int OffVersion = 4;
    private const int OffMaxSlots = 8;
    private const int OffSlotState = 16;
    private const int OffSyntheticSid = 80;
    private const int OffPersonaName = 592;
    // Extra data region
    private const int OffCurrentPing = 5720;  // int32[64]
    private const int OffCrosshair = 5976;  // char[64][64]
    private const int CrosshairLen = 64;
    // Signature/hook status region
    private const int OffSigCount = 10072;  // uint32
    private const int OffSigEntries = 10080;  // SigEntry[8]
    private const int SigNameLen = 32;
    private const int SigEntrySize = 40;  // char[32] + uint64
    private const int MaxSigs = 8;
    // Scoreboard flair region
    private const int OffScoreboardFlair = 10400;  // uint32[64]
    private const int OffBaseSyntheticSid = 10656; // uint64[64]
    private const int OffBasePersonaName = 11168;  // char[64][32]

    // Command region offsets
    private const int OffWriteIdx = 2640;
    private const int OffReadIdx = 2644;
    private const int OffCmds = 2648;
    private const int CmdSize = 48;

    // Command opcodes
    private const byte CmdSetSteamId = 1;
    private const byte CmdSetPersona = 2;
    private const byte CmdSetDisguise = 3;
    private const byte CmdRebuild = 4;
    // 5 (KickAll) and 6 (Refill) retired — match-end clean-rebuild removed
    private const byte CmdSetNameSource = 7;

    // Sentinel slot for global commands
    private const byte SlotAll = 255;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private readonly object _writeLock = new();

    // Try to open the existing mapping. Returns false if BotHider isn't loaded yet
    public bool TryConnect()
    {
        if (_view != null) return true;
        try
        {
            _mmf = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.ReadWrite)
                : MemoryMappedFile.CreateFromFile(PosixMappingPath, FileMode.Open, null,
                    TotalSize, MemoryMappedFileAccess.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, TotalSize,
                MemoryMappedFileAccess.ReadWrite);
            if (_view.ReadUInt32(OffMagic) != Magic ||
                _view.ReadUInt32(OffVersion) != Version ||
                _view.ReadUInt32(OffMaxSlots) != MaxSlots)
            {
                Dispose();
                return false;
            }
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (Exception) { Dispose(); return false; }
    }

    private bool Valid(int slot)
    {
        if (_view == null) TryConnect();
        return _view != null && slot >= 0 && slot < MaxSlots;
    }

    // Returns whether the shared memory bridge is connected
    public bool IsConnected()
    {
        if (_view != null) return true;
        return TryConnect();
    }

    // Native persona base-state read side.

    public bool IsManagedBot(int slot) =>
        Valid(slot) && _view!.ReadByte(OffSlotState + slot) != 0;

    public ulong GetBaseSteamId(int slot) =>
        Valid(slot) ? _view!.ReadUInt64(OffBaseSyntheticSid + slot * 8) : 0UL;

    public ulong GetPublishedSteamId(int slot) =>
        Valid(slot) ? _view!.ReadUInt64(OffSyntheticSid + slot * 8) : 0UL;

    public int[] GetManagedSlots()
    {
        if (_view == null) TryConnect();
        if (_view == null) return Array.Empty<int>();
        var list = new List<int>();
        for (int s = 0; s < MaxSlots; s++)
        {
            if (_view.ReadByte(OffSlotState + s) == 0) continue;
            list.Add(s);
        }
        return list.ToArray();
    }

    public string GetBasePersonaName(int slot)
        => ReadFixedUtf8(slot, OffBasePersonaName, NameLen);

    public string GetPublishedPersonaName(int slot)
        => ReadFixedUtf8(slot, OffPersonaName, NameLen);

    private string ReadFixedUtf8(int slot, int baseOffset, int fieldLength)
    {
        if (!Valid(slot)) return string.Empty;
        var buf = new byte[fieldLength];
        _view!.ReadArray(baseOffset + slot * fieldLength, buf, 0, fieldLength);
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = fieldLength;
        return Encoding.UTF8.GetString(buf, 0, len);
    }

    public int GetPing(int slot) =>
        Valid(slot) ? _view!.ReadInt32(OffCurrentPing + slot * 4) : 0;

    public string GetCrosshairCode(int slot)
    {
        if (!Valid(slot)) return string.Empty;
        var buf = new byte[CrosshairLen];
        _view!.ReadArray(OffCrosshair + slot * CrosshairLen, buf, 0, CrosshairLen);
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = CrosshairLen;
        return Encoding.UTF8.GetString(buf, 0, len);
    }

    // Returns the persona's current scoreboard flair from the native truth source.
    public uint GetScoreboardFlair(int slot)
        => IsManagedBot(slot) ? _view!.ReadUInt32(OffScoreboardFlair + slot * 4) : 0U;

    // Read the resolved hook/signature status table
    public (string Name, ulong Addr)[] GetSignatures()
    {
        if (_view == null) TryConnect();
        if (_view == null) return Array.Empty<(string, ulong)>();
        uint count = _view.ReadUInt32(OffSigCount);
        if (count > MaxSigs) count = MaxSigs;
        var list = new List<(string, ulong)>((int)count);
        var buf = new byte[SigNameLen];
        for (int i = 0; i < count; i++)
        {
            int baseOff = OffSigEntries + i * SigEntrySize;
            _view.ReadArray(baseOff, buf, 0, SigNameLen);
            int len = Array.IndexOf(buf, (byte)0);
            if (len < 0) len = SigNameLen;
            string name = Encoding.UTF8.GetString(buf, 0, len);
            ulong addr = _view.ReadUInt64(baseOff + SigNameLen);
            list.Add((name, addr));
        }
        return list.ToArray();
    }

    // Global disguise toggle
    public bool SetDisguise(bool enabled)
    {
        if (_view == null) TryConnect();
        if (_view == null) return false;
        return PostCommand(CmdSetDisguise, SlotAll, enabled ? 1UL : 0UL, null);
    }

    // Global display-name source toggle (1=bot_info name, 0=botprofile name)
    public bool SetNameSource(bool useBotInfo)
    {
        if (_view == null) TryConnect();
        if (_view == null) return false;
        return PostCommand(CmdSetNameSource, SlotAll, useBotInfo ? 1UL : 0UL, null);
    }

    internal bool SetPublishedSteamId(int slot, ulong steamId)
        => Valid(slot) && PostCommand(CmdSetSteamId, slot, steamId, null);

    internal bool SetPublishedPersonaName(int slot, string playerName)
        => Valid(slot) && !string.IsNullOrWhiteSpace(playerName) &&
           PostCommand(CmdSetPersona, slot, 0UL, playerName);

    // Request a clean bot rebuild
    public bool RequestRebuild()
    {
        if (_view == null) TryConnect();
        if (_view == null) return false;
        return PostCommand(CmdRebuild, SlotAll, 0UL, null);
    }

    private bool PostCommand(byte type, int slot, ulong sid, string? name)
    {
        if (_view == null) return false;
        lock (_writeLock)
        {
            uint w = _view.ReadUInt32(OffWriteIdx);
            uint r = _view.ReadUInt32(OffReadIdx);
            if (unchecked(w - r) >= CmdCount)
                return false;
            int baseOff = OffCmds + (int)(w % CmdCount) * CmdSize;

            _view.Write(baseOff + 0, type);
            _view.Write(baseOff + 1, (byte)slot);
            _view.Write(baseOff + 8, sid);
            var nameBuf = new byte[NameLen];
            if (name != null)
            {
                var encoded = Encoding.UTF8.GetBytes(name);
                Array.Copy(encoded, nameBuf, Math.Min(encoded.Length, NameLen - 1));
            }
            _view.WriteArray(baseOff + 16, nameBuf, 0, NameLen);

            Interlocked.MemoryBarrier();
            _view.Write(OffWriteIdx, w + 1);
        }
        return true;
    }

    public void Dispose()
    {
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
    }
}
