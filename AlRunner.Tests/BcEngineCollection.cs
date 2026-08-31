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
// Members: BcCompilerEmitRetryTests, BcCompilerProfileEmitCrashTests,
// SkeletonSharedObjectContainerLeakTests, RecordBulkWriteNotesTransactionTests,
// CodeunitRunWriteTransactionRefusalTests.
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

    private static string FileHash(string path)
    {
        if (!File.Exists(path)) return "<missing>";
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
    }

    private static void Probe(string msg)
    {
        var path = Environment.GetEnvironmentVariable("AL_RUNNER_TEST_ENGINE_PROBE");
        if (string.IsNullOrEmpty(path)) return;
        try { File.AppendAllText(path, msg + "\n"); } catch { }
    }

    [ModuleInitializer]
    internal static void Initialize()
    {
        Probe($"init: nclLoaded={AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl")}");

        string serviceTierDir;
        try
        {
            // ServiceTierDir's own EnsureSelected() fallback (used when nothing has called
            // SelectVersion yet) picks the numerically HIGHEST cached artifact — fine for a
            // human running the CLI with no opinion, wrong here: this bootstrap must rewrite
            // and load the SAME BC version this test assembly was built and linked against
            // (_BCVersion), not whichever version happens to be newest in ~/.local/share/al-runner
            // /artifacts. On a dev box with several BC versions cached (every version ever
            // built/tested locally accumulates there) the highest-cached one is usually a
            // different major than the build, silently corrupting bin/Ncl.dll with a
            // wrong-major rewrite that the rest of this test run then treats as "the engine".
            // Program.cs's own no-arg default (DefaultVersionPrefix keyed off
            // EngineBuiltVersion()) is the correct selection to mirror here.
            var built = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion();
            var prefix = AlRunner.Infrastructure.BcArtifacts.DefaultVersionPrefix(
                built, AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir);
            AlRunner.Infrastructure.BcArtifacts.SelectVersion(prefix, null);
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
            //
            // Deliberately typeof(BcEngineBootstrap).Assembly.Location, NOT
            // AppContext.BaseDirectory: this initializer can now run inside a process whose
            // entry assembly is NOT AlRunner.Tests.dll — DOTNET_STARTUP_HOOKS (see
            // EngineStartupHook.cs) can load this assembly's module from inside the outer
            // `dotnet test` CLI driver process too, where AppContext.BaseDirectory resolves
            // to the .NET SDK's own install directory, not AlRunner.Tests' bin dir. Measured:
            // without this, that combination rewrote Microsoft.Dynamics.Nav.Ncl.dll INTO the
            // SDK's own folder. Assembly.Location always names THIS assembly's own file, in
            // every process that loads it, regardless of which assembly is the entry point.
            var binNcl = Path.Combine(
                Path.GetDirectoryName(typeof(BcEngineBootstrap).Assembly.Location) ?? AppContext.BaseDirectory,
                "Microsoft.Dynamics.Nav.Ncl.dll");

            // Snapshot before, so we can tell whether the rewrite actually CHANGED the file
            // in this process. Writing bin/…Ncl.dll and then loading it in the same process
            // is the documented BadImageFormatException 0x80131124 hazard — and it is the
            // write that matters, not merely whether the Cecil cache missed. After a fresh
            // build (bin holds the pristine Ncl) even a cache HIT rewrites the file here.
            var before = FileHash(binNcl);

            var coldRewrite = AlRunner.Infrastructure.NclCecilRewrite.RewriteInPlace(serviceTierDir, binNcl);
            var changed = FileHash(binNcl) != before;
            Probe($"rewrite: cold={coldRewrite} changed={changed} binNcl={binNcl} exists={File.Exists(binNcl)}");

            if (changed)
            {
                // We just replaced the file this process is about to load. Skip rather than
                // risk a torn image; the NEXT run finds bin already rewritten, the copy is a
                // no-op, and these tests execute normally.
                SkipReason =
                    "bin/Microsoft.Dynamics.Nav.Ncl.dll was rewritten by this process (first run " +
                    "after a build), so loading it here risks BadImageFormatException 0x80131124. " +
                    "Re-run the suite — bin is now rewritten and these tests will execute.";
                return;
            }

            if (coldRewrite)
            {
                // true == the Cecil cache MISSed and we just rewrote Ncl in THIS process.
                // A process that performs the rewrite then loads the result in-process
                // intermittently dies with BadImageFormatException 0x80131124 — which is
                // exactly why Program.cs re-execs on a `true` return. A test host cannot
                // re-exec, so the honest answer is to skip rather than crash.
                //
                // CI hits this on every fresh runner (cold ~/.cache/al-runner/ncl-cecil).
                // The workflow warms the cache with a throwaway runner invocation before
                // `dotnet test` so these tests really execute there instead of skipping.
                SkipReason =
                    "Ncl Cecil cache was cold, so the rewrite happened in this process; " +
                    "loading it here would risk BadImageFormatException 0x80131124. Warm the " +
                    "cache with one runner invocation before `dotnet test` to run these tests.";
                return;
            }

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

/// <summary>
/// The acceptance check issue #1813 names: on a CI leg — where BC artifacts are
/// provisioned and the Cecil cache is warmed by construction (bc-tests.yml) —
/// <see cref="BcEngineBootstrap.Ready"/> being false is a regression, not a legitimate
/// skip. Fifteen tests in the bc-engine-serial collection skipped silently on every CI
/// run for months because VSTest's own DiaSession loaded Microsoft.Dynamics.Nav.Ncl
/// before <see cref="BcEngineBootstrap.Initialize"/> got a chance to run the Cecil
/// rewrite — reported Skipped, not Failed, so the leg stayed green while those tests
/// asserted nothing. See AlRunner.Tests/EngineStartupHook.cs and
/// AlRunner/EngineTestBinResolverStartupHook.cs for the fix.
///
/// Deliberately takes <paramref name="ready"/> / <paramref name="skipReason"/> /
/// <paramref name="runningOnCi"/> as PARAMETERS rather than reading
/// <see cref="BcEngineFixture"/> and <see cref="TestArtifacts.RunningOnCi"/> directly —
/// mirrors <see cref="TestArtifacts.SkipIfMissingIn"/>'s own CI-fails/local-skips shape
/// exactly, and for the same reason: a pure function is provable in isolation (RED/GREEN
/// with constructed booleans, in BcEngineReadinessGuardTests) with no BC artifacts and no
/// CI environment required to run the proving test.
/// </summary>
internal static class BcEngineReadinessGuard
{
    internal static void AssertReadyOnCi(bool ready, string? skipReason, bool runningOnCi)
    {
        if (ready || !runningOnCi) return;

        Assert.Fail(
            "BcEngineBootstrap.Ready is false on a CI leg where BC artifacts are provisioned and " +
            "the Cecil cache is pre-warmed before `dotnet test` runs (see the 'Warm the Ncl Cecil " +
            "rewrite cache' and 'Generate .runsettings for in-process BC engine tests' steps in " +
            ".github/workflows/bc-tests.yml). That combination means the DOTNET_STARTUP_HOOKS " +
            "wiring that makes BcEngineBootstrap's [ModuleInitializer] run before VSTest's own " +
            "DiaSession (see AlRunner.Tests/EngineStartupHook.cs) has silently stopped taking " +
            "effect — issue #1813 all over again — and every test in the bc-engine-serial " +
            "collection is now executing NOTHING while this leg still reports green. " +
            "SkipReason: " + (skipReason ?? "<none>"));
    }
}
