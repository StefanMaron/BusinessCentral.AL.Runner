// BcAssembler — Roslyn-compiles emitted C# against real BC DLLs.
//
// Pre-compile passes:
//   1. ApplyPolyfillRedirects — string substitutions routing AL-compiler-emitted
//      references for APIs that don't exist on the real service-tier DLLs to
//      small in-process polyfill shims (defined inline as PolyfillSource).
//   2. CallSiteArgWrap — fixes the residual call-site ByRef gap BC's emitter
//      doesn't cover (e.g. `dict.ALGet(K, fieldOfHandleT)` → wraps the field arg
//      as `new ByRef<T>(() => expr, v => expr = v)`). BC's emitter handles
//      parameter-declaration ByRef wraps natively at codeanalysis.cs:342854 —
//      no syntax rewriter needed for those.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using AlRunner.Rewriters;

namespace AlRunner;

public sealed record CompileResult(byte[]? AssemblyBytes, IReadOnlyList<string> Errors)
{
    public bool Success => AssemblyBytes != null;
}

public sealed class BcAssembler
{
    public string ServiceTierDir { get; init; } =
        AlRunner.Infrastructure.BcArtifacts.ServiceTierDir;

    // Roslyn's internal recursion on large bundles can overflow the default 8 MB stack.
    // Run the full compile pass on a thread with 64 MB stack to avoid SIGSEGV.
    private const int CompileStackSize = 64 * 1024 * 1024;

