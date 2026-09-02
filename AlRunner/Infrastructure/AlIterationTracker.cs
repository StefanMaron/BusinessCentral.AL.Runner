// AlIterationTracker - the NavMethodScope-facing entry point of `iterationTracking`
// (issue #2056): resolves each compiled scope class to its loop table once, forwards
// BC's StmtHit/Exit traffic and Message() calls to AlIterationSegmenter, and turns the
// result into wire-ready records (file, lines) at collection time.
//
// Wiring (no new Cecil rewrite - every signal already reaches the runner):
//   AlCoverageTracker.OnStmtHit  -> OnStmtHit(scope, n, valuesObservedNow)
//   AlValueCapture.OnExit        -> OnScopeExit(scope, finalValues)
//   AlMessageCapture.Record      -> OnMessage(message)
// All three are self-gated by Enabled, so a request that did not ask for
// iterationTracking pays one volatile-bool read per event - the same shape
// AlCoverageTracker.Enabled and AlValueCapture.Enabled already have.
//
// SCOPE RESOLUTION - a scope Type maps to (file, member) exactly the way the statement
// table does (AlCoverageTracker.TryResolveScope: [SourceSpans] on the type, object label
// + id from the type name, file via AlCoverageSourceMap, member via [NavName]), then
// AlLoopSyntaxIndex.FindSites picks that member's loops and AlLoopScopeTable.Build
// classifies the scope's instrumented ids. Memoised per Type INCLUDING misses (framework
// scopes, dependency apps outside the bundle) so the hot path stays one dictionary hit.
using System.Collections.Concurrent;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

/// <summary>One iteration on the wire: the values, Message() calls and AL lines that
/// pass produced. <c>CapturedValues</c> are the SAME records the flat per-test series
/// carries, bucketed - never a delta and never a snapshot of every local.</summary>
public sealed record AlIterationStep(
    int Iteration,
    IReadOnlyList<AlCapturedValue> CapturedValues,
    IReadOnlyList<AlCapturedMessage> Messages,
    IReadOnlyList<int> LinesExecuted);

/// <summary>One loop INSTANCE on the wire (a loop site entered once; a procedure called
/// three times yields three). <c>ParentLoopId</c>/<c>ParentIteration</c> name the loop
/// instance and iteration that were active when this one was entered - across
/// procedure calls, not merely lexical nesting. <c>IterationCount</c> is null, with
/// <c>Unsegmentable</c> saying why, when the loop was entered but its iterations cannot
/// be counted (see AlLoopModel.cs).</summary>
public sealed record AlLoopRecord(
    string LoopId,
    string ScopeName,
    string FilePath,
    int LoopLine,
    int LoopEndLine,
    string? ParentLoopId,
    int? ParentIteration,
    int? IterationCount,
    IReadOnlyList<AlIterationStep> Steps,
    string? Unsegmentable);

public static class AlIterationTracker
{
    /// <summary>True only while an `execute` request with iterationTracking:true is
    /// running. Gates every hook below; the hook calls themselves are unconditional.</summary>
    public static volatile bool Enabled;

    private sealed record ScopeLoops(AlLoopScopeTable Table, string FilePath, string ScopeName);

    private static AlLoopSyntaxIndex? _index;
    private static IReadOnlyDictionary<(string Label, int Id), string>? _sourceMap;
    private static readonly ConcurrentDictionary<Type, ScopeLoops?> _scopes = new();
    private static volatile AlIterationSegmenter? _segmenter;

    /// <summary>Installs the request's loop index and file map (HandleServerExecute) and
    /// forgets every per-Type resolution from the previous request - a re-sent bundle is
    /// a new Assembly generation with new scope Types anyway.</summary>
    public static void Configure(AlLoopSyntaxIndex index, IReadOnlyDictionary<(string Label, int Id), string> sourceMap)
    {
        _index = index;
        _sourceMap = sourceMap;
        _scopes.Clear();
    }

    /// <summary>Reset before each top-level AL invocation (RunFirstCodeunitOnRun), the
    /// same bracket AlValueCapture.Reset uses.</summary>
    public static void Reset() => _segmenter = new AlIterationSegmenter();

