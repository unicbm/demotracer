/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Text.Json;
using DtrHiderApi;

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
        var api = DispatchProxy.Create<IDtrHiderApi, ManagedBotProvider>();
        bridgeField.FieldType.GetField("_api", PrivateInstance)!.SetValue(bridge, api);
        bridgeField.FieldType.GetField("_resolved", PrivateInstance)!.SetValue(bridge, true);
        bridgeField.SetValue(plugin, bridge);
        var mode = typeof(DemoTracerPlugin).GetField("_replayIdentityMode", PrivateInstance)!;
        mode.SetValue(plugin, Enum.Parse(mode.FieldType, "Steam"));
        typeof(DemoTracerPlugin).GetField("_replayClanTagEnabled", PrivateInstance)!.SetValue(plugin, true);
        return (plugin, (ManagedBotProvider)api);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClanTagToggleUpdatesLoadedAndRetainedIdentityWithoutChangingNameOrSteamId(bool retained)
    {
        var (plugin, _) = CreatePlugin();
        foreach (var name in new[] { "_session", "_retainedBotHiderPresentation" })
        {
            var field = typeof(DemoTracerPlugin).GetField(name, PrivateInstance)!;
            field.SetValue(plugin, Activator.CreateInstance(field.FieldType, nonPublic: true));
        }
        var clan = new BotHiderClan("team_B1ad3", 38084528);
        if (retained)
        {
            var evidenceBySlot = (IDictionary)typeof(DemoTracerPlugin).GetField("_retainedBotHiderPresentation", PrivateInstance)!.GetValue(plugin)!;
            var evidenceType = evidenceBySlot.GetType().GetGenericArguments()[1];
            evidenceBySlot.Add(7, Activator.CreateInstance(evidenceType,
                [7, "demo player", 76561198000000001UL, 0, null, null, clan]));
        }
        else
        {
            var session = typeof(DemoTracerPlugin).GetField("_session", PrivateInstance)!.GetValue(plugin)!;
            var slots = (ReplaySlotRegistry)session.GetType().GetProperty("ReplaySlots")!.GetValue(session)!;
            slots.LoadAndClaim(7);
            var replays = (IDictionary)session.GetType().GetProperty("LoadedReplays")!.GetValue(session)!;
            var replayType = replays.GetType().GetGenericArguments()[1];
            var replay = Activator.CreateInstance(replayType)!;
            replayType.GetProperty("SteamId")!.SetValue(replay, 76561198000000001UL);
            replayType.GetProperty("PlayerName")!.SetValue(replay, "demo player");
            replayType.GetProperty("Clan")!.SetValue(replay, clan);
            replays.Add(7, replay);
        }
        var builder = typeof(DemoTracerPlugin).GetMethod("BuildBotHiderPresentationOverrides", PrivateInstance)!;
        object?[] args = [null];
        var before = Assert.Single((BotHiderPresentationOverride[])builder.Invoke(plugin, args)!);
        var beforeSignature = Assert.IsType<string>(args[0]);
        var flag = typeof(DemoTracerPlugin).GetField("_replayClanTagEnabled", PrivateInstance)!;
        flag.SetValue(plugin, false);
        var after = Assert.Single((BotHiderPresentationOverride[])builder.Invoke(plugin, args)!);
        Assert.Equal(new BotHiderClan(string.Empty, 0), after.Clan);
        Assert.Equal(before.PlayerName, after.PlayerName);
        Assert.Equal(before.SteamId, after.SteamId);
        Assert.NotEqual(beforeSignature, args[0]);
        flag.SetValue(plugin, true);
        Assert.Equal(clan, Assert.Single((BotHiderPresentationOverride[])builder.Invoke(plugin, args)!).Clan);
        Assert.Equal(beforeSignature, args[0]);
    }

    [Fact]
    public void ClanTagOffExplicitlyClearsAnAppliedClanEvenWithIdentityOff()
    {
        var (plugin, _) = CreatePlugin();
        var mode = typeof(DemoTracerPlugin).GetField("_replayIdentityMode", PrivateInstance)!;
        mode.SetValue(plugin, Enum.Parse(mode.FieldType, "Off"));
        typeof(DemoTracerPlugin).GetField("_replayClanTagEnabled", PrivateInstance)!.SetValue(plugin, false);
        var request = BuildOverride(plugin, 7);
        Assert.Null(request.PlayerName);
        Assert.Null(request.SteamId);
        Assert.Equal(new BotHiderClan(string.Empty, 0), request.Clan);
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("{\"clan_tag\":false}", false)]
    [InlineData("{\"clan_tag\":true}", true)]
    public void RuntimeConfigPreservesClanTagSettingAcrossSerialization(string json, bool? expected)
    {
        var options = (JsonSerializerOptions)typeof(DemoTracerPlugin)
            .GetField("RuntimeConfigJsonOptions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var config = JsonSerializer.Deserialize<DemoTracerPlugin.DemoTracerRuntimeConfig>(json, options)!;
        var reloaded = JsonSerializer.Deserialize<DemoTracerPlugin.DemoTracerRuntimeConfig>(JsonSerializer.Serialize(config, options), options)!;
        Assert.Equal(expected, reloaded.ClanTag);
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
                case "get_ApiVersion": return DtrHiderContract.ApiVersion;
                case nameof(IDtrHiderApi.GetProviderInfo):
                    ProviderInfoCalls++;
                    return new BotHiderProviderInfo { ApiVersion = DtrHiderContract.ApiVersion, Connected = true };
                case nameof(IDtrHiderApi.TryGetManagedSlot):
                    args![1] = new BotHiderManagedSlot { Slot = (int)args[0]!, Incarnation = 42 };
                    return Managed && (int)args[0]! == 7;
                default: throw new InvalidOperationException($"Unexpected provider call: {method.Name}");
            }
        }
    }
}
