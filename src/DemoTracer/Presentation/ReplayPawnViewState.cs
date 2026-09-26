/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    internal readonly record struct ReplayViewOwner(uint Controller, uint Pawn, nint Address);

    // Spawn clears the entry even if the engine reuses the same entity handle.
    // Original values belong only to the fields acquired on this incarnation.
    internal sealed class ReplayPawnViewState(ReplayViewOwner owner)
    {
        public ReplayViewOwner Owner { get; } = owner;
        public ReplayViewmodel? Applied { get; set; }
        public bool Failed { get; set; }
        private readonly ReplayViewmodel _original = new();
        private readonly ReplayViewmodel _written = new();

        public void Capture(ReplayViewmodel current, ReplayViewmodel requested)
        {
            if (requested.Fov.HasValue) { _original.Fov ??= current.Fov; _written.Fov = requested.Fov; }
            if (requested.OffsetX.HasValue) { _original.OffsetX ??= current.OffsetX; _written.OffsetX = requested.OffsetX; }
            if (requested.OffsetY.HasValue) { _original.OffsetY ??= current.OffsetY; _written.OffsetY = requested.OffsetY; }
            if (requested.OffsetZ.HasValue) { _original.OffsetZ ??= current.OffsetZ; _written.OffsetZ = requested.OffsetZ; }
        }

        public ReplayViewmodel? GetRestore(ReplayViewOwner currentOwner, ReplayViewmodel current)
        {
            if (Owner != currentOwner) return null;
            // Do not undo another owner's later write, or restore hand output.
            return new ReplayViewmodel
            {
                Fov = NullableFloatBitsEqual(current.Fov, _written.Fov) ? _original.Fov : null,
                OffsetX = NullableFloatBitsEqual(current.OffsetX, _written.OffsetX) ? _original.OffsetX : null,
                OffsetY = NullableFloatBitsEqual(current.OffsetY, _written.OffsetY) ? _original.OffsetY : null,
                OffsetZ = NullableFloatBitsEqual(current.OffsetZ, _written.OffsetZ) ? _original.OffsetZ : null
            };
        }
    }
}
