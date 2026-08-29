// AlCoverageTracker — the runtime side of --coverage (issue #1922, first slice of the
// #1640 umbrella). Records a hit per (scope type, AL statement index) via a Cecil-rewrite
// hook on Microsoft.Dynamics.Nav.Ncl.dll's NavMethodScope.StmtHit(int) — see
// NclCecilRewrite.RewriteStmtHit — and turns the result into a Cobertura XML report.
//
// StmtHit already maintains NavMethodScope.StatementNumber (decompiled and confirmed;
// see the #1922 investigation notes), which AlCallStackCapture depends on for AL
// stack-trace "line L". The Cecil rewrite PREPENDS the hook call before StmtHit's
// existing body — it does not replace or touch that assignment — so stack traces are
// unaffected whether or not --coverage is passed.
//
// Counters are only recorded when Enabled is set (by --coverage); the hook call itself
// is unconditional in the rewritten IL (so the cached, rewritten Ncl.dll is identical
// whether or not a given run passes --coverage), but OnStmtHit no-ops immediately when
// Enabled is false. Observable behaviour on the default path — test results, timing,
// output — is therefore unchanged.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

/// <summary>One AL statement's coverage record, resolved to its AL source location.</summary>
public readonly record struct AlCoverageStatement(
    string ObjectLabel, int ObjectId, string FilePath, int Line, int HitCount);

public static class AlCoverageTracker
{
    /// <summary>True only while a --coverage run is executing tests. Gates OnStmtHit;
    /// the Cecil-rewritten StmtHit call is unconditional, this flag is not.</summary>
    public static volatile bool Enabled;

    /// <summary>
    /// True only while a `perTestCoverage:true` request (#2135) is executing tests —
    /// a SEPARATE flag from <see cref="Enabled"/> so a plain `coverage:true` request
    /// (the aggregate, whole-run table) never pays for per-test bucketing it did not
    /// ask for, and vice versa: `perTestCoverage:true` alone works without also
    /// setting `coverage:true`. Gates the SECOND write in <see cref="OnStmtHit"/> —
    /// same "volatile bool check on the hot path, real work only when set" shape
    /// <see cref="Enabled"/> and <see cref="AlValueCapture"/>.Enabled already use.
    /// </summary>
    public static volatile bool PerTestEnabled;

