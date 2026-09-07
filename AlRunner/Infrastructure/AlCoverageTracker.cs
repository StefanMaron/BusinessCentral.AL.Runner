// AlCoverageTracker — the runtime side of --coverage (issue #1922, first slice of the
// #1640 umbrella). Records a hit per (scope type, AL statement index) via a Cecil-rewrite
// hook on Microsoft.Dynamics.Nav.Ncl.dll's NavMethodScope.StmtHit(int) — see
// NclCecilRewrite.RewriteStmtHit — and turns the result into a Cobertura XML report.
//
// The rewrite PREPENDS the hook — it never replaces or touches StmtHit's own body, which
// maintains NavMethodScope.StatementNumber that AlCallStackCapture reads for stack-trace
// "line L". Keep it a prepend.
//
// The hook call is unconditional in the rewritten IL, so the cached Ncl.dll is identical
// whether or not a run passes --coverage; OnStmtHit no-ops immediately when Enabled is false.
// Observable behaviour on the default path is unchanged.
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
    /// Issue #2481's behavioural regression gate: incremented UNCONDITIONALLY, first
    /// thing, on every call — proving the Cecil-rewritten call site actually fires on
    /// every AL statement, independent of <see cref="Enabled"/>. Paired with <see
    /// cref="HasRecordedAnyHits"/> (which stays false whenever <see cref="Enabled"/>
    /// stays false): a plain run must show <c>CallCount &gt; 0</c> (the hook fired) AND
    /// <c>HasRecordedAnyHits == false</c> (it did no bookkeeping work) — see
    /// AlRunner.Tests/PlainRunInstrumentationGateTests.cs.
    /// </summary>
    internal static long CallCount;

    /// <summary>True once <see cref="_hits"/> has recorded at least one entry — i.e. real
    /// coverage bookkeeping actually happened. Stays false for the lifetime of a process
    /// that never sets <see cref="Enabled"/>, however many statements ran.</summary>
    internal static bool HasRecordedAnyHits => !_hits.IsEmpty;

    /// <summary>
    /// Hook target for the Cecil-rewritten NavMethodScope.StmtHit(int). Public static,
    /// exactly (NavMethodScope, int) so the rewrite can forward `ldarg.0; ldarg.1; call`
    /// without boxing the int. Must stay side-effect-free beyond counting: it runs on
    /// every AL statement of every test, coverage or not.
    ///
    /// Feeds AlCurrentStatement (#2117) UNCONDITIONALLY — before the Enabled check, not gated
    /// by it: Message() capture has no request-side opt-in.
    ///
    /// Feeds AlValueCapture.OnStmtHit (#2074), SELF-gated by AlValueCapture.Enabled — a separate
    /// flag from this class's, so captureValues works without coverage and a run with neither
    /// pays only a volatile-bool check.
    /// </summary>
    public static void OnStmtHit(NavMethodScope scope, int currentStatementNumber)
    {
        System.Threading.Interlocked.Increment(ref CallCount);
        AlCurrentStatement.Update(scope, currentStatementNumber);
        var observed = AlValueCapture.OnStmtHit(scope, currentStatementNumber);
        // NavMethodScope.ExitStatementNumber (int.MaxValue) is written directly by
        // Exit(), never passed to StmtHit by generated code — guarded defensively so a
        // future BC emit change can't corrupt either dictionary with a giant fake
        // index. Shared by BOTH the aggregate and per-test paths below.
        if (currentStatementNumber == int.MaxValue) return;
        // #2056: iteration segmentation, self-gated; fed the values this observation produced.
        if (AlIterationTracker.Enabled)
            AlIterationTracker.OnStmtHit(scope, currentStatementNumber, observed);
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
    /// <para><see cref="CollectStatementTable"/> scans this set rather than every loaded
    /// SourceSpans-carrying type (what <see cref="Collect"/> does), because a warm
    /// <c>--server</c> process holds N Assembly generations of the same bundle — assemblies are
    /// never unloaded — and a stale generation's Type is still reflectable after
    /// <see cref="Reset"/>, so the whole-process scan emits a phantom hits:0 twin of every live
    /// statement. Keying off _hits sidesteps it: a stale Type recorded nothing this run, so it
    /// is simply absent. <see cref="Collect"/> is CLI-only and single-generation (#2042).</para>
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

    // The one resolution chain, shared by CollectStatementTable and
    // CollectPerTestStatementTable (#2135) — keep it that way; it was two copies, and a fix to
    // any of its five steps reached only whichever copy got edited. [NavName] on the scope CLASS
    // is the AL procedure/trigger/test name (the same attribute AlValueCapture reads off scope
    // FIELDS for local names; both are MemberInfo — see AlNavNameReflection).
    //
    // Null means "not a coverable, mapped AL scope" — a framework type, or an owning object
    // outside sourceMap. CollectPerTestStatementTable memoizes per scope Type, nulls included,
    // because the (Type, statementId) identity does not vary per test and only the hit count does.
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

    /// <summary>ResolveScopeInfo with the reflection init done; null for a scope outside the bundle.</summary>
    internal static (string FilePath, string ScopeName, long[] Spans)? TryResolveScope(
        Type type, IReadOnlyDictionary<(string Label, int Id), string> sourceMap)
    {
        EnsureReflInit();
        AlNavNameReflection.EnsureInit();
        return ResolveScopeInfo(type, sourceMap);
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
    /// <para>A test that recorded zero mappable hits is OMITTED entirely, not returned as
    /// `[]`. This is a positive list of what each test touched, and deliberately NARROWER than
    /// <see cref="CollectStatementTable"/>, which does emit hits:0 for instrumented-but-uncovered
    /// statements. So an absent statement here means "this test did not execute it", NOT "it was
    /// not instrumented" — a caller needing to tell those apart must request `coverage:true`
    /// alongside `perTestCoverage:true`.</para>
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