    public CompileResult Compile(string assemblyName, IEnumerable<EmittedSource> sources)
    {
        CompileResult? result = null;
        Exception? threadEx = null;
        var t = new Thread(() =>
        {
            try { result = CompileCore(assemblyName, sources); }
            catch (Exception ex) { threadEx = ex; }
        }, CompileStackSize);
        t.Start();
        t.Join();
        if (threadEx != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(threadEx).Throw();
        return result!;
    }

    private CompileResult CompileCore(string assemblyName, IEnumerable<EmittedSource> sources)
    {
        var sourceList = sources.ToList();
        if (Environment.GetEnvironmentVariable("DUMP_CS") == "1")
            foreach (var s in sourceList)
                File.WriteAllText(Path.Combine(Path.GetTempPath(), $"gen_{s.Name}.cs"), s.Code);
        var trees = sourceList
            .Select(s => CSharpSyntaxTree.ParseText(
                ApplyPolyfillRedirects(s.Code), path: s.Name + ".cs"))
            .ToList();
        // Inject helpers for runtime-API mismatches between alc-emit and the
        // service-tier DLLs. PolyfillRedirects above route callers here.
        trees.Add(CSharpSyntaxTree.ParseText(PolyfillSource, path: "_polyfill.cs"));

        var refs = ReferencePaths().Select(p => MetadataReference.CreateFromFile(p)).ToList();

        // Fill BC's call-site ByRef gap. Runs a throwaway compile to find CS1503
        // 'cannot convert T to ByRef<T>' errors and rewrites only those args.
        trees = CallSiteArgWrap.Apply(trees, refs).ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                concurrentBuild: false,
                checkOverflow: true,
                optimizationLevel: OptimizationLevel.Release));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var errs = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();
            return new CompileResult(null, errs);
        }
        var bytes = ms.ToArray();
        if (Environment.GetEnvironmentVariable("AL_RUNNER_DUMP_BC_ASM") == "1")
        {
            try
            {
                var dumpPath = Path.Combine(Path.GetTempPath(), assemblyName + ".dll");
                File.WriteAllBytes(dumpPath, bytes);
                Console.Error.WriteLine($"[BcAssembler] dumped {assemblyName} → {dumpPath}");
            }
            catch { /* best-effort */ }
        }
        return new CompileResult(bytes, Array.Empty<string>());
    }

    private IEnumerable<string> ReferencePaths()
    {
        // Real BC service-tier DLLs
        foreach (var n in new[] { "Microsoft.Dynamics.Nav.Types", "Microsoft.Dynamics.Nav.Ncl",
                                  "Microsoft.Dynamics.Nav.Common", "Microsoft.Dynamics.Nav.Language",
                                  "Microsoft.Dynamics.Nav.Types.Report", "Microsoft.Dynamics.Nav.Types.Report.Base",
                                  "Microsoft.Dynamics.Nav.Types.Report.Runtime", "Microsoft.Dynamics.Nav.Core" })
        {
            var p = Path.Combine(ServiceTierDir, n + ".dll");
            if (File.Exists(p)) yield return p;
        }
        // .NET shared framework — System.Runtime, mscorlib equivalents.
        // IMPORTANT: some BC-bundled NuGet assemblies also match "System.*"
        // (e.g. System.IdentityModel.Tokens.Jwt) but are versioned to the target BC
        // release, NOT the .NET shared framework. Prefer the SELECTED ServiceTierDir
        // copy whenever it exists so compile references track the target BC version
        // instead of whatever CopyLocal put in bin at build time (a step toward one
        // binary spanning BC minor versions). Pure-BCL System.* (System.Runtime, …)
        // are not in ServiceTierDir, so they fall through to the bin/TPA copy.
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (var p in tpa.Split(Path.PathSeparator))
        {
            var name = Path.GetFileNameWithoutExtension(p);
            if (name.StartsWith("System.") || name == "mscorlib" || name == "netstandard")
            {
                var inArtifact = Path.Combine(ServiceTierDir, name + ".dll");
                yield return File.Exists(inArtifact) ? inArtifact : p;
            }
        }
        // The runner's own assembly — polyfill shims call back into AlRunner.BcRuntime
        // helpers (e.g. NCLEnumMetadata_CreateByIdAlAware) so AL emit-time captured
        // metadata is reachable from compiled-AL call sites.
        var runnerDll = typeof(BcAssembler).Assembly.Location;
        if (!string.IsNullOrEmpty(runnerDll) && File.Exists(runnerDll))
            yield return runnerDll;
    }

    // Source patches applied to emitted C# before parsing. Each entry redirects a
    // missing-in-runtime symbol to our polyfill. Pure string replace for now —
    // upgrade to a Roslyn rewriter only if false-positive matches show up.
    private static readonly (string from, string to)[] _polyfillRedirects = new[]
    {
        ("NavRuntimeHelpers.ThrowIfWrongArgumentCount",
         "global::AlRunnerShim.NavRuntimeHelpersShim.ThrowIfWrongArgumentCount"),
        // AL compiler 17.0.34 emits a 2-arg ConvertToDotNetFormatString(session, format) but
        // BC 27.5 only ships the 1-arg overload. Redirect to our shim that drops the session.
        ("ALCompiler.ConvertToDotNetFormatString(",
         "global::AlRunnerShim.NavRuntimeHelpersShim.ConvertToDotNetFormatString("),
        // NCLEnumMetadata.Create(int) chains through NavGlobal.MetadataProvider which NREs on the
        // skeleton session.  After JIT tiering the JMP-hook on that method is bypassed, so we
        // redirect at source level.  Our shim returns NCLOptionMetadata.Default which preserves
        // ordinal arithmetic for any enum value that callers create with NavOption.Create.
        ("NCLEnumMetadata.Create(",
         "global::AlRunnerShim.NavRuntimeHelpersShim.NCLEnumMetadataCreate("),
        // ALDebugger methods all throw NavObsoleteMethodException and have value-type params
        // (DataError enum) — redirect at source level to avoid JMP-hook ABI issues.
        ("ALDebugger.ALActivate(",     "global::AlRunnerShim.NavRuntimeHelpersShim.ALDebugger_ALActivate("),
        ("ALDebugger.ALDeactivate(",   "global::AlRunnerShim.NavRuntimeHelpersShim.ALDebugger_ALDeactivate("),
        ("ALDebugger.ALIsActive(",     "global::AlRunnerShim.NavRuntimeHelpersShim.ALDebugger_ALIsActive("),
        ("ALDebugger.ALIsAttached(",   "global::AlRunnerShim.NavRuntimeHelpersShim.ALDebugger_ALIsAttached("),
        ("ALDebugger.CheckPermissionToDebug(", "global::AlRunnerShim.NavRuntimeHelpersShim.ALDebugger_CheckPermissionToDebug("),
        // ALSession.ALStopSession sync wrappers NRE via session.Diagnostics; return false.
        ("ALSession.ALStopSession(",   "global::AlRunnerShim.NavRuntimeHelpersShim.ALSession_StopSession("),
        // ALSession.ALGetExecutionContext / ALGetModuleExecutionContext NRE via session properties.
        // Return ExecutionContext.Normal (0) which is the expected value in a headless runner.
        ("ALSession.ALGetExecutionContext(",         "global::AlRunnerShim.NavRuntimeHelpersShim.ALGetExecutionContext("),
        ("ALSession.ALGetModuleExecutionContext(",   "global::AlRunnerShim.NavRuntimeHelpersShim.ALGetModuleExecutionContext("),
        // ALSession.ALSendTraceTag NREs via session.Diagnostics; telemetry is a no-op here.
        ("ALSession.ALSendTraceTag(",  "global::AlRunnerShim.NavRuntimeHelpersShim.ALSession_SendTraceTag("),
        // ALSessionInformation static properties NRE via session.SqlDebuggingStatisticsCheckPoint.
        // Return 0 — SQL counters are 0 in a skeleton/non-database run.
        ("ALSessionInformation.ALSqlRowsRead",         "global::AlRunnerShim.NavRuntimeHelpersShim.ALSqlRowsRead"),
        ("ALSessionInformation.ALSqlStatementsExecuted", "global::AlRunnerShim.NavRuntimeHelpersShim.ALSqlStatementsExecuted"),
        // ALSystemErrorHandling.ALGetLastErrorCallStack NREs via NavCurrentThread.Session; return "".
        ("ALSystemErrorHandling.ALGetLastErrorCallStack", "global::AlRunnerShim.NavRuntimeHelpersShim.ALGetLastErrorCallStack"),
        // NavSession.Sleep — real body NREs via session state on the skeleton runtime.
        // In-scope (§3.9): inline-execution model, no parallel sessions — Sleep is a no-op delay.
        // The shim sleeps the current thread by `duration` ms (clamped to >=0).
        ("NavSession.Sleep(", "global::AlRunnerShim.NavRuntimeHelpersShim.NavSession_Sleep("),
        // ALSession.ALIsSessionActive — real body chases session state that doesn't exist.
        // Faithful in-scope answer (§3.9): the runner runs sessions inline + synchronously,
        // so any session id is "no longer active" by the time the caller asks. Return false.
        ("ALSession.ALIsSessionActive(", "global::AlRunnerShim.NavRuntimeHelpersShim.ALSession_ALIsSessionActive("),
        // ALSession.ALStartSession — real body schedules an async session via NavCurrentThread/
        // Diagnostics which both NRE on the skeleton. Faithful in-scope replacement (§3.9):
        // dispatch the target codeunit synchronously in-process, assign a fresh non-zero
        // session id, and return true. Missing codeunit → return false (DataError.TrapError
        // pathway). See BcRuntime.AlRunnerStartSession for the dispatch logic.
        ("ALSession.ALStartSession(", "global::AlRunnerShim.NavRuntimeHelpersShim.ALSession_ALStartSession("),
        // NavForm.Run (static, non-modal) — OOS §3.11. BC emits the [Obsolete] sync wrapper
        // NavForm.Run(...) (not RunAsync) for Page.Run calls. The real body calls
        // RunAsync().AsTask().GetAwaiter().GetResult() which NREs deep in NavForm/NCLMetaForm
        // because the skeleton has no live session.  JmpHook.Apply() cannot intercept this
        // because the JIT resolves the call from freshly compiled AL code to a different
        // address than what the hook patches (R2R vs JIT code layout mismatch on .NET 8).
        // Source-level redirect is the reliable alternative: "NavForm.Run(" cannot be a
        // substring of "NavForm.RunModal(" so there is no false-positive risk.
        ("NavForm.Run(", "global::AlRunnerShim.NavRuntimeHelpersShim.NavForm_Run("),
        // NavTextExtensions.ALSubstring — AL contract is 1-based, consistent with all other
        // AL string positions (CopyStr, StrPos, etc.). The prior comment claiming v28+ is
        // 0-based for Substring was incorrect; the AL test library validates 1-based semantics
        // against real BC. Override with shims that consistently apply 1-based behaviour
        // regardless of which BC DLL version is loaded.
        ("NavTextExtensions.ALSubstring(",   "global::AlRunnerShim.NavRuntimeHelpersShim.ALSubstring("),
        // NavTextExtensions.ALIndexOf — AL contract is 1-based (0 = not found), consistent
        // with all other AL string positions (StrPos, CopyStr, SelectStr). The prior comment
        // claiming v28+ is 0-based was incorrect; the AL test library validated against real
        // BC confirms 1-based semantics. Override with shims that return 1-based results.
        ("NavTextExtensions.ALIndexOf(",     "global::AlRunnerShim.NavRuntimeHelpersShim.ALIndexOf("),
        // NavTextExtensions.ALLastIndexOf — same 1-based semantics.
        ("NavTextExtensions.ALLastIndexOf(", "global::AlRunnerShim.NavRuntimeHelpersShim.ALLastIndexOf("),
        // NavTextExtensions.ALIndexOfAny — v27 DLL doesn't have NavList<char> overloads; shim adds them
        // and converts 0-based C# results back to 1-based AL semantics (0 = not found).
        ("NavTextExtensions.ALIndexOfAny(",  "global::AlRunnerShim.NavRuntimeHelpersShim.ALIndexOfAny("),
        // NavTextExtensions.ALSplit — v27 DLL overloads don't accept NavList<char> text/separator
        // directly from the AL compiler. Redirect to the shim which adds those overloads while
        // preserving the same whole-string-delimiter semantics as real BC.
        ("NavTextExtensions.ALSplit(",       "global::AlRunnerShim.NavRuntimeHelpersShim.ALSplit("),
        // ALSystemString.ALMaxStrLen — v27 returns Int32.MaxValue for unlimited Text;
        // v28+ returns 0 for unlimited Text variables (NavDefinedLengthMetadata == Int32.MaxValue).
        ("ALSystemString.ALMaxStrLen(",      "global::AlRunnerShim.NavRuntimeHelpersShim.ALMaxStrLen("),
        // NavApp.GetCurrentModuleInfo — NREs via NavTenant.get_Database on skeleton.
        // Shim returns module info derived from the loaded bundle's app.json.
        ("ALNavApp.ALGetCurrentModuleInfo(", "global::AlRunnerShim.NavRuntimeHelpersShim.ALNavApp_GetCurrentModuleInfo("),
        // NavApp.GetModuleInfo(moduleId, info) — looks up installed extensions, throws on miss.
        // The runner has no installed-extensions registry — the only "extension" loaded is the
        // currently-running bundle. Shim matches against _currentBundleInfo.AppId and returns
        // false (not-found) for any other GUID, mirroring what real BC would return when an
        // unknown id is queried with errorLevel=DataError.Ignore.
        ("ALNavApp.ALGetModuleInfo(", "global::AlRunnerShim.NavRuntimeHelpersShim.ALNavApp_GetModuleInfo("),
        // NavApp.GetCallerModuleInfo has the same service-tier dependency as
        // GetCurrentModuleInfo in this in-process runner.
        ("ALNavApp.ALGetCallerModuleInfo(", "global::AlRunnerShim.NavRuntimeHelpersShim.ALNavApp_GetCallerModuleInfo("),
        // Database.LockTimeout get/set calls reach NavTenant.Database even though the corpus only
        // needs the API to be callable. Redirect property access to a runner-local value.
        ("ALDatabase.ALLockTimeout", "global::AlRunnerShim.NavRuntimeHelpersShim.ALDatabase_ALLockTimeout"),
        ("ALDatabase.ALGetDefaultTableConnection(", "global::AlRunnerShim.NavRuntimeHelpersShim.ALGetDefaultTableConnection("),
        ("ALDatabase.ALRegisterTableConnection(", "global::AlRunnerShim.NavRuntimeHelpersShim.ALRegisterTableConnection("),
        // ALSystemString.ALCopyStr — throws "outside of the permitted range" when fromPos < 1.
        ("ALSystemString.ALCopyStr(",      "global::AlRunnerShim.NavRuntimeHelpersShim.ALCopyStr("),
        // ALSystemString.ALIncStr — returns "" for non-numeric strings.
        ("ALSystemString.ALIncStr(",       "global::AlRunnerShim.NavRuntimeHelpersShim.ALIncStr("),
        // ALSystemString.ALSelectString — throws "does not contain a value for index" for invalid index.
        ("ALSystemString.ALSelectString(", "global::AlRunnerShim.NavRuntimeHelpersShim.ALSelectString("),
        // ALSystemString.ALStrPos — v27 DLL doesn't have NavList<char> overloads; shim adds them
        // while preserving the same semantics: returns 0 when substring is empty or not found.
        ("ALSystemString.ALStrPos(",       "global::AlRunnerShim.NavRuntimeHelpersShim.ALStrPos("),
    };

    private static string ApplyPolyfillRedirects(string code)
    {
        foreach (var (from, to) in _polyfillRedirects)
            code = code.Replace(from, to);
        return code;
    }

    private const string PolyfillSource = @"
