// The handle for the BotController API.

using CounterStrikeSharp.API.Core.Capabilities;

namespace BotControllerApi
{
    public static class BotControllerCapability
    {
        // Capability name is the cross-plugin contract key. Keep it stable.
        public static readonly PluginCapability<IBotControllerApi> Cap =
            new("botcontroller:api");
    }
}
