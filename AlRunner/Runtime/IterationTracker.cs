namespace AlRunner.Runtime;

/// <summary>
/// Static collector for per-iteration data during loop execution.
/// When enabled, injected code calls EnterLoop/EnterIteration/ExitLoop
/// to capture variable values, messages, and executed lines per iteration.
///
/// Design: EnterIteration finalizes the previous iteration's data before
/// starting the new one. ExitLoop finalizes the last iteration. This means
/// only ONE injection point is needed in the loop body (EnterIteration at
/// the start) — no EndIteration call needed, which avoids issues with
/// break/continue/early-exit skipping the finalization.
/// </summary>
public static class IterationTracker
{
    private static bool _enabled;
    private static readonly List<LoopRecord> _loops = new();
    private static readonly Stack<ActiveLoop> _loopStack = new();
    private static int _nextLoopId;

    private static List<int> _currentIterationHits = new();

    public static bool Enabled => _enabled;
    public static void Enable() => _enabled = true;
    public static void Disable() => _enabled = false;

    /// <summary>
    /// Called by StmtHit/CStmtHit on every execution — records which statements
    /// run during the current iteration (not just new unique hits).
    /// </summary>
    public static void RecordHit(int stmtId)
    {
        if (!_enabled || _loopStack.Count == 0) return;
        _currentIterationHits.Add(stmtId);
    }

    public static void Reset()
    {
        _loops.Clear();
        _loopStack.Clear();
        _currentIterationHits.Clear();
        _nextLoopId = 0;
    }

    public static int EnterLoop(string scopeName, int sourceStartLine, int sourceEndLine)
    {
        if (!_enabled) return -1;

        var loopId = _nextLoopId++;
        int? parentLoopId = _loopStack.Count > 0 ? _loopStack.Peek().LoopId : null;
        int? parentIteration = _loopStack.Count > 0 ? _loopStack.Peek().CurrentIteration : null;

        var record = new LoopRecord
        {
            LoopId = loopId,
            ScopeName = scopeName,
            SourceStartLine = sourceStartLine,
            SourceEndLine = sourceEndLine,
            ParentLoopId = parentLoopId,
            ParentIteration = parentIteration,
        };
        _loops.Add(record);

        _loopStack.Push(new ActiveLoop
        {
            LoopId = loopId,
            Record = record,
        });

        return loopId;
    }

    /// <summary>
    /// Called at the top of each iteration. Finalizes the previous iteration's
    /// captured data (if any) before starting a new snapshot.
    /// </summary>
    public static void EnterIteration(int loopId)
    {
        if (!_enabled) return;
        if (_loopStack.Count == 0 || _loopStack.Peek().LoopId != loopId) return;

        var active = _loopStack.Peek();

        // Finalize the previous iteration (if this isn't the first)
        if (active.CurrentIteration > 0)
        {
            FinalizeIteration(active);
        }

        // Start new iteration
        active.CurrentIteration++;
        active.ValueSnapshotBefore = ValueCapture.GetCaptures().Count;
        active.MessageSnapshotBefore = MessageCapture.GetMessages().Count;
        _currentIterationHits.Clear();
    }

    /// <summary>
    /// Called after the loop exits (in a finally block, so always runs).
    /// Finalizes the last iteration's data.
    /// </summary>
    public static void ExitLoop(int loopId)
    {
        if (!_enabled) return;
        if (_loopStack.Count == 0 || _loopStack.Peek().LoopId != loopId) return;

        var active = _loopStack.Pop();

        // Finalize the last iteration
        if (active.CurrentIteration > 0)
        {
            FinalizeIteration(active);
        }

        active.Record.IterationCount = active.CurrentIteration;
    }

    /// <summary>
    /// Captures the delta of values, messages, and hit lines since the
    /// iteration started and records them as an IterationStep.
    /// </summary>
    private static void FinalizeIteration(ActiveLoop active)
    {
        // Captured values added during this iteration
        var allValues = ValueCapture.GetCaptures();
        var iterValues = new List<CapturedValueSnapshot>();
        for (int i = active.ValueSnapshotBefore; i < allValues.Count; i++)
        {
            var v = allValues[i];
            iterValues.Add(new CapturedValueSnapshot { VariableName = v.VariableName, Value = v.Value ?? "" });
        }

        // Messages added during this iteration
        var allMessages = MessageCapture.GetMessages();
        var iterMessages = new List<string>();
        for (int i = active.MessageSnapshotBefore; i < allMessages.Count; i++)
            iterMessages.Add(allMessages[i]);

        // Lines hit during this iteration
        var iterLines = _currentIterationHits.Distinct().ToList();

        active.Record.Steps.Add(new IterationStep
        {
            Iteration = active.CurrentIteration,
            CapturedValues = iterValues,
            Messages = iterMessages,
            LinesExecuted = iterLines,
        });
    }

    public static List<LoopRecord> GetLoops() => new(_loops);

    // --- Data classes ---

    public class LoopRecord
    {
        public int LoopId { get; init; }
        public string ScopeName { get; init; } = "";
        public int SourceStartLine { get; init; }
        public int SourceEndLine { get; init; }
        public int? ParentLoopId { get; init; }
        public int? ParentIteration { get; init; }
        public int IterationCount { get; set; }
        public List<IterationStep> Steps { get; init; } = new();
    }

    public class IterationStep
    {
        public int Iteration { get; init; }
        public List<CapturedValueSnapshot> CapturedValues { get; init; } = new();
        public List<string> Messages { get; init; } = new();
        public List<int> LinesExecuted { get; init; } = new();
    }

    public class CapturedValueSnapshot
    {
        public string VariableName { get; init; } = "";
        public string Value { get; init; } = "";
    }

    private class ActiveLoop
    {
        public int LoopId { get; init; }
        public LoopRecord Record { get; init; } = null!;
        public int CurrentIteration { get; set; }
        public int ValueSnapshotBefore { get; set; }
        public int MessageSnapshotBefore { get; set; }
    }
}
