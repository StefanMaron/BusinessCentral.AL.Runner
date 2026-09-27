// Pins the runner's half of #4676: which app id the NavApplicationObjectBase ctor replacement
// stamps into AppId. The BC-behaviour half -- the Code Coverage Tests Run row names the app the
// test belongs to -- is corpus codeunit 60925, adjudicated on a real service tier.
using System;
using System.Reflection;
using System.Reflection.Emit;
using AlRunner;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// In <see cref="BcEngineCollection"/> for two reasons: it names an Ncl type, which must not be the
/// first thing to load Ncl in the test process; and it sets the process-wide current bundle info,
/// which in-process runner executions read, for the duration of one test.
/// </summary>
[Collection(BcEngineCollection.Name)]
public sealed class OwningAppIdStampTests
{
    private static readonly ApplicationObjectId Codeunit50000 = new(ObjectType.CodeUnit, 50000);

    private static Type DefineProbeType(string label)
    {
        var name = new AssemblyName($"OwningAppIdProbe_{label}_{Guid.NewGuid():N}");
        var module = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run)
            .DefineDynamicModule(name.Name!);
        return module.DefineType("Probe", TypeAttributes.Public).CreateType()!;
    }

    [Fact]
    public void AnObjectFromARegisteredAssembly_IsOwnedByThatAssemblysApp()
    {
        var appId = Guid.NewGuid();
        var probe = DefineProbeType("registered");
        BcRuntime.RegisterModuleInfoForAssembly(probe.Assembly, appId, "Probe App", "Probe", "1.0.0.0");

        Assert.Equal(appId, BcRuntime.OwningAppIdFor(probe, Codeunit50000));
    }

    [Fact]
    public void TwoAssembliesOfTwoApps_EachOwnTheirOwnObjects()
    {
        var first = DefineProbeType("first");
        var second = DefineProbeType("second");
        var firstApp = Guid.NewGuid();
        var secondApp = Guid.NewGuid();
        BcRuntime.RegisterModuleInfoForAssembly(first.Assembly, firstApp, "First", "Probe", "1.0.0.0");
        BcRuntime.RegisterModuleInfoForAssembly(second.Assembly, secondApp, "Second", "Probe", "1.0.0.0");

        // Same object id in both apps: the owner comes from the declaring assembly, not the id.
        Assert.Equal(firstApp, BcRuntime.OwningAppIdFor(first, Codeunit50000));
        Assert.Equal(secondApp, BcRuntime.OwningAppIdFor(second, Codeunit50000));
    }

    [Fact]
    public void AnUnregisteredAssembly_OwnsNothing_EvenWhileABundleIsCurrent()
    {
        var saved = BcRuntime.GetCurrentModuleAppInfo();
        try
        {
            BcRuntime.SetCurrentBundleInfo(Guid.NewGuid(), "Bundle", "Probe", "1.0.0.0");

            Assert.Null(BcRuntime.OwningAppIdFor(DefineProbeType("unregistered"), Codeunit50000));
            // Ncl declares NavRecord; a record built on it is owned per its table metadata, which
            // GetOwningAppId reads only when AppId is null.
            Assert.Null(BcRuntime.OwningAppIdFor(typeof(Microsoft.Dynamics.Nav.Runtime.NavRecord), Codeunit50000));
        }
        finally
        {
            BcRuntime.SetCurrentBundleInfo(saved.AppId, saved.Name, saved.Publisher, saved.Version);
        }
    }

    [Fact]
    public void ADynamicObject_OwnsNothing_EvenFromARegisteredAssembly()
    {
        var probe = DefineProbeType("dynamic");
        BcRuntime.RegisterModuleInfoForAssembly(probe.Assembly, Guid.NewGuid(), "Probe App", "Probe", "1.0.0.0");

        Assert.Null(BcRuntime.OwningAppIdFor(probe, new ApplicationObjectId(ObjectType.DynamicQuery, 1)));
    }
}