    /// <summary>Fed from AlCoverageTracker.OnStmtHit with the captured values THIS
    /// observation produced (AlValueCapture.OnStmtHit's return), so they can be placed in
    /// the iteration they belong to - see AlIterationSegmenter's header.</summary>
    public static void OnStmtHit(NavMethodScope scope, int currentStatementNumber, IReadOnlyList<AlCapturedValue> observed)
    {
        if (!Enabled) return;
        var seg = _segmenter;
        if (seg == null) return;
        var resolved = Resolve(scope.GetType());
        if (resolved == null) return;
        seg.OnHit(scope, resolved.Table, currentStatementNumber, observed);
    }

    /// <summary>Fed from AlValueCapture.OnExit (the Cecil-prepended NavMethodScope.Exit
    /// hook) for EVERY scope exit, with the final diffed values (empty unless
    /// captureValues is on and this is the top-level scope).</summary>
    public static void OnScopeExit(NavMethodScope scope, IReadOnlyList<AlCapturedValue> observed)
    {
        if (!Enabled) return;
        _segmenter?.OnScopeExit(scope, observed);
    }

    /// <summary>Fed from AlMessageCapture.Record at the moment Message() is called.</summary>
    public static void OnMessage(AlCapturedMessage message)
    {
        if (!Enabled) return;
        _segmenter?.OnMessage(message);
    }

    /// <summary>Everything segmented since Reset(), in loop-entry order, resolved to
    /// files and 1-based lines. Empty (never null) when nothing looped.</summary>
    public static IReadOnlyList<AlLoopRecord> Collect()
    {
        var seg = _segmenter;
        if (seg == null) return Array.Empty<AlLoopRecord>();
        var result = new List<AlLoopRecord>();
        foreach (var inst in seg.Finish())
        {
            // Resolved when the instance was pushed, so this cannot miss.
            var info = Resolve(inst.ScopeInstance.GetType())!;
            var steps = new List<AlIterationStep>(inst.Steps.Count);
            foreach (var s in inst.Steps)
            {
                var lines = s.StatementIds
                    .Select(inst.Table.LineOf)
                    .Where(l => l != null)
                    .Select(l => l!.Value)
                    .Distinct()
                    .OrderBy(l => l)
                    .ToList();
                steps.Add(new AlIterationStep(s.Iteration, s.Captures, s.Messages, lines));
            }
            result.Add(new AlLoopRecord(
                LoopId: $"L{inst.Id}",
                ScopeName: info.ScopeName,
                FilePath: info.FilePath,
                LoopLine: inst.Site.StartLine,
                LoopEndLine: inst.Site.EndLine,
                ParentLoopId: inst.ParentId is int p ? $"L{p}" : null,
                ParentIteration: inst.ParentIteration,
                IterationCount: inst.IterationCount,
                Steps: steps,
                Unsegmentable: inst.Site.Unsegmentable));
        }
        return result;
    }

    private static ScopeLoops? Resolve(Type scopeType)
    {
        if (_scopes.TryGetValue(scopeType, out var cached)) return cached;
        var resolved = ResolveUncached(scopeType);
        _scopes[scopeType] = resolved;
        return resolved;
    }

    private static ScopeLoops? ResolveUncached(Type scopeType)
    {
        var index = _index;
        var sourceMap = _sourceMap;
        if (index == null || sourceMap == null) return null;
        if (AlCoverageTracker.TryResolveScope(scopeType, sourceMap) is not { } scope) return null;

        var instrumented = AlCoverageInstrumentedStatements.Find(scopeType);
        if (instrumented.Count == 0) return null;
        // Any real statement's position identifies the member body among same-named
        // triggers; the lowest instrumented id is the body's first statement.
        int first = instrumented.Min();
        AlTextPosition? anchor = null;
        if (first >= 0 && first < scope.Spans.Length)
        {
            var (fromLine, fromColumn, _, _) = AlSourceSpanCodec.Decode(scope.Spans[first]);
            anchor = new AlTextPosition(fromLine, fromColumn);
        }
        var sites = index.FindSites(scope.FilePath, scope.ScopeName, anchor);
        if (sites == null) return null;

        var table = AlLoopScopeTable.Build(sites, scope.Spans, instrumented);
        return new ScopeLoops(table, scope.FilePath, scope.ScopeName);
    }
}