namespace AlRunnerShim
{
    public static class NavRuntimeHelpersShim
    {
        public static void ThrowIfWrongArgumentCount(int expected, object[] args, string memberName)
        {
            if (args is null || args.Length != expected)
                throw new System.ArgumentException(
                    $""Expected {expected} argument(s) for '{memberName}', got {(args?.Length ?? 0)}"");
        }

        // AL compiler 17.0.34 emits ConvertToDotNetFormatString(session, format) but BC 27.5 only
        // ships the 1-arg overload. The 2-arg shim drops the session (not used by the 1-arg impl).
        public static Microsoft.Dynamics.Nav.Runtime.NavOemText ConvertToDotNetFormatString(
            object session, string format)
            => Microsoft.Dynamics.Nav.Runtime.ALCompiler.ConvertToDotNetFormatString(format);

        // Forward 1-arg calls that went through the redirect unchanged.
        public static Microsoft.Dynamics.Nav.Runtime.NavOemText ConvertToDotNetFormatString(
            string format)
            => Microsoft.Dynamics.Nav.Runtime.ALCompiler.ConvertToDotNetFormatString(format);

        // NCLEnumMetadata.Create(int) chains through NavGlobal.MetadataProvider which NREs on the
        // skeleton session.  Forward to AlRunner.BcRuntime.NCLEnumMetadata_CreateByIdAlAware
        // which returns a real NCLOptionMetadata subclass populated with the AL enum's
        // (names[], ordinals[]) so GetNames()/GetOrdinals() work; falls back to
        // NCLOptionMetadata.Default for system / dependency enums whose metadata isn't
        // captured at AL emit time.
        public static Microsoft.Dynamics.Nav.Runtime.NCLOptionMetadata NCLEnumMetadataCreate(int id)
            => global::AlRunner.BcRuntime.NCLEnumMetadata_CreateByIdAlAware(id);

