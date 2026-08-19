// EngineTestBinResolverStartupHook — first half of the DOTNET_STARTUP_HOOKS chain that
// makes AlRunner.Tests' in-process BC engine tests (BcEngineCollection) actually execute.
// See AlRunner.Tests/EngineStartupHook.cs for the full mechanism and issue #1813.
//
// Why this lives in AlRunner.dll, as a SEPARATE hook from AlRunner.Tests.dll's own
// -----------------------------------------------------------------------------------
// Measured: pointing DOTNET_STARTUP_HOOKS at AlRunner.Tests.dll alone is not sufficient.
// Entering ANY method the .NET hosting layer invokes on a startup-hook assembly forces
// that assembly's OWN [ModuleInitializer]s to run FIRST — including
// BcEngineBootstrap.Initialize() — but that happens BEFORE the body of AlRunner.Tests'
// own StartupHook.Initialize() gets a chance to run, so anything it does (installing a
// resolver, say) is already too late for BcEngineBootstrap.Initialize() itself. And
// BcEngineBootstrap.Initialize() needs one: a startup hook is loaded directly off its
// absolute path, outside VSTest's normal probing setup (AlRunner.Tests.deps.json isn't
// consulted yet — that wiring belongs to VSTest, and it hasn't run yet at this point in
// the host's startup sequence). Confirmed by reproduction: without this hook,
// BcEngineBootstrap.Initialize() throws FileNotFoundException for the `al-runner`
// assembly the moment it references AlRunner.Infrastructure.BcArtifacts.
//
// DOTNET_STARTUP_HOOKS runs every configured hook assembly's Initialize() IN THE ORDER
// LISTED (':'-separated on Linux). bc-tests.yml lists THIS assembly's bin-deployed
// copy inside AlRunner.Tests' own output directory FIRST, then AlRunner.Tests.dll SECOND
// — so this hook's Resolving handler is already installed by the time AlRunner.Tests.dll
// is entered and its module initializer runs.
//
// Why this hook ALSO clears DOTNET_STARTUP_HOOKS from the process environment
// -----------------------------------------------------------------------------
// Measured regression: environment variables set on the testhost process (this is set via
// VSTest's RunConfiguration.EnvironmentVariables, scoped to the testhost child process —
// see bc-tests.yml / engine.runsettings) are, by default, INHERITED by every subprocess
// the test suite itself spawns with Process.Start — and roughly two dozen AlRunner.Tests
// classes (CliServer, WatchTests, OutputFormatTests, …) spawn `dotnet run --project
// AlRunner …` to test the CLI/server's own behaviour. Left set, those child `al-runner`
// processes ALSO honour DOTNET_STARTUP_HOOKS (every corehost-launched .NET process does),
// re-running this whole bootstrap a second time inside the very PRODUCT the test is trying
// to observe — printing extra "[Cecil] …" / "[BcRuntime] …" diagnostic lines to its stdout
// before the line the test expects to be first (e.g. CliServer's `--server` readiness
// signal), and risking an outright assembly-identity conflict (two different-path copies of
// `al-runner`, itself and this hook's own copy, loaded into one AssemblyLoadContext).
//
// Clearing it here — the first hook in the chain, before AlRunner.Tests.dll (hook two) is
// even entered — does not undo the hooks already loaded into THIS process (hostfxr decided
// which hooks to run before any hook's managed code got a chance to execute), it only stops
// the variable from being read again if and when this process's own env is later inherited
// by a Process.Start call. That is exactly what we want: BcEngineBootstrap runs once, here,
// then gets out of the way of every subprocess-spawning test.
//
// Deliberately global namespace: DOTNET_STARTUP_HOOKS requires a non-nested,
// non-namespaced type literally named `StartupHook`.

/// <summary>DOTNET_STARTUP_HOOKS entry point — see the file header.</summary>
internal static class StartupHook
{
    /// <summary>
    /// Installs a same-directory assembly-resolution fallback rooted at wherever THIS copy
    /// of al-runner.dll was loaded from, then clears DOTNET_STARTUP_HOOKS from the process
    /// environment so it does not leak into subprocesses this test suite spawns (see file
    /// header). When the workflow points DOTNET_STARTUP_HOOKS at the copy inside
    /// AlRunner.Tests' own bin dir, that directory is exactly where every one of
    /// AlRunner.Tests' dependencies (al-runner.dll itself, xunit, Xunit.SkippableFact,
    /// Spectre.Console.Testing, …) already sits — MSBuild deploys them all to one output
    /// directory. So a same-directory-by-name fallback is sufficient; nothing here needs to
    /// know AlRunner.Tests' specific dependency list.
    /// </summary>
    public static void Initialize()
    {
        var hookDir = System.IO.Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);
        if (!string.IsNullOrEmpty(hookDir))
        {
            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                if (name.Name is null) return null;
                var candidate = System.IO.Path.Combine(hookDir, name.Name + ".dll");
                return System.IO.File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
        }

        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);
    }
}
