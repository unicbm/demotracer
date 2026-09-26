/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Reflection;
using BotHiderImpl;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using DemoTracer;

namespace DemoTracer.Tests;

public sealed class CommandPolicyTests
{
    [Fact]
    public void DemoTracerCommandsUseListenHostPolicy()
    {
        var commands = DeclaredMethods(typeof(DemoTracerPlugin))
            .SelectMany(method => method.GetCustomAttributes<ConsoleCommandAttribute>()
                .Select(command => (method, command.Command)))
            .Where(item => item.Command.StartsWith("dtr_", StringComparison.Ordinal))
            .ToArray();
        var commandsWithoutClientDispatch = new List<string>();

        foreach (var (method, command) in commands)
        {
            var helper = method.CustomAttributes.SingleOrDefault(attribute =>
                attribute.AttributeType == typeof(CommandHelperAttribute));
            var clientAndServer = helper is { ConstructorArguments.Count: >= 3 } &&
                helper.ConstructorArguments[2].Value is int value &&
                (CommandUsage)value == CommandUsage.CLIENT_AND_SERVER;
            if (!clientAndServer)
                commandsWithoutClientDispatch.Add(command);
        }

        Assert.NotEmpty(commands);
        Assert.True(
            commandsWithoutClientDispatch.Count == 0,
            "Commands without explicit listen-host dispatch: " +
            string.Join(", ", commandsWithoutClientDispatch));
        Assert.Equal(
            commands.Select(item => item.Command).OrderBy(command => command, StringComparer.Ordinal),
            DemoTracerPlugin.GetControlCommandNames());
    }

    [Fact]
    public void BotHiderCommandsRemainServerOnly()
    {
        var unsafeCommands = new List<string>();
        var commandCount = 0;

        foreach (var method in DeclaredMethods(typeof(BotHiderImplPlugin)))
        {
            var commands = method.GetCustomAttributes<ConsoleCommandAttribute>().ToArray();
            commandCount += commands.Length;
            if (commands.Length == 0)
                continue;

            var helper = method.CustomAttributes.SingleOrDefault(attribute =>
                attribute.AttributeType == typeof(CommandHelperAttribute));
            var serverOnly = helper is { ConstructorArguments.Count: >= 3 } &&
                helper.ConstructorArguments[2].Value is int value &&
                (CommandUsage)value == CommandUsage.SERVER_ONLY;
            if (!serverOnly)
                unsafeCommands.AddRange(commands.Select(command => command.Command));
        }

        Assert.True(commandCount > 0, "No BotHider commands were discovered.");
        Assert.True(
            unsafeCommands.Count == 0,
            $"BotHider commands without an explicit server-only policy: {string.Join(", ", unsafeCommands)}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("203.0.113.10:27005")]
    [InlineData("192.168.1.20")]
    public void RemotePlayersCannotExecuteDemoTracerCommands(string? remoteAddress)
    {
        Assert.False(DemoTracerCommandCallerPolicy.CanExecute(
            serverConsole: false,
            isBot: false,
            playerSlot: 1,
            remoteAddress,
            isDedicatedServer: false));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:27005")]
    [InlineData("[::1]:27005")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("loopback")]
    public void ListenServerHostCanExecuteDemoTracerCommands(string remoteAddress)
    {
        Assert.True(DemoTracerCommandCallerPolicy.CanExecute(
            serverConsole: false,
            isBot: false,
            playerSlot: 0,
            remoteAddress,
            isDedicatedServer: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("192.168.1.20:27005")]
    [InlineData("steam-networking")]
    public void ListenServerHostDoesNotDependOnReportedIpAddress(string? remoteAddress)
    {
        Assert.True(DemoTracerCommandCallerPolicy.CanExecute(
            serverConsole: false,
            isBot: false,
            playerSlot: 0,
            remoteAddress,
            isDedicatedServer: false));
    }

    [Fact]
    public void DedicatedServersAndBotsDoNotUseListenHostException()
    {
        Assert.False(DemoTracerCommandCallerPolicy.CanExecute(
            serverConsole: false,
            isBot: false,
            playerSlot: 0,
            remoteAddress: "127.0.0.1",
            isDedicatedServer: true));
        Assert.False(DemoTracerCommandCallerPolicy.CanExecute(
            serverConsole: false,
            isBot: true,
            playerSlot: 0,
            remoteAddress: "127.0.0.1",
            isDedicatedServer: false));
        Assert.True(DemoTracerCommandCallerPolicy.CanExecute(
            serverConsole: true,
            isBot: false,
            playerSlot: -1,
            remoteAddress: null,
            isDedicatedServer: true));
    }

    [Theory]
    [InlineData(new[] { "cs2.exe", "-dedicated", "+map", "de_mirage" }, true)]
    [InlineData(new[] { "cs2.exe", "-DEDICATED=true" }, true)]
    [InlineData(new[] { "cs2.exe", "-insecure", "+map", "de_mirage" }, false)]
    public void CommandLineProvidesHotReloadServerMode(string[] arguments, bool expectedDedicated)
    {
        Assert.Equal(
            expectedDedicated,
            DemoTracerCommandCallerPolicy.InferDedicatedServer(arguments));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RestartingPlanKeepsPresentationOwnershipUntilRoundStart(
        bool restartRequested,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReplayPlanOverridePolicy.DeferExistingReplayCleanupUntilRoundStart(
                restartRequested));
    }

    private static IEnumerable<MethodInfo> DeclaredMethods(Type type)
        => type.GetMethods(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);
}
