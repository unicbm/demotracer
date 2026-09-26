/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Reflection;
using System.Runtime.CompilerServices;
using DemoTracerBotHiderApi;

namespace DemoTracer.Tests;

public sealed class BotHiderTakeoverPresentationTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ManagedBotKeepsDemoIdentityWithoutAReplayWritablePawn()
    {
        var (plugin, provider) = CreatePlugin();

        // Run the real request builder without a CS2 pawn or controller API.
        // The original bot controller is still managed during death/takeover;
        // looking up its pawn here used to discard its presentation override.
        var first = BuildOverride(plugin, 7);
        var duringTakeover = BuildOverride(plugin, 7);
        Assert.Equal("demo player", first.PlayerName);
        Assert.Equal(76561198000000001UL, first.SteamId);
        Assert.Equal(first.PlayerName, duringTakeover.PlayerName);
        Assert.Equal(first.SteamId, duringTakeover.SteamId);
        Assert.Equal(new BotHiderClan("team_B1ad3", 38084528), duringTakeover.Clan);
        Assert.Equal(42UL, duringTakeover.Incarnation);
        Assert.Equal(0, provider.ProviderInfoCalls);

        // Native management remains the authority: a real disconnect removes
        // eligibility, and a human slot is never assigned this bot's identity.
        Assert.Empty(BuildRequests(plugin, 3));
        provider.Managed = false;
        Assert.Empty(BuildRequests(plugin, 7));
    }

    private static (DemoTracerPlugin, ManagedBotProvider) CreatePlugin()
    {
        // Avoid BasePlugin's live server services while exercising the actual
        // private request path, without adding a production-only testing seam.
        var plugin = (DemoTracerPlugin)RuntimeHelpers.GetUninitializedObject(typeof(DemoTracerPlugin));
        var bridgeField = typeof(DemoTracerPlugin).GetField("_botHiderBridge", PrivateInstance)!;
        var bridge = Activator.CreateInstance(bridgeField.FieldType, nonPublic: true)!;
        var api = DispatchProxy.Create<IBotHiderApi, ManagedBotProvider>();
        bridgeField.FieldType.GetField("_api", PrivateInstance)!.SetValue(bridge, api);
        bridgeField.FieldType.GetField("_resolved", PrivateInstance)!.SetValue(bridge, true);
        bridgeField.SetValue(plugin, bridge);
        var mode = typeof(DemoTracerPlugin).GetField("_replayIdentityMode", PrivateInstance)!;
        mode.SetValue(plugin, Enum.Parse(mode.FieldType, "Steam"));
        return (plugin, (ManagedBotProvider)api);
    }

    [Theory]
    [InlineData("Name", true)]
    [InlineData("Steam", true)]
    [InlineData("Avatar", true)]
    [InlineData("Off", false)]
    public void ClanFollowsIdentityMode(string value, bool expected)
    {
        var (plugin, _) = CreatePlugin();
        var mode = typeof(DemoTracerPlugin).GetField("_replayIdentityMode", PrivateInstance)!;
        mode.SetValue(plugin, Enum.Parse(mode.FieldType, value));
        var requests = BuildRequests(plugin, 7);
        if (expected)
            Assert.NotNull(Assert.Single(requests).Value.Clan);
        else
            Assert.Empty(requests);
    }

    private static BotHiderPresentationOverride BuildOverride(DemoTracerPlugin plugin, int slot)
        => Assert.Single(BuildRequests(plugin, slot)).Value;

    private static Dictionary<int, BotHiderPresentationOverride> BuildRequests(DemoTracerPlugin plugin, int slot)
    {
        var evidenceType = typeof(DemoTracerPlugin).GetNestedType("BotHiderPresentationEvidence", BindingFlags.NonPublic)!;
        var evidence = Activator.CreateInstance(evidenceType,
            [slot, "demo player", 76561198000000001UL, 0, null, null, new BotHiderClan("team_B1ad3", 38084528)]);
        var requests = new Dictionary<int, BotHiderPresentationOverride>();
        typeof(DemoTracerPlugin).GetMethod("AddBotHiderPresentationOverride", PrivateInstance)!
            .Invoke(plugin, [requests, evidence]);
        return requests;
    }

    public class ManagedBotProvider : DispatchProxy
    {
        public bool Managed { get; set; } = true;
        public int ProviderInfoCalls { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_ApiVersion": return DemoTracerBotHiderContract.ApiVersion;
                case nameof(IBotHiderApi.GetProviderInfo):
                    ProviderInfoCalls++;
                    return new BotHiderProviderInfo { ApiVersion = DemoTracerBotHiderContract.ApiVersion, Connected = true };
                case nameof(IBotHiderApi.TryGetManagedSlot):
                    args![1] = new BotHiderManagedSlot { Slot = (int)args[0]!, Incarnation = 42 };
                    return Managed && (int)args[0]! == 7;
                default: throw new InvalidOperationException($"Unexpected provider call: {method.Name}");
            }
        }
    }
}
