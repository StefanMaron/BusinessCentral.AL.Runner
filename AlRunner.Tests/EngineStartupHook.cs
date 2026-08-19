// EngineStartupHook — makes the 15 in-process BC engine tests (BcEngineCollection)
// actually reachable instead of permanently skipping. See issue #1813.
//
// Root cause (measured, not inferred — a DOTNET_STARTUP_HOOKS diagnostic hook that logs
// every Microsoft.Dynamics.Nav.* AssemblyLoad event with a full stack trace)
// -------------------------------------------------------------------------------------
// BcEngineBootstrap.Initialize() ([ModuleInitializer] in BcEngineCollection.cs) is the
// earliest hook a test host gives OUR code — but it is not the earliest thing that runs
// in the test host process. Before xUnit executes a single line of our code, the VSTest
// xUnit adapter builds a source-line cache for stack traces:
//
//   Xunit.Runner.VisualStudio.VisualStudioSourceInformationProvider..ctor
//     -> DiaSession..ctor(assemblyFileName: "...AlRunner.Tests.dll")
//     -> PortableSymbolReader.PopulateCacheForTypeAndMethodSymbols
//     -> RuntimeModule.GetTypes()
//
// `Module.GetTypes()` forces the CLR to fully resolve every type declared in
// AlRunner.Tests.dll, including the field types of classes like
// SkeletonSharedObjectContainerLeakTests (ITreeObject, TreeHandler,
// TreeSharedObjectContainer — all Microsoft.Dynamics.Nav.Ncl types) — which loads
// Microsoft.Dynamics.Nav.Ncl.dll as a side effect. Measured locally: an
// AppDomain.AssemblyLoad probe attached via a diagnostic startup hook caught exactly this
// call stack loading Ncl BEFORE anything else touched the assembly.
//
// Crucially, `Module.GetTypes()` is metadata-driven type LOADING, not code EXECUTION — a
// second measurement (a throwaway two-project repro: a lib with a [ModuleInitializer] and
// a host that does `Assembly.LoadFrom(...).GetTypes()`) confirmed the module initializer
// does NOT fire merely from that call. It fires only once something in the module is
// actually INVOKED (even via reflection). So by the time BcEngineBootstrap.Initialize()
// gets its first chance to run — on the first real method call into AlRunner.Tests.dll —
// DiaSession has already loaded the un-rewritten Ncl, and the rewrite permanently no-ops
// (NclCecilRewrite.RewriteInPlace's "already loaded" guard).
//
// The fix
// --------
// DOTNET_STARTUP_HOOKS is the one hook that is guaranteed to run BEFORE the test host's
// own entry point (testhost's Main, which is what eventually constructs DiaSession) — see
// https://github.com/dotnet/runtime — .NET hosting invokes every configured startup
// hook's `Initialize()` before Main() runs, in-process, same AppDomain. The hosting layer
// invokes it via reflection, which — per the second measurement above — DOES trigger this
// module's [ModuleInitializer] first. So merely being loaded and invoked as a startup hook
// is enough: entering this (empty) method forces BcEngineBootstrap.Initialize() to run to
// completion strictly before DiaSession gets a chance to touch Nav types.
//
// Wiring: .github/workflows/bc-tests.yml sets
//   DOTNET_STARTUP_HOOKS=<AlRunner.Tests-bin copy of al-runner.dll>:<AlRunner.Tests.dll>
// on the `dotnet test` invocation — TWO hooks, in that order. The first
// (AlRunner/EngineTestBinResolverStartupHook.cs) installs a same-directory dependency
// resolver BEFORE this assembly is ever entered; see that file for why a second, separate
// hook assembly is required rather than doing it here. A local `dotnet test` run WITHOUT
// DOTNET_STARTUP_HOOKS set behaves exactly as before (BcEngineCollection tests skip with
// their existing, accurate reasons) — this file changes nothing for a host that doesn't
// opt in.
//
// Deliberately in the GLOBAL namespace, not `AlRunner.Tests`: the startup hook convention
// requires a non-nested, non-namespaced type literally named `StartupHook`
// (https://learn.microsoft.com/dotnet/core/runtime-config/#startup-hooks).

/// <summary>
/// DOTNET_STARTUP_HOOKS entry point. See the file header for the full mechanism this
/// exists to defeat.
/// </summary>
internal static class StartupHook
{
    /// <summary>
    /// Intentionally empty. Entering this method is the entire mechanism (see file
    /// header): it forces AlRunner.Tests.dll's module — and therefore its
    /// [ModuleInitializer] (BcEngineBootstrap.Initialize) — to run first, before the
    /// host's own Main() and everything downstream of it (including VSTest's
    /// DiaSession). By the time this line would execute, that module initializer has
    /// already completed, so there is nothing left to do here.
    /// </summary>
    public static void Initialize()
    {
    }
}
