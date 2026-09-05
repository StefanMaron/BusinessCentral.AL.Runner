using System;
using System.Reflection;
using AlRunner;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2961 — <c>ALNavApp.ALGetModuleInfo(DataError, Guid, ByRef&lt;NavModuleInfo&gt;)</c>.
///
/// BC's own body resolves the id against
/// <c>NavCurrentThread.TryResolveAppGroup().OrderedAppMetadata</c>, empty on the skeleton, so
/// every id missed. A precompiled app asking about itself therefore got BC's raise — measured on
/// BC 28.1, Codeunit1809 "Assisted Setup Installation".OnInstallAppPerCompany asking
/// <c>VersionInstalled</c> about the System Application's own id.
///
/// The AL-visible half is pinned by <c>tests/runner-extras/navapp-getmoduleinfo-byid</c>, but that
/// suite can only reach the helper through BcAssembler's polyfill redirect, because
/// source-compiled AL never calls the Ncl method. Precompiled AL does, and the only route to it
/// today is an install trigger (#2960, not landed). So the Cecil rewrite itself is pinned here,
/// by invoking the rewritten method in the loaded Ncl and asserting it answers from the runner's
/// module registry — something the unpatched body cannot do, since its metadata list is empty.
/// </summary>
[Collection(BcEngineCollection.Name)]
public sealed class NavAppGetModuleInfoByIdTests
{
    private readonly BcEngineFixture _engine;

    public NavAppGetModuleInfoByIdTests(BcEngineFixture engine) => _engine = engine;

    private static MethodInfo RewrittenAlGetModuleInfo()
    {
        var alNavApp = typeof(ITreeObject).Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.ALNavApp")
            ?? throw new InvalidOperationException("ALNavApp not found in the loaded Ncl.");
        foreach (var m in alNavApp.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            if (m.Name == "ALGetModuleInfo" && m.GetParameters().Length == 3)
                return m;
        throw new InvalidOperationException(
            "ALNavApp.ALGetModuleInfo(DataError, Guid, ByRef<NavModuleInfo>) not found — "
            + "BC's shape changed and the Cecil rewrite in NclCecilRewrite.Dispatch.cs is now bound to nothing.");
    }

    private static (bool Ok, NavModuleInfo? Info) CallRewritten(DataError errorLevel, Guid moduleId)
    {
        NavModuleInfo? slot = null;
        var byRef = new ByRef<NavModuleInfo>(() => slot!, v => slot = v);
        var ok = (bool)RewrittenAlGetModuleInfo().Invoke(null, new object?[] { errorLevel, moduleId, byRef })!;
        return (ok, slot);
    }

    /// <summary>A module id registered only for this test, so a pass cannot come from the closure.</summary>
    private static Guid RegisterSyntheticModule(string name)
    {
        var appId = Guid.NewGuid();
        BcRuntime.RegisterModuleInfoForAssembly(
            typeof(NavAppGetModuleInfoByIdTests).Assembly, appId, name, "AL Runner Tests", "3.4.5.6");
        return appId;
    }

    [SkippableFact]
    public void RewrittenNclMethod_ResolvesAModuleTheRunnerRegistered()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var appId = RegisterSyntheticModule("NGM Synthetic App");

        var (ok, info) = CallRewritten(DataError.TrapError, appId);

        // BC's unpatched body reads an app-group metadata list the runner never fills, so it
        // answers false for every id. True here means the rewrite is live AND reading the
        // runner's registry.
        Assert.True(ok, "the rewritten ALGetModuleInfo must resolve a registered module id");
        Assert.NotNull(info);
        Assert.Equal(appId, info!.ALId);
        Assert.Equal("NGM Synthetic App", info.ALName);
        Assert.Equal("AL Runner Tests", info.ALPublisher);
    }

    [SkippableFact]
    public void RewrittenNclMethod_UnknownId_TrappingFormAnswersFalseAndNullsTheSlot()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (ok, info) = CallRewritten(DataError.TrapError, Guid.NewGuid());

        Assert.False(ok);
        Assert.Null(info);
    }

    [SkippableFact]
    public void RewrittenNclMethod_UnknownId_NonTrappingFormRaisesBcsOwnError()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var missing = Guid.NewGuid();

        var ex = Assert.Throws<TargetInvocationException>(
            () => CallRewritten(DataError.ThrowError, missing));

        // The concrete message BC raises for this case, so AL asserterror / GetLastErrorText see
        // what a service tier shows. Returning false here instead would be the silent default
        // .claude/rules/loud-failures.md rules out: AL emits a bare
        // `NavApp.GetModuleInfo(id, info)` statement with ThrowError, so a quiet false lets the
        // caller carry on with an empty ModuleInfo.
        Assert.NotNull(ex.InnerException);
        Assert.Contains("No installed extension was found", ex.InnerException!.Message);
        Assert.Contains(missing.ToString(), ex.InnerException.Message);
    }

    [Fact]
    public void Helper_IsTheSingleImplementationBothCompilationRoutesUse()
    {
        // BcAssembler's source-side polyfill delegates to this exact method; the Cecil rewrite
        // binds it by name with Public|Static. Renaming or re-signing it silently unbinds one or
        // both routes, which is how the two copies drifted in the first place (#2961): the shim
        // returned false on a miss whatever the DataError said, while BC raised.
        var m = typeof(NavAppModuleInfoPatches).GetMethod(
            nameof(NavAppModuleInfoPatches.ALNavApp_GetModuleInfo),
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(m);
        Assert.Equal(typeof(bool), m!.ReturnType);
        var ps = m.GetParameters();
        Assert.Equal(3, ps.Length);
        Assert.Equal(typeof(DataError), ps[0].ParameterType);
        Assert.Equal(typeof(Guid), ps[1].ParameterType);
        Assert.Equal(typeof(ByRef<NavModuleInfo>), ps[2].ParameterType);
    }
}