        // ALDebugger — all classic-debugger methods are obsolete stubs that throw.
        // Shims return false / no-op so Debugger.IsActive, .Activate, .Deactivate work in tests.
        public static bool ALDebugger_ALActivate(Microsoft.Dynamics.Nav.Types.DataError e) => false;
        public static bool ALDebugger_ALActivate() => false;
        public static bool ALDebugger_ALDeactivate(Microsoft.Dynamics.Nav.Types.DataError e) => false;
        public static bool ALDebugger_ALDeactivate() => false;
        public static bool ALDebugger_ALIsActive() => false;
        public static bool ALDebugger_ALIsAttached() => false;
        public static void ALDebugger_CheckPermissionToDebug() { }

        // ALSession.ALStopSession — sync wrappers call ALStopSessionAsync which NREs.
        public static bool ALSession_StopSession(Microsoft.Dynamics.Nav.Types.DataError e, int sessionId) => false;
        public static bool ALSession_StopSession(Microsoft.Dynamics.Nav.Types.DataError e, int sessionId, string comment) => false;

        // ALSession.ALGetExecutionContext / ALGetModuleExecutionContext.
        // Return Normal (0) — headless runner has no install/upgrade execution context.
        public static Microsoft.Dynamics.Nav.Types.ExecutionContext ALGetExecutionContext(object session)
            => Microsoft.Dynamics.Nav.Types.ExecutionContext.Normal;
        public static Microsoft.Dynamics.Nav.Types.ExecutionContext ALGetModuleExecutionContext(object session)
            => Microsoft.Dynamics.Nav.Types.ExecutionContext.Normal;
        public static Microsoft.Dynamics.Nav.Types.ExecutionContext ALGetModuleExecutionContext(object session, int id)
            => Microsoft.Dynamics.Nav.Types.ExecutionContext.Normal;
        public static Microsoft.Dynamics.Nav.Types.ExecutionContext ALGetModuleExecutionContext(object session, System.Guid id)
            => Microsoft.Dynamics.Nav.Types.ExecutionContext.Normal;

        // ALSession.ALSendTraceTag — telemetry no-op; accepts all parameter overloads.
        public static void ALSession_SendTraceTag(object session, string tag, string category, object verbosity, string message) { }
        public static void ALSession_SendTraceTag(object session, string tag, string category, object verbosity, string message, object dataClass) { }

        // ALSessionInformation — SQL counters are 0 in a headless/skeleton run.
        public static long ALSqlRowsRead => 0L;
        public static long ALSqlStatementsExecuted => 0L;

        // ALSystemErrorHandling — GetLastErrorCallStack: return the AL call stack captured by
        // AlCallStackCapture (FCE-based), falling back to empty when no error has been raised.
        public static string ALGetLastErrorCallStack =>
            global::AlRunner.Infrastructure.AlCallStackCapture.GetCaptured() ?? string.Empty;

