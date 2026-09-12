/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracerApi;

public interface IDemoTracerApi
{
    int ApiVersion { get; }

    // True for any loaded native replay, including other providers.
    bool IsSlotBusy(int slot);

    // DemoTracer session ownership; native replay activity alone is insufficient.
    bool IsDemoTracerBot(int slot);

    bool TryGetBotCosmeticState(int slot, out DemoTracerBotCosmeticState state);
}

public sealed class DemoTracerBotCosmeticState
{
    public bool IsDemoTracerBot { get; set; }

    public bool IsSlotBusy { get; set; }

    public bool HasCosmeticEvidence { get; set; }

    public bool CosmeticWriterEnabled { get; set; }

    public bool ShouldDeferInventoryWrites { get; set; }
}
