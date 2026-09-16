using System.Runtime.InteropServices;
using System.Text;

namespace BotHiderImpl;

// Synchronous, main-thread-only C ABI. The native session expires at unload;
// no process-global mapping or queued writes can survive a slot replacement.
public sealed unsafe class NativePresentationClient : IDisposable
{
    public const int NativeAbi = 2;
    public const int SlotByteSize = 172;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct Slot
    {
        public ulong Session, Incarnation, BaseSteamId, SteamId;
        public int Managed, Ping;
        public uint ScoreboardFlair;
        public fixed byte BaseName[32];
        public fixed byte Name[32];
        public fixed byte Crosshair[64];
        public string ReadBaseName() { fixed (byte* p = BaseName) return Decode(p, 32); }
        public string ReadName() { fixed (byte* p = Name) return Decode(p, 32); }
        public string ReadCrosshair() { fixed (byte* p = Crosshair) return Decode(p, 64); }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Signature { public fixed byte Name[32]; public ulong Address; }

    [DllImport("BotHider", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotHider_GetNativeAbi();
    [DllImport("BotHider", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong BotHider_GetSession();
    [DllImport("BotHider", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotHider_ReadSlot(int slot, out Slot state, int size);
    [DllImport("BotHider", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotHider_ReadSignature(int index, out Signature signature, int size);
    [DllImport("BotHider", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotHider_PublishIdentity(int slot, ulong session, ulong incarnation, ulong sid, byte[] name);
    [DllImport("BotHider", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotHider_SetOption(ulong session, int option, int value);
    [DllImport("BotHider", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BotHider_PublishCrosshair(int slot, ulong session, ulong incarnation, uint controllerHandle);

    public ulong Session
    {
        get
        {
            if (_disposed) return 0;
            try { return BotHider_GetNativeAbi() == NativeAbi ? BotHider_GetSession() : 0; }
            catch (DllNotFoundException) { return 0; }
            catch (EntryPointNotFoundException) { return 0; }
            catch (BadImageFormatException) { return 0; }
        }
    }
    public bool TryConnect() => Session != 0;
    public bool IsConnected() => Session != 0;
    internal bool TryGetSlot(int slot, out Slot state)
    {
        state = default;
        return slot is >= 0 and < 64 && Session != 0 && BotHider_ReadSlot(slot, out state, sizeof(Slot)) == 0;
    }
    public bool IsManagedBot(int slot) => TryGetSlot(slot, out var s) && s.Managed != 0;
    public ulong GetBaseSteamId(int slot) => TryGetSlot(slot, out var s) ? s.BaseSteamId : 0;
    public ulong GetPublishedSteamId(int slot) => TryGetSlot(slot, out var s) ? s.SteamId : 0;
    public string GetBasePersonaName(int slot) => TryGetSlot(slot, out var s) ? s.ReadBaseName() : "";
    public string GetPublishedPersonaName(int slot) => TryGetSlot(slot, out var s) ? s.ReadName() : "";
    public int[] GetManagedSlots()
    {
        List<int> slots = [];
        for (int slot = 0; slot < 64; slot++) if (IsManagedBot(slot)) slots.Add(slot);
        return slots.ToArray();
    }
    internal bool PublishIdentity(int slot, ulong session, ulong incarnation, ulong sid, string name)
        => session != 0 && Session == session && TryEncodeFixedUtf8(name, 32, out var bytes) &&
           BotHider_PublishIdentity(slot, session, incarnation, sid, bytes) == 0;

    internal bool PublishCrosshair(int slot, ulong session, ulong incarnation, uint controllerHandle)
        => session != 0 && Session == session &&
           BotHider_PublishCrosshair(slot, session, incarnation, controllerHandle) == 0;

    public (string Name, ulong Addr)[] GetSignatures()
    {
        if (Session == 0) return [];
        List<(string, ulong)> entries = [];
        for (int i = 0; i < 8 && BotHider_ReadSignature(i, out var s, sizeof(Signature)) == 0; i++)
            entries.Add((Decode(s.Name, 32), s.Address));
        return entries.ToArray();
    }
    private bool SetOption(int option, int value)
    {
        var session = Session;
        return session != 0 && BotHider_SetOption(session, option, value) == 0;
    }
    public bool SetDisguise(bool enabled) => SetOption(1, enabled ? 1 : 0);
    public bool SetNameSource(bool useBotInfo) => SetOption(2, useBotInfo ? 1 : 0);
    public bool RequestRebuild() => SetOption(3, 0);

    private static string Decode(byte* pointer, int length)
    {
        var bytes = new ReadOnlySpan<byte>(pointer, length);
        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }
    internal static bool TryEncodeFixedUtf8(string value, int length, out byte[] buffer)
    {
        buffer = length > 0 ? new byte[length] : [];
        if (length <= 0 || value.Contains('\0') || Encoding.UTF8.GetByteCount(value) >= length) return false;
        Encoding.UTF8.GetBytes(value, buffer);
        return true;
    }
    public void Dispose() => _disposed = true;
}
