// XunitRunnerVisualStudioAdapterVersionTests — issue #1844: `dotnet test AlRunner.Tests`
// exited 1 with zero test failures ("Passed! - Failed: 0, Passed: 590" followed by
// "##[error]Process completed with exit code 1"). The crash was in VSTest's own teardown,
// not our code:
//
//   VisualStudioSourceInformationProvider.DisposeAsync()
//     -> DiaSessionWrapper.Dispose()
//       -> DiaSession.Dispose(bool)
//         -> PortableSymbolReader.Dispose()   <-- NullReferenceException here
//
// Investigation (decompiled xunit.runner.visualstudio.testadapter.dll for versions 3.1.0
// through 3.1.5 via ilspycmd against the packages fetched straight from nuget.org — not
// guessed):
//
//   1. The obvious first fix to reach for is <CollectSourceInformation>false</...> in the
//      generated engine.runsettings (skip building a DiaSession/PDB cache at all). That flag
//      does NOT help here: VsTestRunner.RunTestsInAssembly / DiscoverTests unconditionally
//      construct a VisualStudioSourceInformationProvider (and therefore a DiaSessionWrapper
//      and a DiaSession) before Xunit2.ForDiscoveryAndExecution ever runs, in every version
//      checked (3.1.0..3.1.5). CollectSourceInformation only gates a DIFFERENT, Cecil-based
//      ISourceInformationProvider used by the xunit v3 in-process TestingPlatform launcher
//      (Xunit.Runner.v3.Xunit3) and as a fallback inside Xunit2.ForDiscoveryAndExecution when
//      no provider was already supplied — never true here, since VsTestRunner always supplies
//      one. AlRunner.Tests references xunit 2.9.3 (v2), so this repo is always on the
//      unconditional path. Setting CollectSourceInformation=false would not have prevented
//      the crash.
//   2. 3.1.0 (what this project pinned before this fix) through 3.1.2: DiaSessionWrapper.
//      Dispose() and VisualStudioSourceInformationProvider.DisposeAsync() call
//      `session.Dispose()` directly, with no try/catch and no double-dispose guard. Any
//      exception raised inside DiaSession.Dispose(bool) -> PortableSymbolReader.Dispose()
//      propagates straight out of the test host as a "Catastrophic failure" — exactly the CI
//      stack trace above — regardless of how many tests passed.
//   3. 3.1.3 adds a disposal lock, but only to make double-dispose throw
//      ObjectDisposedException instead of corrupting state; it still does not touch the
//      underlying NRE.
//   4. 3.1.4 is the exact version that wraps both call sites in
//      `System.DisposableExtensions.SafeDispose` (try/catch-swallow) AND makes disposal
//      idempotent (no-op on second call) instead of throwing. 3.1.5 (this project's current
//      pin) carries the same fix forward.
//
// So the fix that actually closes the crash path is bumping xunit.runner.visualstudio to
// 3.1.4 or later (AlRunner.Tests.csproj pins 3.1.5) — not a runsettings change. This is a
// mechanism test, not a corpus/BC-behaviour test (see
// .claude/rules/bc-behavior-tests-go-upstream.md): the claim is entirely about which VSTest
// adapter binary this repo's own `dotnet test` step resolves and ships, so it belongs here,
// not upstream.
//
// What this test can and cannot prove
// ------------------------------------
// It cannot execute a nested `dotnet test` run and observe the exit code directly — that
// would require reproducing the exact CI host (DOTNET_STARTUP_HOOKS wiring an in-process BC
// engine, real BC assemblies with PDB gaps) inside a test, which is neither hermetic nor
// fast. What IS provable, and IS a meaningful regression guard: the adapter DLL our own
// `dotnet test AlRunner.Tests` step actually resolves and deploys next to the test assembly
// (not just the version string written in the .csproj — a lockfile/restore mismatch would
// show up here too) carries a file version at or above 3.1.4.0, the threshold established
// above. A revert of the csproj pin, or a downgrade via a central package management override
// elsewhere in the repo, fails this test.
using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

public sealed class XunitRunnerVisualStudioAdapterVersionTests
{
    /// <summary>
    /// The exact version xunit.runner.visualstudio started wrapping DiaSessionWrapper.
    /// Dispose() / VisualStudioSourceInformationProvider.DisposeAsync()'s underlying
    /// session.Dispose() call in a try/catch-swallow (see file header, point 4).
    /// </summary>
    private static readonly Version MinimumFixedVersion = new(3, 1, 4);

    [Fact]
    public void DeployedAdapter_MeetsMinimumFixedVersion()
    {
        string adapterPath = Path.Combine(AppContext.BaseDirectory, "xunit.runner.visualstudio.testadapter.dll");

        Assert.True(File.Exists(adapterPath),
            $"xunit.runner.visualstudio.testadapter.dll not found next to the test assembly at " +
            $"'{adapterPath}' — AlRunner.Tests.csproj's xunit.runner.visualstudio PackageReference " +
            "should deploy it there (IncludeAssets includes 'build').");

        var fileVersionInfo = FileVersionInfo.GetVersionInfo(adapterPath);
        var deployedVersion = new Version(
            fileVersionInfo.FileMajorPart,
            fileVersionInfo.FileMinorPart,
            fileVersionInfo.FileBuildPart);

        Assert.True(deployedVersion >= MinimumFixedVersion,
            $"Deployed xunit.runner.visualstudio.testadapter.dll is version {deployedVersion}, " +
            $"below the {MinimumFixedVersion} floor established by issue #1844 " +
            "(DiaSessionWrapper.Dispose()/VisualStudioSourceInformationProvider.DisposeAsync() did " +
            "not catch exceptions from the underlying DiaSession.Dispose() before 3.1.4, so a " +
            "PortableSymbolReader.Dispose() NRE during teardown crashed the whole test host with " +
            "exit code 1 even when every test passed). Check AlRunner.Tests.csproj's " +
            "xunit.runner.visualstudio PackageReference version.");
    }

    /// <summary>
    /// Negative direction (tdd.md): proves the version comparison in the test above is not a
    /// tautology. 3.1.0.0 is the exact version this project was pinned to before issue #1844 —
    /// it must NOT satisfy the >= 3.1.4 floor, or the positive assertion above would pass
    /// against any resolved version at all, including the one that actually crashed CI.
    /// </summary>
    [Fact]
    public void PreFixVersion_DoesNotMeetMinimumFixedVersion()
    {
        var preFixVersion = new Version(3, 1, 0, 0);

        Assert.False(preFixVersion >= MinimumFixedVersion,
            $"{preFixVersion} unexpectedly satisfies the {MinimumFixedVersion} floor — the " +
            "comparison used by DeployedAdapter_MeetsMinimumFixedVersion would not have caught " +
            "the pre-fix pin that shipped issue #1844.");
    }
}