        // ───────────────────────────────────────────────────────────────────────
        // NavSession.Sleep — in-scope (§3.9). Inline execution model: a Sleep
        // simply pauses the current thread by `duration` ms (clamped to >= 0).
        // The real body chases skeleton-null session state and NREs.
        public static void NavSession_Sleep(int duration)
        {
            if (duration <= 0) return;
            try { System.Threading.Thread.Sleep(duration); } catch { /* ignore */ }
        }

        // ───────────────────────────────────────────────────────────────────────
        // ALSession.ALIsSessionActive — in-scope (§3.9). Inline-synchronous
        // dispatch means any session id is already completed by the time the
        // caller observes it. Faithful answer for both overloads: false.
        public static bool ALSession_ALIsSessionActive(int sessionId) => false;
        public static bool ALSession_ALIsSessionActive(
            Microsoft.Dynamics.Nav.Runtime.NavSession session, int sessionId) => false;

        // ───────────────────────────────────────────────────────────────────────
        // ALSession.ALStartSession — in-scope (§3.9). Dispatch the target
        // codeunit synchronously, assign a fresh positive session id, return true.
        // Missing codeunit (or any execution error under DataError.TrapError) → false.
        // All overloads route through the central BcRuntime helper.
        public static bool ALSession_ALStartSession(
            Microsoft.Dynamics.Nav.Types.DataError errorLevel,
            Microsoft.Dynamics.Nav.Runtime.ByRef<int> sessionId,
            int objectId)
            => global::AlRunner.BcRuntime.AlRunnerStartSession(
                errorLevel, sessionId, objectId, null, null);

        public static bool ALSession_ALStartSession(
            Microsoft.Dynamics.Nav.Types.DataError errorLevel,
            Microsoft.Dynamics.Nav.Runtime.ByRef<int> sessionId,
            int objectId,
            string companyName)
            => global::AlRunner.BcRuntime.AlRunnerStartSession(
                errorLevel, sessionId, objectId, companyName, null);

        public static bool ALSession_ALStartSession(
            Microsoft.Dynamics.Nav.Types.DataError errorLevel,
            Microsoft.Dynamics.Nav.Runtime.ByRef<int> sessionId,
            int objectId,
            Microsoft.Dynamics.Nav.Runtime.NavDuration timeout)
            => global::AlRunner.BcRuntime.AlRunnerStartSession(
                errorLevel, sessionId, objectId, null, null);

        public static bool ALSession_ALStartSession(
            Microsoft.Dynamics.Nav.Types.DataError errorLevel,
            Microsoft.Dynamics.Nav.Runtime.ByRef<int> sessionId,
            int objectId,
            Microsoft.Dynamics.Nav.Runtime.NavDuration timeout,
            string companyName)
            => global::AlRunner.BcRuntime.AlRunnerStartSession(
                errorLevel, sessionId, objectId, companyName, null);

        public static bool ALSession_ALStartSession(
            Microsoft.Dynamics.Nav.Types.DataError errorLevel,
            Microsoft.Dynamics.Nav.Runtime.ByRef<int> sessionId,
            int objectId,
            Microsoft.Dynamics.Nav.Runtime.NavDuration timeout,
            string companyName,
            Microsoft.Dynamics.Nav.Runtime.NavRecord record)
            => global::AlRunner.BcRuntime.AlRunnerStartSession(
                errorLevel, sessionId, objectId, companyName, record);

        public static bool ALSession_ALStartSession(
            Microsoft.Dynamics.Nav.Types.DataError errorLevel,
            Microsoft.Dynamics.Nav.Runtime.ByRef<int> sessionId,
            int objectId,
            string companyName,
            Microsoft.Dynamics.Nav.Runtime.NavRecord record)
            => global::AlRunner.BcRuntime.AlRunnerStartSession(
                errorLevel, sessionId, objectId, companyName, record);

        public static bool ALSession_ALStartSession(
            Microsoft.Dynamics.Nav.Types.DataError errorLevel,
            Microsoft.Dynamics.Nav.Runtime.ByRef<int> sessionId,
            int objectId,
            string companyName,
            Microsoft.Dynamics.Nav.Runtime.NavRecord record,
            Microsoft.Dynamics.Nav.Runtime.NavDuration timeout)
            => global::AlRunner.BcRuntime.AlRunnerStartSession(
                errorLevel, sessionId, objectId, companyName, record);

