// NavMethodScope-facing entry point of iterationTracking (#2056). Fed by
// AlCoverageTracker.OnStmtHit, AlValueCapture.OnExit and AlMessageCapture.Record, all
// self-gated by Enabled. No new Cecil rewrite.
//
// Records are not copied. Each loop's iterations carry only the statements and lines that
// ran; the captured values and messages stay in the flat series, and Collect returns the
// tag maps (flat index -> loop id + iteration) the wire uses to stamp them. Loop ids are
// offset per response so a top-level message tag is unambiguous across bundles.
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

/// <summary>One iteration on the wire: the AL statement ids that ran (the statement
/// table's id-space) and their 1-based lines. The values and messages it produced are
/// the flat records tagged with this loop id and iteration index.</summary>
public sealed record AlIterationRecord(int Index, IReadOnlyList<int> Statements, IReadOnlyList<int> Lines);

/// <summary>One loop instance on the wire. <c>ParentLoop</c>/<c>ParentIteration</c> name
/// the loop instance and iteration active when this one was entered, across procedure
/// calls. <c>IterationCount</c> is null (and <c>Unsegmentable</c> a code) when the loop
/// was entered but its iterations cannot be counted.</summary>
public sealed record AlLoopRecord(
    int Id,
    string ScopeName,
    string FilePath,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    int? ParentLoop,
    int? ParentIteration,
    int? IterationCount,
    AlLoopEnd ClosedBy,
    string? Unsegmentable,
    IReadOnlyList<AlIterationRecord> Iterations);

/// <summary>A bundle's segmented loops plus the tags the wire stamps onto the flat
/// series. <c>CaptureTags</c> keys the per-test capturedValues index; <c>MessageTags</c>
/// keys the response-wide messages index.</summary>
public sealed record AlIterationCollect(
    IReadOnlyList<AlLoopRecord> Loops,
    IReadOnlyDictionary<int, (int Loop, int Iteration)> CaptureTags,
    IReadOnlyDictionary<int, (int Loop, int Iteration)> MessageTags);

public static class AlIterationTracker
{
    /// <summary>True only while an execute request with iterationTracking:true runs.</summary>
    public static volatile bool Enabled;

    private static volatile AlIterationSegmenter? _segmenter;
    private static int _idBase;   // response-level loop-id offset; reset in ConfigureResponse
    private static readonly Dictionary<int, (int, int)> _responseMessageTags = new();

    /// <summary>The whole response's message tags (message index -> loop id + iteration),
    /// accumulated across bundles; the top-level `messages[]` is stamped from this.</summary>
    public static IReadOnlyDictionary<int, (int Loop, int Iteration)> ResponseMessageTags => _responseMessageTags;

    /// <summary>Reset the response-level loop-id counter and message tags (once per request).</summary>
    public static void ConfigureResponse()
    {
        _idBase = 0;
        _responseMessageTags.Clear();
    }

    /// <summary>Reset before each top-level AL invocation, like AlValueCapture.Reset.</summary>
    public static void Reset() => _segmenter = Enabled ? new AlIterationSegmenter() : null;

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

    /// <summary>Fed from AlMessageCapture.Record with the message's index into the response list.</summary>
    public static void OnMessage(AlCapturedMessage message, int index)
    {
        if (!Enabled) return;
        _segmenter?.OnMessage(message, index);
    }

    /// <summary>Everything segmented since Reset(), in entry order, resolved to files and
    /// lines, with loop ids offset into the response id-space and the flat-series tags.</summary>
    public static AlIterationCollect Collect()
    {
        var seg = _segmenter;
        if (seg == null)
            return new AlIterationCollect(Array.Empty<AlLoopRecord>(),
                new Dictionary<int, (int, int)>(), new Dictionary<int, (int, int)>());

        var loops = new List<AlLoopRecord>();
        var captureTags = new Dictionary<int, (int, int)>();
        var messageTags = new Dictionary<int, (int, int)>();
        int maxLocalId = -1;

        foreach (var inst in seg.Finish())
        {
            int loopId = _idBase + inst.Id;
            if (inst.Id > maxLocalId) maxLocalId = inst.Id;
            var info = AlScopeSyntaxResolver.Resolve(inst.ScopeInstance.GetType())!;
            var iterations = new List<AlIterationRecord>(inst.Steps.Count);
            foreach (var s in inst.Steps)
            {
                var ids = s.StatementIds.Distinct().OrderBy(i => i).ToList();
                var lines = ids.Select(inst.Table.LineOf).Where(l => l != null).Select(l => l!.Value).Distinct().ToList();
                iterations.Add(new AlIterationRecord(s.Iteration, ids, lines));
                foreach (var ci in s.CaptureIndices) captureTags[ci] = (loopId, s.Iteration);
                foreach (var mi in s.MessageIndices) { messageTags[mi] = (loopId, s.Iteration); _responseMessageTags[mi] = (loopId, s.Iteration); }
            }
            loops.Add(new AlLoopRecord(
                Id: loopId,
                ScopeName: info.ScopeName,
                FilePath: info.FilePath,
                Line: inst.Site.StartLine,
                Column: inst.Site.StartColumn,
                EndLine: inst.Site.EndLine,
                EndColumn: inst.Site.EndColumn,
                ParentLoop: inst.ParentId is int p ? _idBase + p : null,
                ParentIteration: inst.ParentIteration,
                IterationCount: inst.IterationCount,
                ClosedBy: inst.ClosedBy ?? AlLoopEnd.Unfinished,
                Unsegmentable: inst.Site.Unsegmentable,
                Iterations: iterations));
        }
        _idBase += maxLocalId + 1; // next bundle's ids continue past this one's
        return new AlIterationCollect(loops, captureTags, messageTags);
    }
}
