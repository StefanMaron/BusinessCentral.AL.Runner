// NavMethodScope-facing entry point of iterationTracking (#2056). Fed by
// AlCoverageTracker.OnStmtHit, AlValueCapture.OnExit and AlMessageCapture.Record, all
// self-gated by Enabled. No new Cecil rewrite.
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

/// <summary>One iteration on the wire. CapturedValues are the flat series' records, bucketed.</summary>
public sealed record AlIterationStep(
    int Iteration,
    IReadOnlyList<AlCapturedValue> CapturedValues,
    IReadOnlyList<AlCapturedMessage> Messages,
    IReadOnlyList<int> LinesExecuted);

/// <summary>One loop instance on the wire. Parent fields name the instance and iteration active at entry, across calls. IterationCount is null when Unsegmentable says why.</summary>
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
    /// <summary>True only while an execute request with iterationTracking:true runs.</summary>
    public static volatile bool Enabled;

    private static volatile AlIterationSegmenter? _segmenter;

    /// <summary>Reset before each top-level AL invocation, like AlValueCapture.Reset.</summary>
    public static void Reset() => _segmenter = new AlIterationSegmenter();

    /// <summary>Fed with the values this observation produced, so they land in the right iteration.</summary>
    public static void OnStmtHit(NavMethodScope scope, int currentStatementNumber, IReadOnlyList<AlCapturedValue> observed)
    {
        if (!Enabled) return;
        var seg = _segmenter;
        if (seg == null) return;
        var resolved = AlScopeSyntaxResolver.Resolve(scope.GetType());
        if (resolved == null) return;
        seg.OnHit(scope, resolved.Loops, currentStatementNumber, observed);
    }

    /// <summary>Fed from AlValueCapture.OnExit for every scope exit.</summary>
    public static void OnScopeExit(NavMethodScope scope, IReadOnlyList<AlCapturedValue> observed)
    {
        if (!Enabled) return;
        _segmenter?.OnScopeExit(scope, observed);
    }

    /// <summary>Fed from AlMessageCapture.Record.</summary>
    public static void OnMessage(AlCapturedMessage message)
    {
        if (!Enabled) return;
        _segmenter?.OnMessage(message);
    }

    /// <summary>Everything segmented since Reset, in entry order, resolved to files and lines. Empty, never null.</summary>
    public static IReadOnlyList<AlLoopRecord> Collect()
    {
        var seg = _segmenter;
        if (seg == null) return Array.Empty<AlLoopRecord>();
        var result = new List<AlLoopRecord>();
        foreach (var inst in seg.Finish())
        {
            var info = AlScopeSyntaxResolver.Resolve(inst.ScopeInstance.GetType())!;
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
}