        // ───────────────────────────────────────────────────────────────────────
        // NavForm.Run (static, non-modal) — OOS §3.11 #ui.
        // BC emits NavForm.Run(...) (the [Obsolete] sync wrapper around RunAsync)
        // for all Page.Run call sites. JmpHook.Apply cannot reliably intercept
        // these on .NET 8 R2R (code-layout mismatch); source-level redirect is safe.
        // All overloads throw RunnerOutOfScopeException when OosHooksActive (i.e.
        // inside a test run); calls during BC SA init pass through harmlessly.
        public static void NavForm_Run(int formId)
        {
            if (global::AlRunner.BcRuntime.OosHooksActive)
                global::AlRunner.Infrastructure.RunnerScope.ThrowOutOfScope(""NavForm.RunAsync"", ""non-modal-ui"", ""ui"");
        }
        public static void NavForm_Run(int formId, Microsoft.Dynamics.Nav.Runtime.NavRecord record)
        {
            if (global::AlRunner.BcRuntime.OosHooksActive)
                global::AlRunner.Infrastructure.RunnerScope.ThrowOutOfScope(""NavForm.RunAsync"", ""non-modal-ui"", ""ui"");
        }
        public static void NavForm_Run(int formId, Microsoft.Dynamics.Nav.Runtime.NavRecord record, int fieldNo)
        {
            if (global::AlRunner.BcRuntime.OosHooksActive)
                global::AlRunner.Infrastructure.RunnerScope.ThrowOutOfScope(""NavForm.RunAsync"", ""non-modal-ui"", ""ui"");
        }
        public static void NavForm_Run(string fullName, Microsoft.Dynamics.Nav.Runtime.NavRecord record)
        {
            if (global::AlRunner.BcRuntime.OosHooksActive)
                global::AlRunner.Infrastructure.RunnerScope.ThrowOutOfScope(""NavForm.RunAsync"", ""non-modal-ui"", ""ui"");
        }
        public static void NavForm_Run(string fullName, Microsoft.Dynamics.Nav.Runtime.NavRecord record, int fieldNo)
        {
            if (global::AlRunner.BcRuntime.OosHooksActive)
                global::AlRunner.Infrastructure.RunnerScope.ThrowOutOfScope(""NavForm.RunAsync"", ""non-modal-ui"", ""ui"");
        }

        // ─── Text method polyfills ────────────────────────────────────────────────
        // AL string positions are 1-based throughout (CopyStr, StrPos, IndexOf, Substring).
        // These shims translate AL's 1-based startIndex to BCL's 0-based index (startIndex - 1).
        // count is a length, not a position, so it is forwarded unchanged.

        public static Microsoft.Dynamics.Nav.Runtime.NavText ALSubstring(string text, int startIndex)
            => new Microsoft.Dynamics.Nav.Runtime.NavText(text.Substring(startIndex - 1));

        public static Microsoft.Dynamics.Nav.Runtime.NavText ALSubstring(string text, int startIndex, int count)
            => new Microsoft.Dynamics.Nav.Runtime.NavText(text.Substring(startIndex - 1, count));

        public static int ALIndexOf(string text, string value)
            => text.IndexOf(value, global::System.StringComparison.Ordinal) + 1;

        public static int ALIndexOf(string text, string value, int startIndex)
            => text.IndexOf(value, startIndex - 1, global::System.StringComparison.Ordinal) + 1;

        public static int ALLastIndexOf(string text, string value)
            => text.LastIndexOf(value, global::System.StringComparison.Ordinal) + 1;

        public static int ALLastIndexOf(string text, string value, int startIndex)
            => text.LastIndexOf(value, startIndex - 1, global::System.StringComparison.Ordinal) + 1;

        // ALIndexOfAny: AL uses 1-based indexing (0 = not found). Convert from C# 0-based.
        // The startIndex parameter from AL is also 1-based.
        public static int ALIndexOfAny(string text, string chars)
        {
            int r = text.IndexOfAny(chars.ToCharArray());
            return r < 0 ? 0 : r + 1;
        }

        public static int ALIndexOfAny(string text, string chars, int startIndex)
        {
            int r = text.IndexOfAny(chars.ToCharArray(), startIndex - 1);
            return r < 0 ? 0 : r + 1;
        }

        public static int ALIndexOfAny(string text, Microsoft.Dynamics.Nav.Runtime.NavList<char> chars)
        {
            var arr = new char[chars.ALCount];
            for (int i = 0; i < arr.Length; i++) arr[i] = chars.ALGet(i + 1);
            int r = text.IndexOfAny(arr);
            return r < 0 ? 0 : r + 1;
        }

        public static int ALIndexOfAny(string text, Microsoft.Dynamics.Nav.Runtime.NavList<char> chars, int startIndex)
        {
            var arr = new char[chars.ALCount];
            for (int i = 0; i < arr.Length; i++) arr[i] = chars.ALGet(i + 1);
            int r = text.IndexOfAny(arr, startIndex - 1);
            return r < 0 ? 0 : r + 1;
        }

        // ALSplit: the separator is treated as a whole-string delimiter (not per-character).
        // This matches BC behaviour for Text.Split(separator) in both v27 and v28+.
        public static Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText> ALSplit(
            string text, string separators)
        {
            var parts = text.Split(new string[] { separators }, global::System.StringSplitOptions.None);
            var result = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText>.Default;
            foreach (var p in parts) result.ALAdd(new Microsoft.Dynamics.Nav.Runtime.NavText(p));
            return result;
        }

        public static Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText> ALSplit(
            string text, string[] separators)
        {
            var parts = text.Split(separators, global::System.StringSplitOptions.None);
            var result = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText>.Default;
            foreach (var p in parts) result.ALAdd(new Microsoft.Dynamics.Nav.Runtime.NavText(p));
            return result;
        }

        public static Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText> ALSplit(
            string text, Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText> separators)
        {
            var sepArr = new string[separators.ALCount];
            for (int i = 0; i < sepArr.Length; i++) sepArr[i] = separators.ALGet(i + 1);
            var parts = text.Split(sepArr, global::System.StringSplitOptions.None);
            var result = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText>.Default;
            foreach (var p in parts) result.ALAdd(new Microsoft.Dynamics.Nav.Runtime.NavText(p));
            return result;
        }

