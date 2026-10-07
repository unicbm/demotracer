/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Reflection;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Plugin;

namespace DemoTracer.Tests;

public sealed class BotBuySuspensionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrAlreadyDisabledPluginIsNeverLoaded(bool present)
    {
        var (context, fake) = CreateContext();
        fake.State = PluginState.Unloaded;
        var suspension = new BotBuySuspension();
        Assert.False(suspension.Suspend(present ? context : null));
        Assert.False(suspension.Restore(idle: true));
        Assert.Equal(0, fake.Loads);
    }

    [Fact]
    public void MultipleOwnersAndPendingRoundsKeepPluginUnloadedUntilIdle()
    {
        var (context, fake) = CreateContext();
        var suspension = new BotBuySuspension();
        var slots = new ReplaySlotRegistry();
        slots.LoadAndClaim(1);
        slots.LoadAndClaim(2);
        Assert.True(suspension.Suspend(context));
        Assert.False(suspension.Suspend(context));
        slots.Release(1);
        Assert.False(suspension.Restore(slots.OwnedCount == 0));
        slots.Release(2);
        Assert.False(suspension.Restore(idle: false)); // Next sequence round is pending.
        Assert.True(suspension.Restore(idle: true));
        Assert.False(suspension.Restore(idle: true));
        Assert.Equal(1, fake.Unloads);
        Assert.Equal(1, fake.Loads);
        Assert.Equal(1, fake.AllLoadedCalls);
        Assert.Equal(PluginState.Loaded, fake.State);
    }

    [Fact]
    public void ExternalReloadDoesNotCreateAnotherPluginInstance()
    {
        var (context, fake) = CreateContext();
        var suspension = new BotBuySuspension();
        suspension.Suspend(context);
        fake.State = PluginState.Loaded;
        Assert.True(suspension.Restore(idle: true));
        Assert.Equal(0, fake.Loads);
        Assert.Equal(0, fake.AllLoadedCalls);
    }

    [Fact]
    public void FailedUnloadRefusesControlAndKeepsOriginalForCleanup()
    {
        var (context, fake) = CreateContext();
        fake.FailUnload = true;
        var suspension = new BotBuySuspension();
        Assert.Throws<InvalidOperationException>(() => suspension.Suspend(context));
        Assert.True(suspension.Restore(idle: true));
        Assert.Equal(0, fake.Loads);
    }

    [Fact]
    public void FailedRestoreDoesNotRetryEveryTickButCanRetryAtLifecycleCleanup()
    {
        var (context, fake) = CreateContext();
        var suspension = new BotBuySuspension();
        suspension.Suspend(context);
        fake.FailLoad = true;
        Assert.Throws<InvalidOperationException>(() => suspension.Restore(idle: true));
        Assert.False(suspension.Restore(idle: true));
        Assert.Equal(1, fake.Loads);
        fake.FailLoad = false;
        Assert.True(suspension.Restore(idle: true, retry: true));
        Assert.Equal(2, fake.Loads);
        Assert.Equal(1, fake.AllLoadedCalls);
    }

    private static (IPluginContext Context, FakeContext Fake) CreateContext()
    {
        var context = DispatchProxy.Create<IPluginContext, FakeContext>();
        return (context, (FakeContext)(object)context);
    }

    public class FakeContext : DispatchProxy
    {
        public PluginState State = PluginState.Loaded;
        public int Loads, Unloads, AllLoadedCalls;
        public bool FailLoad, FailUnload;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_State": return State;
                case "get_Plugin":
                    var plugin = (FakePlugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(FakePlugin));
                    plugin.OnLoaded = () => AllLoadedCalls++;
                    return plugin;
                case "Unload":
                    Unloads++;
                    if (!FailUnload) State = PluginState.Unloaded;
                    return null;
                case "Load":
                    Loads++;
                    if (!FailLoad) State = PluginState.Loaded;
                    return null;
                default: throw new InvalidOperationException(method.Name);
            }
        }
    }

    public class FakePlugin : BasePlugin
    {
        public override string ModuleName => "BotBuyPatch";
        public override string ModuleVersion => "test";
        public Action OnLoaded = null!;
        public override void OnAllPluginsLoaded(bool hotReload) => OnLoaded();
    }
}
