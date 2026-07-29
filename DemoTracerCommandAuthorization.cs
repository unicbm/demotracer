using System.Net;
using System.Reflection;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;

namespace DemoTracer;

internal static class DemoTracerCommandCallerPolicy
{
    internal static bool CanExecute(
        bool serverConsole,
        bool isBot,
        string? remoteAddress,
        bool isDedicatedServer)
    {
        if (serverConsole)
            return true;

        if (isBot || isDedicatedServer)
            return false;

        return IsLoopbackAddress(remoteAddress);
    }

    internal static bool IsLoopbackAddress(string? remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress))
            return false;

        var value = remoteAddress.Trim();
        if (value.Equals("loopback", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(value, out var address))
            return IsLoopback(address);

        return IPEndPoint.TryParse(value, out var endpoint) &&
               IsLoopback(endpoint.Address);
    }

    internal static bool InferDedicatedServer(IEnumerable<string> commandLineArguments)
        => commandLineArguments.Any(argument =>
            argument.Equals("-dedicated", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("-dedicated=", StringComparison.OrdinalIgnoreCase));

    private static bool IsLoopback(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }
}

public sealed partial class DemoTracerPlugin
{
    private bool _isDedicatedServer =
        DemoTracerCommandCallerPolicy.InferDedicatedServer(Environment.GetCommandLineArgs());

    internal static string[] GetControlCommandNames()
        => typeof(DemoTracerPlugin)
            .GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetCustomAttributes<ConsoleCommandAttribute>())
            .Select(attribute => attribute.Command)
            .Where(command => command.StartsWith("dtr_", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(command => command, StringComparer.Ordinal)
            .ToArray();

    private void RegisterControlCommandAuthorization()
    {
        foreach (var commandName in GetControlCommandNames())
            AddCommandListener(commandName, AuthorizeControlCommand, HookMode.Pre);
    }

    private HookResult AuthorizeControlCommand(
        CCSPlayerController? player,
        CommandInfo command)
    {
        try
        {
            if (DemoTracerCommandCallerPolicy.CanExecute(
                    serverConsole: player == null,
                    isBot: player?.IsBot ?? false,
                    remoteAddress: player?.IpAddress,
                    isDedicatedServer: _isDedicatedServer))
            {
                return HookResult.Continue;
            }
        }
        catch
        {
            // Native player properties can disappear during disconnect. Fail closed.
        }

        command.ReplyToCommand(
            "dtr: command denied; use the server console or the local listen-server host");
        return HookResult.Handled;
    }

    [GameEventHandler]
    public HookResult OnServerSpawnForCommandAuthorization(
        EventServerSpawn @event,
        GameEventInfo info)
    {
        _isDedicatedServer = @event.Dedicated;
        return HookResult.Continue;
    }
}