        // Text.Split(List of [Char]) — EACH CHARACTER is a separator, not the concatenation
        // of them. This mirrors BC's own body verbatim
        // (NavTextExtensions.ALSplit(string, NavList<char>) => text.Split(separators.Value.ToArray())).
        // It used to do separator.ToString() and pass the result as a single whole-string
        // delimiter, so 'a,b;c'.Split([',', ';']) looked for the two-character literal
        // comma-semicolon and returned one part instead of three.
        public static Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText> ALSplit(
            string text, Microsoft.Dynamics.Nav.Runtime.NavList<char> separators)
        {
            var result = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText>.Default;
            if (separators == null)
            {
                result.ALAdd(new Microsoft.Dynamics.Nav.Runtime.NavText(text));
                return result;
            }
            var chars = new char[separators.ALCount];
            for (int i = 0; i < chars.Length; i++) chars[i] = separators.ALGet(i + 1);
            foreach (var p in text.Split(chars))
                result.ALAdd(new Microsoft.Dynamics.Nav.Runtime.NavText(p));
            return result;
        }

        // NavList<char> (AL Text) overloads — emitted C# passes Text args as NavList<char>.
        // NOTE: Only keeping the two most common overloads; using explicit static helper to avoid overload resolution explosion in Roslyn.
        public static Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText> ALSplit(
            Microsoft.Dynamics.Nav.Runtime.NavList<char> text, string separators)
        {
            string t = text == null ? global::System.String.Empty : text.ToString();
            return ALSplit(t, separators);
        }

        public static Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText> ALSplit(
            Microsoft.Dynamics.Nav.Runtime.NavList<char> text, Microsoft.Dynamics.Nav.Runtime.NavList<char> separator)
        {
            string t = text == null ? global::System.String.Empty : text.ToString();
            string sep = separator == null ? global::System.String.Empty : separator.ToString();
            return ALSplit(t, sep);
        }

        // ALMaxStrLen: unlimited Text/Code returns Int32.MaxValue; bounded returns the
        // declared length. NavDefinedLengthMetadata stores 0 for unlimited and N for Text[N].
        public static int ALMaxStrLen(Microsoft.Dynamics.Nav.Runtime.NavText text)
            => text.NavDefinedLengthMetadata == 0 ? int.MaxValue : text.NavDefinedLengthMetadata;

        public static int ALMaxStrLen(Microsoft.Dynamics.Nav.Runtime.NavCode text)
            => text.NavDefinedLengthMetadata == 0 ? int.MaxValue : text.NavDefinedLengthMetadata;

        public static int ALMaxStrLen(string text)
            => int.MaxValue; // unlimited Text passed as raw string

        // NavApp.GetCurrentModuleInfo — module info of the EXECUTING app. This polyfill
        // class is compiled into each emitted assembly (bundle emit + every dep emit),
        // so GetExecutingAssembly() here IS the module whose AL code made the call —
        // BcRuntime maps it to that app's identity (real BC's executing-module rule;
        // a dependency like SPBLIC must see its own name/version, not the bundle's).
        public static void ALNavApp_GetCurrentModuleInfo(
            Microsoft.Dynamics.Nav.Types.DataError errorLevel,
            Microsoft.Dynamics.Nav.Runtime.ByRef<Microsoft.Dynamics.Nav.Runtime.NavModuleInfo> info)
        {
            var (appId, name, publisher, version) = global::AlRunner.BcRuntime.GetModuleAppInfoFor(
                global::System.Reflection.Assembly.GetExecutingAssembly());
            var navVersion = new Microsoft.Dynamics.Nav.Runtime.NavVersion(version);
            var emptyDeps = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavModuleDependencyInfo>.Default;
            info.Value = new Microsoft.Dynamics.Nav.Runtime.NavModuleInfo(
                appId, name, publisher, navVersion, navVersion, emptyDeps, appId);
        }

        // NavApp.GetModuleInfo(errorLevel, moduleId, info) — resolves any REGISTERED
        // module (bundle + every loaded dependency assembly) by AppId; unknown ids
        // return false (callers that pass errorLevel.Throw and want a strict miss can
        // still distinguish by checking the bool return).
        public static bool ALNavApp_GetModuleInfo(
            Microsoft.Dynamics.Nav.Types.DataError errorLevel,
            global::System.Guid moduleId,
            Microsoft.Dynamics.Nav.Runtime.ByRef<Microsoft.Dynamics.Nav.Runtime.NavModuleInfo> info)
        {
            var found = global::AlRunner.BcRuntime.TryGetModuleInfoByAppId(moduleId);
            if (found == null) return false;
            var (appId, name, publisher, version) = found.Value;
            var navVersion = new Microsoft.Dynamics.Nav.Runtime.NavVersion(version);
            var emptyDeps = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavModuleDependencyInfo>.Default;
            info.Value = new Microsoft.Dynamics.Nav.Runtime.NavModuleInfo(
                appId, name, publisher, navVersion, navVersion, emptyDeps, appId);
            return true;
        }

        // NavApp.GetCallerModuleInfo — the module that CALLED into the executing app
        // (nearest stack frame from a different registered AL assembly); falls back to
        // the executing app itself when no cross-module frame exists.
        public static bool ALNavApp_GetCallerModuleInfo(
            Microsoft.Dynamics.Nav.Types.DataError errorLevel,
            Microsoft.Dynamics.Nav.Runtime.ByRef<Microsoft.Dynamics.Nav.Runtime.NavModuleInfo> info)
        {
            var (appId, name, publisher, version) = global::AlRunner.BcRuntime.GetCallerModuleAppInfoFor(
                global::System.Reflection.Assembly.GetExecutingAssembly());
            var navVersion = new Microsoft.Dynamics.Nav.Runtime.NavVersion(version);
            var emptyDeps = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavModuleDependencyInfo>.Default;
            info.Value = new Microsoft.Dynamics.Nav.Runtime.NavModuleInfo(
                appId, name, publisher, navVersion, navVersion, emptyDeps, appId);
            return true;
        }

