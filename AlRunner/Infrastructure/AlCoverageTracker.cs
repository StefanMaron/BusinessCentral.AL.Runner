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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type ScopeType, int Stmt), int> _hits = new();

    /// <summary>Reset between coverage collections (tests). Exposed for test isolation.</summary>
    public static void Reset() => _hits.Clear();

    /// <summary>
    /// Hook target for the Cecil-rewritten NavMethodScope.StmtHit(int). Public static,
    /// exactly (NavMethodScope, int) so the rewrite can forward `ldarg.0; ldarg.1; call`
    /// without boxing the int. Must stay side-effect-free beyond counting: it runs on
    /// every AL statement of every test, coverage or not.
    /// </summary>
    public static void OnStmtHit(NavMethodScope scope, int currentStatementNumber)
    {
        if (!Enabled) return;
        // NavMethodScope.ExitStatementNumber (int.MaxValue) is written directly by
        // Exit(), never passed to StmtHit by generated code — guarded defensively so a
        // future BC emit change can't corrupt the dictionary with a giant fake index.
        if (currentStatementNumber == int.MaxValue) return;
        _hits.AddOrUpdate((scope.GetType(), currentStatementNumber), 1, static (_, c) => c + 1);
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
            if (Attribute.GetCustomAttribute(t, _tSourceSpansAttr!) is not object srcAttr) continue;
            if (_piEncodedSpans!.GetValue(srcAttr) is not long[] spans || spans.Length == 0) continue;

            var (label, id) = AlCallStackCapture.ParseObjectTypeAndId(t);
            if (id == 0) continue;
            if (!sourceMap.TryGetValue((label, id), out var filePath)) continue;

            // [NavName] on the scope class itself is the AL procedure/trigger/test
            // method name — the SAME attribute AlValueCapture reads off scope
            // FIELDS for local names, here read off the TYPE instead (both are
            // MemberInfo — see AlNavNameReflection). Confirmed via
            // BCCOMPILER_DUMP_CS=1: `[NavName("Run")] private sealed class
            // Run_Scope__... : NavMethodScope<...>`.
            var scopeName = AlNavNameReflection.GetAlName(t) ?? "?";

            var instrumented = AlCoverageInstrumentedStatements.Find(t);
            foreach (var i in instrumented)
            {
                if (i < 0 || i >= spans.Length) continue; // defensive: BC shape drift
                var (fromLine, fromColumn, toLine, toColumn) = AlSourceSpanCodec.Decode(spans[i]);
                result.Add(new AlStatementRecord(
                    filePath, scopeName, i,
                    fromLine + 1, fromColumn + 1, toLine + 1, toColumn + 1,
                    GetHitCount(t, i)));
            }
        }

        return result;
    }
}
