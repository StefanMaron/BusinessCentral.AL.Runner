// BcEngineCollection — serializes the tests that load the BC engine IN-PROCESS and
// performs the Ncl Cecil rewrite exactly once, before any of them runs.
//
// The race being fixed
// --------------------
// Production (Program.cs) Cecil-rewrites Microsoft.Dynamics.Nav.Ncl.dll in-place in the
// app's own bin dir BEFORE anything touches it, then re-execs on a cold rewrite so the
// child starts clean. A test host cannot re-exec, and — worse — xUnit runs test classes
// from different collections in PARALLEL in one process. So one class could call
// NclCecilRewrite.RewriteInPlace() (which OVERWRITES bin/…Ncl.dll on disk) while another
// class was concurrently loading types out of that very file.
//
// The reader then mapped a half-written image, which surfaced as torn-metadata errors far
// from the cause and with a different face each run:
//   - BadImageFormatException "Index not found. (0x80131124)"
//   - CultureNotFoundException from RuntimeAssembly.GetLocale() reading a garbage locale
//   - TypeInitializationException on NavEnvironment (un-rewritten cctor -> WindowsIdentity)
// Which class won the race decided which one failed, so the two tests appeared to fail
// alternately and neither reproduced in isolation.
//
// The fix: every test that touches the in-process BC engine joins this collection.
// DisableParallelization keeps them off each other, and the fixture below does the rewrite
// once at collection start — so the file is fully written before any member test loads it,
// and nothing rewrites it again afterwards.
//
// Members: BcCompilerEmitRetryTests, SkeletonSharedObjectContainerLeakTests.
// Any NEW test that loads Microsoft.Dynamics.Nav.* types in-process belongs here too.

using System.Runtime.CompilerServices;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runs the BC engine bootstrap at ASSEMBLY LOAD, before xUnit executes any test.
///
/// A collection fixture is too late. Ncl.dll is rewritten ON DISK, but ~180 tests in this
/// assembly touch AlRunner types that transitively load Ncl types, and once the CLR has
/// mapped the un-rewritten image no later rewrite of the file can affect the loaded module.
/// Whichever ran first decided whether the engine tests saw a patched NavEnvironment — hence
/// the "PlatformNotSupportedException: Windows Principal functionality" cctor failure that
/// appeared only in a full-suite run and never in isolation.
///
/// [ModuleInitializer] is the earliest hook a test host gives us, and it is the test-host
/// stand-in for the re-exec Program.cs performs after a cold rewrite.
/// </summary>
internal static class BcEngineBootstrap
{
    internal static bool Ready { get; private set; }
    internal static string? SkipReason { get; private set; }

    [ModuleInitializer]
    internal static void Initialize()
    {
        string serviceTierDir;
        try
        {
            serviceTierDir = AlRunner.Infrastructure.BcArtifacts.ServiceTierDir;
        }
        catch (Exception ex)
        {
            SkipReason = $"BC artifacts not provisioned: {ex.Message}";
            return;
        }

        if (!File.Exists(Path.Combine(serviceTierDir, "Microsoft.Dynamics.Nav.CodeAnalysis.dll"))
            || !File.Exists(Path.Combine(serviceTierDir, "Microsoft.Dynamics.Nav.Ncl.dll")))
        {
            SkipReason = $"BC artifacts incomplete in '{serviceTierDir}'";
            return;
        }

        try
        {
            // Order mirrors Program.cs: rewrite Ncl FIRST, before any AlRunner type resolves
            // an Ncl type and forces the un-rewritten file to load.
            var binNcl = Path.Combine(AppContext.BaseDirectory, "Microsoft.Dynamics.Nav.Ncl.dll");
            AlRunner.Infrastructure.NclCecilRewrite.RewriteInPlace(serviceTierDir, binNcl);

            DependencyLoader.EnsureResolverInstalled_Public();
            BcRuntime.EnsureApplied();
            Ready = true;
        }
        catch (Exception ex)
        {
            // Report rather than take the whole assembly down: without artifacts-backed
            // engine state only the two engine tests are affected, and they no-op below.
            SkipReason = $"BC engine bootstrap failed: {ex.GetType().Name}: {ex.Message}";
        }
    }
}

/// <summary>
/// Prepares the in-process BC engine once: Cecil-rewrites Ncl.dll into the test host's own
/// bin dir, installs the dependency-resolving ALC handler, and applies the runtime patches —
/// the same bootstrap Program.cs performs, minus the re-exec a test host cannot do.
/// </summary>
public sealed class BcEngineFixture
{
    /// <summary>
    /// True when BC service-tier artifacts are provisioned on this machine AND the engine
    /// bootstrap succeeded. Member tests must no-op when this is false: a bare CI leg
    /// without artifacts is not a failure, it simply cannot exercise the engine.
    /// </summary>
    public bool Ready => BcEngineBootstrap.Ready;

    /// <summary>Why <see cref="Ready"/> is false, for a test that wants to report it.</summary>
    public string? SkipReason => BcEngineBootstrap.SkipReason;
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BcEngineCollection : ICollectionFixture<BcEngineFixture>
{
    public const string Name = "bc-engine-serial";
}