        public static bool ALDatabase_ALLockTimeout { get; set; }
        public static int ALDatabase_ALLockTimeoutDuration { get; set; }

        public static string ALGetDefaultTableConnection(Microsoft.Dynamics.Nav.Types.TableConnectionType type)
            => string.Empty;

        public static void ALRegisterTableConnection(
            Microsoft.Dynamics.Nav.Types.CompilationTarget target,
            Microsoft.Dynamics.Nav.Types.TableConnectionType type,
            string name,
            string connectionString)
            => throw new global::System.InvalidOperationException(
                ""You do not have permission to register table connections in the in-process runner."");

        public static void ALRegisterTableConnection(
            Microsoft.Dynamics.Nav.Types.TableConnectionType type,
            string name,
            string connectionString)
            => throw new global::System.InvalidOperationException(
                ""You do not have permission to register table connections in the in-process runner."");

        // ─── Text function polyfills ──────────────────────────────────────────────────

        // CopyStr: both v27 and v28 throw when fromPos < 1.
        public static string ALCopyStr(string source, int fromPos1Based)
        {
            if (fromPos1Based < 1)
                throw new global::System.ArgumentOutOfRangeException(
                    nameof(fromPos1Based),
                    ""Position is outside of the permitted range of the input string."");
            if (source == null) return global::System.String.Empty;
            return Microsoft.Dynamics.Nav.Runtime.ALSystemString.ALCopyStr(source, fromPos1Based);
        }
        public static string ALCopyStr(string source, int fromPos1Based, int length)
        {
            if (fromPos1Based < 1)
                throw new global::System.ArgumentOutOfRangeException(
                    nameof(fromPos1Based),
                    ""Position is outside of the permitted range of the input string."");
            if (source == null) return global::System.String.Empty;
            return Microsoft.Dynamics.Nav.Runtime.ALSystemString.ALCopyStr(source, fromPos1Based, length);
        }
        public static string ALCopyStr(Microsoft.Dynamics.Nav.Runtime.NavList<char> source, int fromPos1Based)
            => ALCopyStr(source == null ? null : source.ToString(), fromPos1Based);
        public static string ALCopyStr(Microsoft.Dynamics.Nav.Runtime.NavList<char> source, int fromPos1Based, int length)
            => ALCopyStr(source == null ? null : source.ToString(), fromPos1Based, length);

        // IncStr: both v27 and v28 return "" for non-numeric strings.
        public static string ALIncStr(string value)
        {
            if (value == null) return global::System.String.Empty;
            bool hasDigit = false;
            foreach (char c in value) if (char.IsDigit(c)) { hasDigit = true; break; }
            if (!hasDigit) return global::System.String.Empty;
            return Microsoft.Dynamics.Nav.Runtime.ALSystemString.ALIncStr(value);
        }
        public static string ALIncStr(string value, long increment)
        {
            if (value == null) return global::System.String.Empty;
            bool hasDigit = false;
            foreach (char c in value) if (char.IsDigit(c)) { hasDigit = true; break; }
            if (!hasDigit) return global::System.String.Empty;
            return Microsoft.Dynamics.Nav.Runtime.ALSystemString.ALIncStr(value, increment);
        }
        public static string ALIncStr(Microsoft.Dynamics.Nav.Runtime.NavList<char> value)
            => ALIncStr(value == null ? null : value.ToString());
        public static string ALIncStr(Microsoft.Dynamics.Nav.Runtime.NavList<char> value, long increment)
            => ALIncStr(value == null ? null : value.ToString(), increment);

        // SelectStr: both v27 and v28 throw for index 0 or index > count.
        public static string ALSelectString(int index1Based, string source)
            => Microsoft.Dynamics.Nav.Runtime.ALSystemString.ALSelectString(index1Based, source);
        public static string ALSelectString(int index1Based, Microsoft.Dynamics.Nav.Runtime.NavList<char> source)
        {
            string s = source == null ? global::System.String.Empty : source.ToString();
            return Microsoft.Dynamics.Nav.Runtime.ALSystemString.ALSelectString(index1Based, s);
        }

        // StrPos: delegates to the original BC runtime behaviour.
        // Both v27 and v28+ return 0 when the substring is empty (""not found"").
        public static int ALStrPos(string source, string substring)
        {
            return Microsoft.Dynamics.Nav.Runtime.ALSystemString.ALStrPos(source, substring);
        }
        public static int ALStrPos(Microsoft.Dynamics.Nav.Runtime.NavList<char> source, string substring)
        {
            string s = source == null ? global::System.String.Empty : source.ToString();
            return Microsoft.Dynamics.Nav.Runtime.ALSystemString.ALStrPos(s, substring);
        }
        public static int ALStrPos(string source, Microsoft.Dynamics.Nav.Runtime.NavList<char> substring)
        {
            string sub = substring == null ? global::System.String.Empty : substring.ToString();
            return Microsoft.Dynamics.Nav.Runtime.ALSystemString.ALStrPos(source, sub);
        }
        public static int ALStrPos(Microsoft.Dynamics.Nav.Runtime.NavList<char> source, Microsoft.Dynamics.Nav.Runtime.NavList<char> substring)
        {
            string s = source == null ? global::System.String.Empty : source.ToString();
            string sub = substring == null ? global::System.String.Empty : substring.ToString();
            return Microsoft.Dynamics.Nav.Runtime.ALSystemString.ALStrPos(s, sub);
        }
    }
}
";
}
