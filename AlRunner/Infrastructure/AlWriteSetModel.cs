// Which locals a statement assigns, from syntax (#2056). The value diff cannot tell
// "x := 5 ran while x was 5" from "x untouched"; the write set makes the former a record.
// Counted: the root local of an assignment target (`Rec.Amount :=` writes Rec), a
// method-call statement's receiver (`Rec.Insert();`), the first argument of Clear and
// Evaluate. Not knowable from syntax, so left to the diff: a user procedure's var
// parameter, a receiver mutated inside an expression. Loop variables are covered by
// AlLoopScopeTable.LoopVariablesAssignedBefore.
namespace AlRunner.Infrastructure;

/// <summary>One statement's write targets, keyed by the statement's start position.</summary>
public readonly record struct AlStatementWrites(AlTextPosition Start, IReadOnlyList<string> Targets);

/// <summary>Statement id to write set for one scope class.</summary>
public sealed class AlWriteSetTable
{
    private static readonly IReadOnlySet<string> NoTargets = new HashSet<string>();
    public static readonly AlWriteSetTable Empty = new(new Dictionary<int, IReadOnlySet<string>>());

    private readonly Dictionary<int, IReadOnlySet<string>> _byId;

    private AlWriteSetTable(Dictionary<int, IReadOnlySet<string>> byId) => _byId = byId;

    /// <summary>Locals the statement assigns (case-insensitive); empty for an id with no assigning statement.</summary>
    public IReadOnlySet<string> TargetsOf(int statementId) =>
        _byId.TryGetValue(statementId, out var s) ? s : NoTargets;

    /// <summary>Pairs each id with the statement starting exactly where its span starts. A condition's id starts mid-line, a block's `end` id at `end`; neither has a statement there.</summary>
    public static AlWriteSetTable Build(IReadOnlyList<AlStatementWrites> writes, long[] spans, IEnumerable<int>? instrumented = null)
    {
        var byStart = new Dictionary<AlTextPosition, IReadOnlySet<string>>();
        foreach (var w in writes)
        {
            if (w.Targets.Count == 0) continue;
            byStart[w.Start] = new HashSet<string>(w.Targets, StringComparer.OrdinalIgnoreCase);
        }
        var byId = new Dictionary<int, IReadOnlySet<string>>();
        foreach (var i in instrumented ?? Enumerable.Range(0, spans.Length))
        {
            if (i < 0 || i >= spans.Length) continue; // defensive: BC shape drift
            var (fromLine, fromColumn, _, _) = AlSourceSpanCodec.Decode(spans[i]);
            if (byStart.TryGetValue(new AlTextPosition(fromLine, fromColumn), out var targets))
                byId[i] = targets;
        }
        return new AlWriteSetTable(byId);
    }
}
