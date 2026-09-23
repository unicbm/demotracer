/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace DemoTracer;

internal sealed class ProjectilePhysicsHook(string serverPath, Action<nint> beforePhysics) : IDisposable
{
    // This is the shared projectile slot 228
    // implementation, not the player/controller PhysicsSimulate hook.
    internal const string ServerSha256 = "4f5c59c1153eb5f455f9131f80458bc2b9a6d1d7f30d170a2030d800685c00e8";
    internal const string BodySha256 = "e98fbf6bf05707520d8f3874b07e1ee15644bb19641794b98d0e26f8a640de81";
    internal const int EntryRva = 0x9c6ce0;
    internal const int BodyLength = 0x9c77ad - EntryRva;
    internal static ReadOnlySpan<int> VtableSlotRvas =>
        [0x17c4ae0, 0x17c60e8, 0x17c5528, 0x18e7188, 0x18e5700, 0x18e4030];

    private const string Signature =
        "48 89 5C 24 18 48 89 74 24 20 55 41 56 41 57 48 8D AC 24 30 FF FF FF 48 81 EC D0 01 00 00";

    // Win64: this, position*, velocity*, angles*, angularVelocity*. The fifth
    // argument is read at entry RSP+0x28 (function 0x9c760d). All four output
    // vectors and every original argument remain owned by the engine.
    private MemoryFunctionVoid<nint, nint, nint, nint, nint>? _function;
    private bool _attached;
    public bool Ready { get; private set; }
    public string Status { get; private set; } = "not_initialized";

    public void Install()
    {
        if (Ready)
            return;
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new InvalidOperationException("requires_windows_x64");
            using var process = Process.GetCurrentProcess();
            // Metamod also loads a server.dll. Select the game's module by its
            // complete path before validating its binary and loaded code.
            var modules = process.Modules.Cast<ProcessModule>().ToArray();
            var module = modules[FindGameServerModuleIndex(
                modules.Select(m => m.FileName).ToArray(), serverPath)];
            using var file = File.OpenRead(module.FileName);
            if (!MatchesHash(SHA256.HashData(file), ServerSha256))
                throw new InvalidOperationException("unsupported_server_binary");
            if (!OffsetsFitImage(module.ModuleMemorySize))
                throw new InvalidOperationException("invalid_server_image_size");

            var body = new byte[BodyLength];
            Marshal.Copy(module.BaseAddress + EntryRva, body, 0, body.Length);
            if (!MatchesHash(SHA256.HashData(body), BodySha256))
                throw new InvalidOperationException("physics_body_changed_or_already_detoured");
            foreach (var rva in VtableSlotRvas)
            {
                if (Marshal.ReadIntPtr(module.BaseAddress + rva) != module.BaseAddress + EntryRva)
                    throw new InvalidOperationException("projectile_vtable_mismatch");
            }

            _function = new(Signature, module.FileName);
            _function.Hook(OnPre, HookMode.Pre);
            _attached = true;
            Ready = true;
            Status = "ready:first_physics_pre";
        }
        catch (Exception ex)
        {
            Ready = false;
            Status = $"unavailable:{ex.Message}";
        }
    }

    internal static bool MatchesHash(ReadOnlySpan<byte> hash, string expected)
        => Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);

    internal static int FindGameServerModuleIndex(IReadOnlyList<string> modulePaths, string serverPath)
    {
        var expected = Path.GetFullPath(serverPath);
        var matches = Enumerable.Range(0, modulePaths.Count).Where(index =>
            Path.GetFullPath(modulePaths[index]).Equals(expected, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"game_server_module_matches={matches.Length}");
        return matches[0];
    }

    internal static bool OffsetsFitImage(int size)
        => size >= EntryRva + BodyLength && VtableSlotRvas.ToArray().All(rva => rva <= size - sizeof(long));

    private HookResult OnPre(DynamicHook hook)
    {
        try
        {
            if (Ready)
                beforePhysics(hook.GetParam<nint>(0));
        }
        catch (Exception ex)
        {
            // Never unwind a managed exception across the native physics call.
            Ready = false;
            Status = $"unavailable:callback_failed:{ex.Message}";
        }
        return HookResult.Continue;
    }

    public void Dispose()
    {
        Ready = false;
        if (_attached)
            _function?.Unhook(OnPre, HookMode.Pre);
        _attached = false;
        _function = null;
        Status = "unloaded";
    }
}
