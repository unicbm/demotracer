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

internal sealed class ProjectilePhysicsHook(string serverPath, string profilePath, Action<nint> beforePhysics) : IDisposable
{
    // Win64: this, position*, velocity*, angles*, angularVelocity*. The fifth
    // argument is read at entry RSP+0x28. All four output
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
            var profile = ProjectilePhysicsProfile.Load(profilePath);
            byte[] file = File.ReadAllBytes(module.FileName);
            if (!NativeCompatibility.PeImageFingerprint.Compute(file).Equals(profile.ImageSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("unsupported_server_image:audit_native_profile");
            int entryRva = profile.ResolveEntry(file);
            if (!profile.OffsetsFitImage(module.ModuleMemorySize))
                throw new InvalidOperationException("invalid_server_image_size");

            var body = new byte[profile.BodyLength];
            Marshal.Copy(module.BaseAddress + entryRva, body, 0, body.Length);
            if (!MatchesHash(SHA256.HashData(body), profile.BodySha256))
                throw new InvalidOperationException("physics_body_changed_or_already_detoured");
            foreach (var rva in profile.VtableSlotRvas)
            {
                if (Marshal.ReadIntPtr(module.BaseAddress + rva) != module.BaseAddress + entryRva)
                    throw new InvalidOperationException("projectile_vtable_mismatch");
            }

            _function = new(profile.Signature, module.FileName);
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
