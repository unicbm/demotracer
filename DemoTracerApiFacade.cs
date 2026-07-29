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

        public bool TrySetBotHudCrosshairOverride(
            int slot,
            string crosshairCode,
            out DemoTracerCrosshairOverrideResult result)
            => _plugin.TrySetBotHudCrosshairOverride(slot, crosshairCode, out result);

        public bool ClearBotHudCrosshairOverride(
            int slot,
            out DemoTracerCrosshairOverrideResult result)
            => _plugin.ClearBotHudCrosshairOverride(slot, out result);

        public void ClearBotHudCrosshairOverrides()
            => _plugin.ClearBotHudCrosshairOverrides();

        public DemoTracerCrosshairOverrideStatus GetCrosshairOverrideStatus()
            => _plugin.GetCrosshairOverrideStatus();
    }
}
