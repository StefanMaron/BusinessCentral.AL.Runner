// BcAssembler — Roslyn-compiles emitted C# against real BC DLLs.
//
// Pre-compile passes:
//   1. ApplyPolyfillRedirects — string substitutions routing AL-compiler-emitted
//      references for APIs that don't exist on the real service-tier DLLs to
//      small in-process polyfill shims (defined inline as PolyfillSource).
//
// Post-compile pass (runs only when the real emit needs it):
//   2. CallSiteArgWrap — fixes the residual call-site ByRef gap BC's emitter
//      doesn't cover (e.g. `dict.ALGet(K, fieldOfHandleT)` → wraps the field arg
//      as `new ByRef<T>(() => expr, v => expr = v)`). BC's emitter handles
//      parameter-declaration ByRef wraps natively at codeanalysis.cs:342854 —
//      no syntax rewriter needed for those. Runs only when an emit reports the
//      gap (see #2590), so a module without one never pays for it.
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

    /// <summary>
    /// Parse options for BC-generated C#. <c>CSharpParseOptions.Default</c> carries
    /// <c>DocumentationMode.Parse</c>, so the lexer builds structured XML-doc trivia for every
    /// <c>///</c> comment. BC's emitter writes none, and nothing downstream reads doc comments,
    /// so both the scan and the trivia nodes it would allocate are pure overhead.
    /// </summary>
    /// <remarks>
    /// The language version is pinned rather than left at <see cref="LanguageVersion.Default"/>,
    /// which resolves to whatever the referenced Roslyn package's newest major happens to be — so
    /// a routine package bump silently changes the language BC's generated C# is parsed as. That
    /// is not hypothetical: C# 14 made <c>field</c> a contextual keyword inside property accessor
    /// bodies, which is exactly the kind of identifier an AL-to-C# emitter produces. Pinned at the
    /// version the corpus has actually been compiled under; raising it is a deliberate change that
    /// needs a corpus run behind it.
    /// </remarks>
    /// <summary>Test seam: the options every generated source is parsed with.</summary>
    internal static CSharpParseOptions GeneratedParseOptionsForTests => GeneratedParseOptions;

    private static readonly CSharpParseOptions GeneratedParseOptions =
        CSharpParseOptions.Default
            .WithDocumentationMode(DocumentationMode.None)
            .WithLanguageVersion(LanguageVersion.CSharp13);

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
        // Same BCCOMPILER_TIMING=1 switch and [emit-timing] channel BcCompiler.Emit uses, because
        // the two halves of a compile are only comparable when they are measured the same way.
        // The Roslyn half used to be one opaque number; splitting it is what showed that the
        // three costs this change addresses are 6% of it and CallSiteArgWrap's throwaway compile
        // is 47% (see #2589 and #2590 for the table).
        bool timing = Environment.GetEnvironmentVariable("BCCOMPILER_TIMING") == "1";
        var timer = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string phase)
        {
            if (timing)
                Console.Error.WriteLine(
                    $"[emit-timing] {assemblyName}: {phase}: {timer.ElapsedMilliseconds}ms "
                    + $"(heap {GC.GetTotalMemory(false) / (1024 * 1024)}MB)");
            timer.Restart();
        }

        var sourceList = sources.ToList();
        // #2967 — SCRATCH-DIR CLASSIFICATION: a DOCUMENTED TRADE-OFF, same as the
        // AL_RUNNER_DUMP_BC_ASM dump below. Off unless a developer sets DUMP_CS=1, and a
        // predictable filename is the entire point of a debug dump. Two concurrent runs with
        // the flag set overwrite each other; nothing reads these back, so no RESULT depends on
        // which one wins.
        if (Environment.GetEnvironmentVariable("DUMP_CS") == "1")
            foreach (var s in sourceList)
                File.WriteAllText(Path.Combine(Path.GetTempPath(), $"gen_{s.Name}.cs"), s.Code);

        // One tree per AL object, parsed in parallel. Parsing a file has no dependency on any
        // other file, and a whole-module compile has thousands of them, so this is the one pass
        // in CompileCore that fans out without changing what the compiler sees: results land in
        // a fixed array slot, so the tree ORDER handed to CSharpCompilation.Create is identical
        // to the sequential form, whatever order the workers finish in. Roslyn folds tree order
        // into member ordering and diagnostic ordering, so that is not a detail.
        var trees = new List<SyntaxTree>(ParseInParallel(sourceList))
        {
            // Helpers for runtime-API mismatches between alc-emit and the service-tier DLLs.
            // PolyfillRedirects route callers here.
            CSharpSyntaxTree.ParseText(PolyfillSource, GeneratedParseOptions, path: "_polyfill.cs"),
        };
        Mark($"Roslyn parse {trees.Count} sources");

        var refs = SharedMetadataReferences(ReferencePaths());
        Mark($"metadata references ({refs.Count})");

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true,
            concurrentBuild: false,
            checkOverflow: true,
            optimizationLevel: OptimizationLevel.Release);

        // Emit first, then fill BC's call-site ByRef gap only when the emit actually
        // reports one — see CallSiteArgWrap's header for why this pass is no longer
        // speculative (#2590). Bounded at 6 emits total (an initial attempt plus up to 5
        // rewrite-and-retry cycles), mirroring the round budget the old speculative
        // diagnose-and-rewrite loop used before every real compile.
        const int MaxAttempts = 6;
        byte[]? bytes = null;
        IReadOnlyList<string> errors = Array.Empty<string>();
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var compilation = CSharpCompilation.Create(assemblyName, trees, refs, options);
            using var ms = new MemoryStream();
            var emit = compilation.Emit(ms);
            if (emit.Success)
            {
                bytes = ms.ToArray();
                errors = Array.Empty<string>();
                break;
            }

            errors = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();

            if (attempt == MaxAttempts - 1) break;
            var rewritten = CallSiteArgWrap.TryRewrite(trees, emit.Diagnostics);
            // Null means the failure is NOT a ByRef gap — a genuine compile error, which
            // must be reported as itself rather than retried into a misleading message.
            if (rewritten == null) break;
            trees = rewritten.ToList();
        }
        Mark("Roslyn bind + IL gen (+ CallSiteArgWrap on demand)");
        if (bytes == null)
            return new CompileResult(null, errors);
        if (Environment.GetEnvironmentVariable("AL_RUNNER_DUMP_BC_ASM") == "1")
        {
            try
            {
                // #2967: documented trade-off — see the DUMP_CS note at the top of this method.
                var dumpPath = Path.Combine(Path.GetTempPath(), assemblyName + ".dll");
                File.WriteAllBytes(dumpPath, bytes);
                Console.Error.WriteLine($"[BcAssembler] dumped {assemblyName} → {dumpPath}");
            }
            catch { /* best-effort */ }
        }
        return new CompileResult(bytes, Array.Empty<string>());
    }

    /// <summary>
    /// Parses every source into its own syntax tree, applying the polyfill redirects first, with
    /// the work spread over at most <see cref="Environment.ProcessorCount"/> threads.
    /// </summary>
    /// <remarks>
    /// Deliberately dedicated threads sized <see cref="CompileStackSize"/>, not a
    /// <c>Parallel.For</c> over the thread pool. <see cref="Compile"/> runs this whole method on
    /// a 64 MB-stack thread precisely because Roslyn's recursion on generated code has overflowed
    /// the default stack here before; handing the parse to pool threads would put it back on a
    /// default-sized stack and quietly give that guarantee up. The parse of one AL object is
    /// shallow enough that it has never been the overflow, but "has never been" is not the same
    /// claim as "cannot be", and a stack overflow is a SIGSEGV with no managed stack to read.
    /// </remarks>
    internal static SyntaxTree[] ParseInParallel(IReadOnlyList<EmittedSource> sources)
    {
        var parsed = new SyntaxTree[sources.Count];
        SyntaxTree ParseOne(int i) => CSharpSyntaxTree.ParseText(
            ApplyPolyfillRedirects(sources[i].Code), GeneratedParseOptions,
            path: sources[i].Name + ".cs");

        // Below this size the thread setup costs more than the parse it would overlap.
        const int ParallelThreshold = 8;
        var workers = Math.Min(Environment.ProcessorCount, sources.Count);
        if (workers <= 1 || sources.Count < ParallelThreshold)
        {
            for (var i = 0; i < sources.Count; i++) parsed[i] = ParseOne(i);
            return parsed;
        }

        var next = -1;
        Exception? failure = null;
        var threads = new Thread[workers];
        for (var w = 0; w < workers; w++)
        {
            threads[w] = new Thread(() =>
            {
                try
                {
                    int i;
                    while ((i = Interlocked.Increment(ref next)) < sources.Count)
                        parsed[i] = ParseOne(i);
                }
                catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
            }, CompileStackSize);
            threads[w].Start();
        }
        foreach (var t in threads) t.Join();
        if (failure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return parsed;
    }

    /// <summary>
    /// One <see cref="MetadataReference"/> per (path, last-write, length), shared by every
    /// compile in the process.
    ///
    /// <para><see cref="MetadataReference.CreateFromFile(string, MetadataReferenceProperties, DocumentationProvider)"/>
    /// reads and indexes the whole PE metadata of the file it is given, and this list is ~195
    /// unchanging assemblies: every BC service-tier DLL plus the .NET shared framework. Recreating
    /// them per app group meant a bundle of N app groups paid it N times, and a <c>--watch</c>
    /// session paid it again on every cycle for files that cannot have moved. Roslyn is designed
    /// for these to be shared — a MetadataReference is immutable and its underlying metadata is
    /// reference-counted — so caching also keeps one copy of that metadata in memory instead of
    /// one per live compilation.</para>
    ///
    /// <para>Keyed on the file's stamp as well as its path, never the path alone: a
    /// <c>--bc-version</c> switch points the same path at different bytes, and serving the old
    /// metadata would compile AL against a version no longer on disk.</para>
    ///
    /// <para>One path is deliberately NOT served from here: the runner's own assembly, which is
    /// in the list too. It is held for the process instead (<see cref="RunnerAssemblyReference"/>)
    /// — a stamp key would make it re-read per compile, and the loaded assembly is what the
    /// compiled AL binds against at run time anyway, so a newer file on disk would be skew
    /// rather than accuracy. #2880.</para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (string Path, long Ticks, long Length), MetadataReference> _metadataReferenceCache = new();

    internal static List<MetadataReference> SharedMetadataReferences(IEnumerable<string> paths)
    {
        var runnerDll = typeof(BcAssembler).Assembly.Location;
        var refs = new List<MetadataReference>();
        foreach (var path in paths)
        {
            // #2880: the one mandatory reference is served from the held instance rather than
            // re-stamped and re-read, so a moment in which the file is unreadable — the shape
            // #2880 points at, MSBuild's delete-then-write during a concurrent build — cannot
            // drop it from a compile that happens after it resolved once.
            if (!string.IsNullOrEmpty(runnerDll) && string.Equals(path, runnerDll, StringComparison.Ordinal))
            {
                refs.Add(RunnerAssemblyReference);
                continue;
            }
            (string Path, long Ticks, long Length) key;
            try
            {
                var info = new FileInfo(path);
                key = (path, info.LastWriteTimeUtc.Ticks, info.Length);
            }
            catch (IOException)
            {
                // No readable stamp — take the uncached path rather than key on a guess.
                refs.Add(MetadataReference.CreateFromFile(path));
                continue;
            }
            refs.Add(_metadataReferenceCache.GetOrAdd(
                key, static k => MetadataReference.CreateFromFile(k.Path)));
        }
        return refs;
    }

    /// <summary>Test seam: the reference list a compile of this assembler would use.</summary>
    internal IEnumerable<string> ReferencePathsForTests() => ReferencePaths();

    /// <summary>Test seam: the polyfill source Roslyn parses as <c>_polyfill.cs</c>.</summary>
    internal static string PolyfillSourceForTests => PolyfillSource;

    /// <summary>
    /// The runner's own assembly, resolved ONCE and held for the life of the process (#2880).
    ///
    /// <para><see cref="PolyfillSource"/> references <c>global::AlRunner</c> unconditionally, so
    /// this reference is not optional the way the BC service-tier DLLs above it are — a
    /// compilation without it cannot succeed. It used to be re-resolved per compile, through a
    /// <c>File.Exists</c> guard that SILENTLY dropped it when the file was not there. The
    /// resulting compile failed 24 diagnostics later with
    /// <c>_polyfill.cs(31,24): error CS0400: The type or namespace name 'AlRunner' could not be
    /// found</c> and a cascade of CS8130 deconstruction-inference errors — an error list that
    /// names a generated file the reader has never seen and a namespace, and says nothing about
    /// a missing reference.</para>
    ///
    /// <para>Holding it NARROWS the window to first resolution — it does not remove it. Once
    /// resolved, the metadata is fixed for the process whatever happens to the file on disk
    /// afterwards, and the loaded assembly is the one the compiled AL will bind against at run
    /// time anyway, so re-reading the path per compile could only introduce absence or skew,
    /// never accuracy. A transient that lands BEFORE the first compile still faults that
    /// compile — loudly, by the throw below, which is the point. It is retryable rather than
    /// terminal because the holder uses <c>PublicationOnly</c>; the default
    /// <c>ExecutionAndPublication</c> would cache the exception and rethrow it on every later
    /// compile even after the file returned. (The mtime-keyed
    /// <see cref="_metadataReferenceCache"/> stays as it is for every OTHER path: those really
    /// can point at different bytes after a <c>--bc-version</c> switch.)</para>
    /// </summary>
    internal static MetadataReference RunnerAssemblyReference => _runnerAssemblyReference.Value;

    private static readonly Lazy<MetadataReference> _runnerAssemblyReference =
        NewRunnerAssemblyReferenceHolder(() =>
            MetadataReference.CreateFromFile(ResolveRunnerAssemblyPath(
                typeof(BcAssembler).Assembly.Location, File.Exists)));

    // PublicationOnly, not the Lazy<T> default. ExecutionAndPublication CACHES the factory's
    // exception, so a single unreadable moment at the first compile of the process would make
    // every later compile rethrow it for as long as the process lived — turning exactly the
    // momentary condition #2880 points at into a permanent one, which is the opposite of what
    // holding the reference is for. PublicationOnly retries on the next ask. It can run the
    // factory concurrently and discard the losers; MetadataReference.CreateFromFile is safe to
    // call that way, and a discarded duplicate costs one extra read of a file already in the
    // page cache.
    private static Lazy<MetadataReference> NewRunnerAssemblyReferenceHolder(
        Func<MetadataReference> factory)
        => new(factory, System.Threading.LazyThreadSafetyMode.PublicationOnly);

    /// <summary>Test seam for <see cref="NewRunnerAssemblyReferenceHolder"/>.</summary>
    internal static Lazy<MetadataReference> NewRunnerAssemblyReferenceHolderForTests(
        Func<MetadataReference> factory) => NewRunnerAssemblyReferenceHolder(factory);

    /// <summary>Test seam for <see cref="ResolveRunnerAssemblyPath"/>.</summary>
    internal static string ResolveRunnerAssemblyPathForTests(string location, Func<string, bool> exists)
        => ResolveRunnerAssemblyPath(location, exists);

    /// <summary>
    /// Where the runner's own assembly is on disk, or a loud failure naming what it would have
    /// broken. Never returns null: an unresolvable mandatory reference is not a condition to
    /// carry on from (.claude/rules/loud-failures.md), and the 24-diagnostic cascade it used to
    /// produce is exactly the "silent out-of-scope failure" that rule exists to prevent.
    /// </summary>
    private static string ResolveRunnerAssemblyPath(string location, Func<string, bool> exists)
    {
        if (!string.IsNullOrEmpty(location) && exists(location)) return location;
        throw new InvalidOperationException(
            "AlRunner's own assembly could not be resolved as a compile reference"
            + (string.IsNullOrEmpty(location) ? " (Assembly.Location was empty)" : $": {location}")
            + ". _polyfill.cs references global::AlRunner unconditionally, so every emitted "
            + "module would fail to compile with CS0400 'AlRunner could not be found' and a "
            + "cascade of CS8130 deconstruction errors that name neither this assembly nor the "
            + "real cause. See issue #2880.");
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
        //
        // #2880: this used to be `if (!IsNullOrEmpty && File.Exists) yield return`, matching the
        // BC service-tier entries above. That guard is right for THEM — a BC version that does
        // not ship Types.Report.Runtime.dll must still compile — and wrong for this one, which
        // no compilation can succeed without. See RunnerAssemblyReference.
        yield return ResolveRunnerAssemblyPath(typeof(BcAssembler).Assembly.Location, File.Exists);
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
        // ALSession.ALGetExecutionContext / ALGetModuleExecutionContext used to be redirected here
        // to a shim that always answered ExecutionContext.Normal. Removed with AlRunner#2353:
        // they NRE'd only because NavDatabase.Tenant was null on the skeleton, and that is now
        // populated (RecordPatches.Register), so BC's own bodies run and compute the answer —
        // including Install / Uninstall / Upgrade from session.AppInstallationContext and
        // session.AppUpgradeContext, which a hardcoded Normal got wrong inside an install trigger.
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
        // ALDatabase.ALGetDefaultTableConnection / ALRegisterTableConnection used to be shimmed
        // here ("" and an untyped "no permission" throw). Both now run BC's own bodies against
        // the skeleton session's real TableConnectionManager — see
        // AlRunner/Patches/TableConnectionPatches.cs (#2725).
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
        // NavForm.Run (static, non-modal) — Page.Run.
        // BC emits NavForm.Run(...) (the [Obsolete] sync wrapper around RunAsync)
        // for all Page.Run call sites. JmpHook.Apply cannot reliably intercept
        // these on .NET 8 R2R (code-layout mismatch); source-level redirect is safe.
        //
        // These forward to BC's own RunAsync, which asks NavTestExecution.TestHandleForm
        // first: in a test session that dispatches the page to the test's TestPage.Trap()
        // or its [PageHandler], with no client involved, and refuses with BC's own
        // ""Unhandled UI"" error when the test declared neither. Outside a test session
        // there is genuinely no client and BC's own client-callback refusal stands.
        // They used to throw out-of-scope §3.11 unconditionally, which made the SAME AL
        // behave differently depending on whether it was compiled here or arrived in a
        // precompiled Base App DLL (which never went through this redirect). See #2349.
        //
        // Named RunAsync rather than Run so the _polyfillRedirects rewrite of the literal
        // ""NavForm.Run("" cannot match these bodies and send them back to themselves.
        public static void NavForm_Run(int formId)
            => Microsoft.Dynamics.Nav.Runtime.NavForm.RunAsync(formId).AsTask().GetAwaiter().GetResult();
        public static void NavForm_Run(int formId, Microsoft.Dynamics.Nav.Runtime.NavRecord record)
            => Microsoft.Dynamics.Nav.Runtime.NavForm.RunAsync(formId, record).AsTask().GetAwaiter().GetResult();
        public static void NavForm_Run(int formId, Microsoft.Dynamics.Nav.Runtime.NavRecord record, int fieldNo)
            => Microsoft.Dynamics.Nav.Runtime.NavForm.RunAsync(formId, record, fieldNo).AsTask().GetAwaiter().GetResult();
        public static void NavForm_Run(string fullName, Microsoft.Dynamics.Nav.Runtime.NavRecord record)
            => Microsoft.Dynamics.Nav.Runtime.NavForm.RunAsync(fullName, record).AsTask().GetAwaiter().GetResult();
        public static void NavForm_Run(string fullName, Microsoft.Dynamics.Nav.Runtime.NavRecord record, int fieldNo)
            => Microsoft.Dynamics.Nav.Runtime.NavForm.RunAsync(fullName, record, fieldNo).AsTask().GetAwaiter().GetResult();

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
        // Returns bool (#1942): AL declares this Boolean-valued
        // (`NavApp.GetCurrentModuleInfo(var ModuleInfo): Boolean`), and BC's own emitted
        // C# treats the call as boolean-valued (`!ALNavApp.ALGetCurrentModuleInfo(...)`),
        // so a void polyfill fails Roslyn compile with CS0023 the instant a caller uses
        // the return value. The executing assembly is always registered and resolvable
        // here, so `true` is the faithful answer every time this runs — mirrors the
        // Cecil-side patch for the same BC method (NavAppModuleInfoPatches.cs) and the
        // sibling source polyfill ALNavApp_GetCallerModuleInfo below.
        public static bool ALNavApp_GetCurrentModuleInfo(
            Microsoft.Dynamics.Nav.Types.DataError errorLevel,
            Microsoft.Dynamics.Nav.Runtime.ByRef<Microsoft.Dynamics.Nav.Runtime.NavModuleInfo> info)
        {
            var (appId, name, publisher, version) = global::AlRunner.BcRuntime.GetModuleAppInfoFor(
                global::System.Reflection.Assembly.GetExecutingAssembly());
            var navVersion = new Microsoft.Dynamics.Nav.Runtime.NavVersion(version);
            var emptyDeps = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavModuleDependencyInfo>.Default;
            info.Value = new Microsoft.Dynamics.Nav.Runtime.NavModuleInfo(
                appId, name, publisher, navVersion, navVersion, emptyDeps, appId);
            return true;
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
