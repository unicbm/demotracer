/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using DemoTracerApi;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private sealed class DemoTracerApiFacade : IDemoTracerApi
    {
        private readonly DemoTracerPlugin _plugin;

        public DemoTracerApiFacade(DemoTracerPlugin plugin)
        {
            _plugin = plugin;
        }

        public int ApiVersion => BotControllerNative.DemoTracerApiVersion;

        public bool IsSlotBusy(int slot)
            => _plugin.IsReplaySlotBusy(slot);

        public bool IsDemoTracerBot(int slot)
            => _plugin.IsDemoTracerBot(slot);

        public bool TryGetBotCosmeticState(int slot, out DemoTracerBotCosmeticState state)
            => _plugin.TryGetBotCosmeticState(slot, out state);
    }
}