    // Set by TestExecutor.RunOne (and Program.cs's RunFirstCodeunitOnRun for the
    // `execute` single-codeunit path) around a test's own invocation window — see
    // BeginTest's doc comment. Single process-global slot, not per-thread: the SAME
    // "the runner invokes exactly one test body at a time" assumption
    // AlCurrentStatement's single slot already documents (InvokeWithTimeout hands the
    // AL body to a fresh Thread each test, but only ONE such thread is ever alive at
    // once — the caller Join()s, with a timeout, before starting the next).
    private static volatile string? _currentTestKey;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type ScopeType, int Stmt), int> _hits = new();

    // Per-test hit buckets (#2135) — one inner dictionary per test key, populated
    // ONLY while PerTestEnabled is true. Keyed by the SAME "{Codeunit}.{Method}"
    // string TestEvent/ToWire(TestResult) already put on the wire as `name`, so a
    // caller can join this back to a specific test with no separate id mapping.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, System.Collections.Concurrent.ConcurrentDictionary<(Type ScopeType, int Stmt), int>> _perTestHits = new();

    /// <summary>Reset between coverage collections (tests). Exposed for test isolation.</summary>
    public static void Reset() => _hits.Clear();

    /// <summary>Reset between per-test coverage collections — the per-test analogue of
    /// <see cref="Reset"/>, kept SEPARATE so a caller that only wants aggregate
    /// `coverage:true` never pays to clear (or populate) this dictionary.</summary>
    public static void ResetPerTest() => _perTestHits.Clear();

    /// <summary>
    /// Marks the start of one test's execution window for per-test attribution
    /// (#2135). Called UNCONDITIONALLY from TestExecutor.RunOne / Program.cs's
    /// RunFirstCodeunitOnRun — same "always call, cheap even when the feature is
    /// off" pattern AlCallStackCapture.Clear() already uses — so neither caller needs
    /// to know whether `perTestCoverage:true` was actually requested; the cost of
    /// NOT requesting it is a single volatile write here plus one unread volatile
    /// read per StmtHit (PerTestEnabled's own check, see OnStmtHit).
    /// </summary>
    public static void BeginTest(string testKey) => _currentTestKey = testKey;

    /// <summary>Marks the end of the current test's execution window. See <see cref="BeginTest"/>.</summary>
    public static void EndTest() => _currentTestKey = null;

    /// <summary>
    /// Hook target for the Cecil-rewritten NavMethodScope.StmtHit(int). Public static,
    /// exactly (NavMethodScope, int) so the rewrite can forward `ldarg.0; ldarg.1; call`
    /// without boxing the int. Must stay side-effect-free beyond counting: it runs on
    /// every AL statement of every test, coverage or not.
    ///
    /// Also feeds AlCurrentStatement (#2117) UNCONDITIONALLY — i.e. before the Enabled
    /// check below, not gated by it. That tracker answers "which AL statement is
    /// executing right now" for RunnerClientCallback's Message() capture, which (unlike
    /// coverage/capturedValues) has no request-side opt-in — see AlCurrentStatement's
    /// and AlMessageCapture's doc comments for why session.CurrentMethodScope could not
    /// answer that question and this hook's own scope argument can.
    ///
    /// Also feeds AlValueCapture.OnStmtHit (#2074) — the per-execution half of
    /// --capture-values, SELF-gated by AlValueCapture.Enabled (a separate flag from this
    /// class's own Enabled), so a coverage:false/captureValues:true request still gets
    /// per-statement value diffing, and a plain corpus run (neither flag set) pays only
    /// the volatile-bool check inside that method.
    /// </summary>
    public static void OnStmtHit(NavMethodScope scope, int currentStatementNumber)
    {
        AlCurrentStatement.Update(scope, currentStatementNumber);
        AlValueCapture.OnStmtHit(scope, currentStatementNumber);
        // NavMethodScope.ExitStatementNumber (int.MaxValue) is written directly by
        // Exit(), never passed to StmtHit by generated code — guarded defensively so a
        // future BC emit change can't corrupt either dictionary with a giant fake
        // index. Shared by BOTH the aggregate and per-test paths below.
        if (currentStatementNumber == int.MaxValue) return;
        if (Enabled)
            _hits.AddOrUpdate((scope.GetType(), currentStatementNumber), 1, static (_, c) => c + 1);
        // #2135: per-test attribution — a SEPARATE flag/dictionary from the aggregate
        // one above, so the two opt-ins are priced independently (see PerTestEnabled's
        // doc comment). _currentTestKey is null outside any test's window (e.g. the
        // install-trigger seed run between codeunits) — statements hit there are
        // deliberately NOT attributed to any test.
        if (PerTestEnabled)
        {
            var testKey = _currentTestKey;
            if (testKey != null)
            {
                var bucket = _perTestHits.GetOrAdd(testKey,
                    static _ => new System.Collections.Concurrent.ConcurrentDictionary<(Type, int), int>());
                bucket.AddOrUpdate((scope.GetType(), currentStatementNumber), 1, static (_, c) => c + 1);
            }
        }
    }

    /// <summary>Hit count recorded for one (scope type, statement index). 0 if never hit.</summary>
    public static int GetHitCount(Type scopeType, int stmt) =>
        _hits.TryGetValue((scopeType, stmt), out var c) ? c : 0;

    private static Type? _tSourceSpansAttr;
    private static PropertyInfo? _piEncodedSpans;

    private static void EnsureReflInit()
    {
        if (_tSourceSpansAttr != null) return;
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl")
            ?? throw new InvalidOperationException(
                "[coverage] Microsoft.Dynamics.Nav.Ncl.dll not loaded — cannot resolve SourceSpansAttribute");

        _tSourceSpansAttr = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.SourceSpansAttribute")
            ?? throw new InvalidOperationException(
                "[coverage] Microsoft.Dynamics.Nav.Runtime.SourceSpansAttribute not found in Ncl.dll — BC changed shape, do not ship silently");
        _piEncodedSpans = _tSourceSpansAttr.GetProperty("EncodedSpans", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "[coverage] SourceSpansAttribute.EncodedSpans not found — BC changed shape, do not ship silently");
        // SignatureSpanAttribute is not needed here (coverage uses absolute lines, not
        // AlCallStackCapture's signature-relative ones), but validate its presence too
        // so a BC-shape drift on either attribute fails loudly instead of only breaking
        // the other call site silently.
        _ = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.SignatureSpanAttribute")
            ?? throw new InvalidOperationException(
                "[coverage] Microsoft.Dynamics.Nav.Runtime.SignatureSpanAttribute not found in Ncl.dll — BC changed shape, do not ship silently");
    }

    /// <summary>
    /// Enumerates every AL-compiled NavMethodScope subclass currently loaded — identified
    /// by carrying BC's own [SourceSpansAttribute] (only the AL compiler emits it; Ncl's
    /// own scope classes, e.g. RootMethodScope, never do) — decodes each statement's
    /// absolute AL source line via the shared AlSourceSpanCodec, and cross-references the
    /// hit counts from OnStmtHit. Statements that never executed are included with hit
    /// count 0 because this is a reflection scan over the compiled shape, not a replay of
    /// what ran — the "did not execute" half of coverage is not vacuous.
    ///
    /// <paramref name="sourceMap"/> resolves (object label, object id) to a file path
    /// (see AlCoverageSourceMap.Build); scopes whose owning object is not in the map are
    /// skipped, e.g. framework/library assemblies outside the bundle under test.
    /// </summary>
    public static List<AlCoverageStatement> Collect(IReadOnlyDictionary<(string Label, int Id), string> sourceMap)
    {
        EnsureReflInit();
        var result = new List<AlCoverageStatement>();

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }

            foreach (var t in types)
            {
                if (Attribute.GetCustomAttribute(t, _tSourceSpansAttr!) is not object srcAttr) continue;
                if (_piEncodedSpans!.GetValue(srcAttr) is not long[] spans || spans.Length == 0) continue;

                var (label, id) = AlCallStackCapture.ParseObjectTypeAndId(t);
                if (id == 0) continue;
                if (!sourceMap.TryGetValue((label, id), out var filePath)) continue;

                // Only indices BC's compiler actually backed with a StmtHit/CStmtHit call
                // are real, coverable statements — see AlCoverageInstrumentedStatements
                // for why the raw SourceSpans array is not that set on its own (it
                // carries a trailing, never-instrumented sentinel entry).
                var instrumented = AlCoverageInstrumentedStatements.Find(t);
                foreach (var i in instrumented)
                {
                    if (i < 0 || i >= spans.Length) continue; // defensive: BC shape drift
                    int line = AlSourceSpanCodec.AbsoluteFromLine(spans[i]);
                    result.Add(new AlCoverageStatement(label, id, filePath, line, GetHitCount(t, i)));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// One AL statement's full identity + hit count for the statement-position table
    /// (issue #2042): the SAME id-space <see cref="AlValueCapture"/>'s
    /// <c>AlCapturedValue.StatementId</c> uses (both read straight off
    /// NavMethodScope.StatementNumber / the StmtHit(N) argument for THIS scope type —
    /// verified in AlStatementTableTests, not assumed), the AL member name that owns
    /// the scope (<c>ScopeName</c>, matching <c>AlCapturedValue.ScopeName</c>), and the
    /// FULL decoded [SourceSpans] position (start AND end line/column) rather than just
    /// the start line <see cref="AlCoverageStatement"/> carries — the id↔position
    /// mapping a consumer like ALchemist needs to place a captured value in an editor
    /// instead of guessing from a covered-lines index (see the issue's linked
    /// SShadowS/ALchemist#1 reply).
    /// </summary>
    public readonly record struct AlStatementRecord(
        string FilePath, string ScopeName, int StatementId,
        int Line, int Column, int EndLine, int EndColumn, int HitCount);

    /// <summary>
    /// Distinct scope Types that have recorded at least one hit since the last
    /// <see cref="Reset"/> — i.e. scopes genuinely invoked in the CURRENT run.
    ///
    /// #2042's <see cref="CollectStatementTable"/> scans exactly this set instead of
    /// every SourceSpans-carrying type currently loaded in the process (which is what
    /// <see cref="Collect"/> does), because a warm <c>--server</c> process is not the
    /// single-generation world <c>Collect</c> was built for: <c>RunBundleForServer</c>
    /// calls <c>Assembly.Load(assemblyBytes)</c> again on EVERY request that isn't a
    /// cross-bundle-dedup reuse — including a pure AL-output cache HIT with
    /// byte-identical content — so re-running the SAME bundle N times against one warm
    /// server leaves N distinct Assembly generations resident (assemblies are never
    /// unloaded). Scanning "every loaded assembly" after <see cref="Reset"/> then
    /// reports the SAME AL statement once per generation: the CURRENT generation with
    /// its real hit count, plus one ghost entry per STALE generation showing 0 (its
    /// Type is still reflectable; Reset() only cleared the dictionary, not the type
    /// itself) — reproduced empirically by sending an identical `coverage:true`
    /// `runTests` request twice to one warm server and observing duplicate
    /// {id, line} entries, one live and one phantom-zero, per statement. Restricting
    /// to _hits' own keys sidesteps this entirely: a stale generation's Type recorded
    /// zero hits THIS run (Reset() cleared it and nothing in this run touched it), so
    /// it is simply absent from the key set — no "which generation is live" logic
    /// needed. <see cref="Collect"/> (--coverage, CLI-only) does not need this fix: a
    /// CLI invocation is one short-lived process, so exactly one generation ever
    /// exists there.
    /// </summary>
    private static IReadOnlyCollection<Type> GetHitTrackedTypes() =>
        _hits.Keys.Select(k => k.ScopeType).Distinct().ToArray();

    /// <summary>
    /// Same idea as <see cref="Collect"/> (cross-reference SourceSpans-carrying scope
    /// types against OnStmtHit's hit counts), but scoped to <see
    /// cref="GetHitTrackedTypes"/> instead of every loaded assembly (see that method's
    /// doc comment for why), and keeping each statement separate — never summed by
    /// line — while carrying the scope name plus the full decoded span instead of
    /// collapsing to (object, line, hits). Two statements sharing a line get two
    /// entries here with the SAME line but different id/column, which is exactly the
    /// distinction <see cref="AlCoverageReport"/>'s line-rollup necessarily discards.
    /// </summary>
    public static List<AlStatementRecord> CollectStatementTable(IReadOnlyDictionary<(string Label, int Id), string> sourceMap)
    {
        EnsureReflInit();
        AlNavNameReflection.EnsureInit();
        var result = new List<AlStatementRecord>();

        foreach (var t in GetHitTrackedTypes())
        {
            // #2135: resolution chain shared with CollectPerTestStatementTable via
            // ResolveScopeInfo — see that method's doc comment for why this used to
            // be inlined here twice (once per caller) and no longer is.
            if (ResolveScopeInfo(t, sourceMap) is not { } resolved) continue;

            var instrumented = AlCoverageInstrumentedStatements.Find(t);
            foreach (var i in instrumented)
            {
                if (i < 0 || i >= resolved.Spans.Length) continue; // defensive: BC shape drift
                var (fromLine, fromColumn, toLine, toColumn) = AlSourceSpanCodec.Decode(resolved.Spans[i]);
                result.Add(new AlStatementRecord(
                    resolved.FilePath, resolved.ScopeName, i,
                    fromLine + 1, fromColumn + 1, toLine + 1, toColumn + 1,
                    GetHitCount(t, i)));
            }
        }

        return result;
    }

    // Shared by CollectStatementTable AND CollectPerTestStatementTable (#2135) —
    // originally two independent copies of this exact chain (SourceSpans attribute,
    // EncodedSpans, ParseObjectTypeAndId, the id==0 guard, the sourceMap lookup,
    // GetAlName), which is exactly the kind of duplication that drifts: a future fix
    // to how any of those five steps resolves would silently reach only whichever
    // copy got edited. Consolidated into one helper instead of leaving the aggregate
    // path's inline version as-is. [NavName] on the scope class itself is the AL
    // procedure/trigger/test method name — the SAME attribute AlValueCapture reads
    // off scope FIELDS for local names, here read off the TYPE instead (both are
    // MemberInfo — see AlNavNameReflection). Confirmed via BCCOMPILER_DUMP_CS=1:
    // `[NavName("Run")] private sealed class Run_Scope__... : NavMethodScope<...>`.
    //
    // CollectPerTestStatementTable additionally MEMOIZES this per scope Type across
    // every test whose bucket touched it — the file/scope/position identity of a
    // given (Type, statementId) pair does not vary per test, only the hit count
    // does, so re-running this chain once per (type, test) pair the way
    // CollectStatementTable's single-pass loop does (once per type, since
    // GetHitTrackedTypes() already de-duplicates) would be wasted repeat work
    // across a suite with many tests hitting the SAME codeunit. Null means "not a
    // coverable, mapped AL scope" (framework type, or owning object outside
    // sourceMap) — CollectPerTestStatementTable's memo caches null too, so a miss
    // is not re-attempted per test either.
    private static (string FilePath, string ScopeName, long[] Spans)? ResolveScopeInfo(
        Type type, IReadOnlyDictionary<(string Label, int Id), string> sourceMap)
    {
        if (Attribute.GetCustomAttribute(type, _tSourceSpansAttr!) is not object srcAttr) return null;
        if (_piEncodedSpans!.GetValue(srcAttr) is not long[] spans || spans.Length == 0) return null;
        var (label, id) = AlCallStackCapture.ParseObjectTypeAndId(type);
        if (id == 0) return null;
        if (!sourceMap.TryGetValue((label, id), out var filePath)) return null;
        var scopeName = AlNavNameReflection.GetAlName(type) ?? "?";
        return (filePath, scopeName, spans);
    }

    /// <summary>
    /// Per-test statement attribution (#2135) — full per-statement hit counts grouped
    /// by the test whose execution window recorded them, keyed by the SAME
    /// "{Codeunit}.{Method}" string TestEvent/ToWire(TestResult) already put on the
    /// wire as `name` (see BeginTest's doc comment). Only populated while
    /// <see cref="PerTestEnabled"/> was true during the run — reads
    /// <see cref="_perTestHits"/> rather than the aggregate <see cref="_hits"/>
    /// dictionary <see cref="CollectStatementTable"/> uses, so `perTestCoverage:true`
    /// works independently of `coverage:true` (and vice versa).
    ///
    /// A test with an EMPTY entry (declared but recorded zero hits — e.g. every
    /// statement it touched belonged to a scope Type outside <paramref
    /// name="sourceMap"/>, such as a framework codeunit) is OMITTED from the
    /// returned dictionary entirely, matching the "positive list of what a test
    /// touched" shape: a mutation-testing consumer wants "which tests could possibly
    /// kill this mutant", and a test that touched nothing mappable can never answer
    /// yes for any mutant — recording it as `[]` would only cost the caller a
    /// pointless lookup.
    ///
    /// This is a NARROWER membership than <see cref="CollectStatementTable"/>'s own
    /// list, deliberately: that one walks every instrumented statement (via <see
    /// cref="AlCoverageInstrumentedStatements"/>) and emits a hits:0 record for ones
    /// no test ever hit, because "instrumented but never covered" is a fact its
    /// callers need. This method never emits a hits:0 record for anything — a
    /// statement absent from a given test's list means "this test didn't execute
    /// it", not "it wasn't instrumented"; a caller that needs to tell those two
    /// apart has to cross-reference <see cref="CollectStatementTable"/>'s own
    /// output (i.e. request `coverage:true` alongside `perTestCoverage:true`).
    /// </summary>
    public static Dictionary<string, List<AlStatementRecord>> CollectPerTestStatementTable(
        IReadOnlyDictionary<(string Label, int Id), string> sourceMap)
    {
        EnsureReflInit();
        AlNavNameReflection.EnsureInit();
        var result = new Dictionary<string, List<AlStatementRecord>>();
        var typeInfo = new Dictionary<Type, (string FilePath, string ScopeName, long[] Spans)?>();

        foreach (var testEntry in _perTestHits)
        {
            List<AlStatementRecord>? list = null;
            foreach (var stmtEntry in testEntry.Value)
            {
                var type = stmtEntry.Key.ScopeType;
                if (!typeInfo.TryGetValue(type, out var info))
                {
                    info = ResolveScopeInfo(type, sourceMap);
                    typeInfo[type] = info;
                }
                if (info is not { } resolved) continue;

                var stmtId = stmtEntry.Key.Stmt;
                if (stmtId < 0 || stmtId >= resolved.Spans.Length) continue; // defensive: BC shape drift
                var (fromLine, fromColumn, toLine, toColumn) = AlSourceSpanCodec.Decode(resolved.Spans[stmtId]);
                (list ??= new List<AlStatementRecord>()).Add(new AlStatementRecord(
                    resolved.FilePath, resolved.ScopeName, stmtId,
                    fromLine + 1, fromColumn + 1, toLine + 1, toColumn + 1,
                    stmtEntry.Value));
            }
            if (list is { Count: > 0 }) result[testEntry.Key] = list;
        }

        return result;
    }
}
