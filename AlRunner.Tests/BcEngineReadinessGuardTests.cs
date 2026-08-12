// BcEngineReadinessGuardTests — makes "the in-process BC engine tests silently stopped
// executing" impossible to reach green on CI again (issue #1813).
//
// What actually happened
// -----------------------
// VSTest's own Xunit.Runner.VisualStudio.VisualStudioSourceInformationProvider builds a
// DiaSession for the test assembly (for stack-trace source-line mapping), which internally
// calls PortableSymbolReader.PopulateCacheForTypeAndMethodSymbols -> RuntimeModule.GetTypes(),
// forcing full type resolution of the ENTIRE AlRunner.Tests module — including field types
// declared by other test classes — as a side effect, BEFORE any test code (including our
// [ModuleInitializer]) gets a chance to run. That resolved and loaded
// Microsoft.Dynamics.Nav.Ncl straight out of its un-rewritten bin copy. By the time
// BcEngineBootstrap.Initialize() ran, NclCecilRewrite.RewriteInPlace() saw Ncl already loaded
// and no-opped, and BcRuntime.EnsureApplied() threw against the un-rewritten assembly — caught,
// turned into a SkipReason, never surfaced as a failure. Fifteen tests across
// BcCompilerEmitRetryTests / SkeletonSharedObjectContainerLeakTests /
// BcEngineReadinessGuardTests-adjacent classes reported Skipped, not Failed, on every CI run —
// a green leg that had quietly stopped covering the in-process BC engine at all.
//
// The fix: AlRunner.Tests/EngineStartupHook.cs + AlRunner/EngineTestBinResolverStartupHook.cs
// wire a DOTNET_STARTUP_HOOKS chain (the earliest hookable point in a .NET process — it runs
// strictly before the host's own Main()) that forces AlRunner.Tests.dll's module, and
// therefore its [ModuleInitializer], to run before VSTest's DiaSession gets the chance.
// .github/workflows/test-matrix.yml wires it via a generated .runsettings, scoped to the
// testhost child process.
//
// This file is the acceptance check the issue itself names: on a CI leg, artifacts are
// provisioned and the Cecil cache is warmed by construction, so BcEngineFixture.Ready being
// false there is never a legitimate skip — it means this whole mechanism regressed again (a
// workflow edit drops --settings, a hook assembly path goes stale, VSTest changes how/when it
// resolves types, …), and this test fails LOUD instead of joining the silent-skip pile it
// exists to prevent.
//
// Split in two, deliberately:
//   1. BcEngineReadinessGuard.AssertReadyOnCi (in BcEngineCollection.cs) is a PURE function of
//      (ready, skipReason, runningOnCi) — proven below with constructed booleans, no BC
//      artifacts and no CI environment required to run the proving test.
//   2. Ready_IsTrue_WhenArtifactsAreProvisioned wires that pure function to the REAL
//      BcEngineFixture and TestArtifacts.RunningOnCi — reusing TestArtifacts.RunningOnCi
//      rather than re-deriving CI detection, exactly as TestArtifacts.SkipIfMissingIn does for
//      the artifacts-presence gate this mirrors.

using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcEngineReadinessGuardTests
{
    private readonly BcEngineFixture _engine;

    public BcEngineReadinessGuardTests(BcEngineFixture engine) => _engine = engine;

    /// <summary>
    /// Off CI, artifacts genuinely may not be provisioned on this dev box — that is a
    /// legitimate skip (TestArtifacts.SkipIfMissing), not a defect. On CI,
    /// TestArtifacts.SkipIfMissing() itself fails loud instead of skipping, so reaching
    /// BcEngineReadinessGuard.AssertReadyOnCi on CI already proves artifacts are present;
    /// if the engine is STILL not ready there, that is the regression this test exists to
    /// catch (see the comment on BcEngineReadinessGuard.AssertReadyOnCi).
    /// </summary>
    [SkippableFact]
    public void Ready_IsTrue_WhenArtifactsAreProvisioned()
    {
        TestArtifacts.SkipIfMissing();

        BcEngineReadinessGuard.AssertReadyOnCi(_engine.Ready, _engine.SkipReason, TestArtifacts.RunningOnCi);

        // Off CI with artifacts present but the engine still not ready (e.g. a genuinely cold
        // local Cecil cache on the very first run) is a real, if unusual, local skip — not a
        // silent pass: TestArtifacts.SkipIf raises a visible Skipped rather than falling off
        // the end of the method.
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        Assert.True(_engine.Ready);
    }

    // ---- BcEngineReadinessGuard.AssertReadyOnCi: pure logic, proven without any BC
    // ---- artifacts or CI environment ------------------------------------------------

    [Fact]
    public void AssertReadyOnCi_Throws_WhenNotReadyOnCi()
    {
        var ex = Record.Exception(() =>
            BcEngineReadinessGuard.AssertReadyOnCi(ready: false, skipReason: "Ncl Cecil cache was cold", runningOnCi: true));

        Assert.NotNull(ex);
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(ex);
        var msg = ex!.Message;
        Assert.Contains("Ncl Cecil cache was cold", msg, StringComparison.Ordinal);
        Assert.Contains("issue #1813", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertReadyOnCi_DoesNothing_WhenReadyOnCi()
    {
        Assert.Null(Record.Exception(() =>
            BcEngineReadinessGuard.AssertReadyOnCi(ready: true, skipReason: null, runningOnCi: true)));
    }

    /// <summary>Negative direction for the CI gate itself: off CI, "not ready" is not a defect.</summary>
    [Fact]
    public void AssertReadyOnCi_DoesNothing_WhenNotReadyButOffCi()
    {
        Assert.Null(Record.Exception(() =>
            BcEngineReadinessGuard.AssertReadyOnCi(ready: false, skipReason: "BC artifacts not provisioned", runningOnCi: false)));
    }
}
