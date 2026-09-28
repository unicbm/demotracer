// The handle for the BotController API.

using CounterStrikeSharp.API.Core.Capabilities;

namespace DtrControllerApi
{
    public static class BotControllerCapability
    {
        // Capability name is the cross-plugin contract key. Keep it stable.
        public static readonly PluginCapability<IDtrControllerApi> Cap =
            new("dtr-controller:api");
    }
}
