// The handle for the BotController API.

using CounterStrikeSharp.API.Core.Capabilities;

namespace DtrControllerApi
{
    public static class BotControllerCapability
    {
        public static readonly PluginCapability<IDtrControllerApi> Cap =
            new("dtr-controller:api");
    }
}
