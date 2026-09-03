// AlWriteSetModel - which AL locals a statement ASSIGNS, from the syntax tree (issue
// #2056, full-fidelity captures). `--capture-values` observes a statement's effect by
// diffing every local at the next StmtHit, which cannot tell "x := 5 ran and x was
// already 5" from "x was not touched". The agreed contract (SShadowS/ALchemist#1, carried
// into #2074's issue text) is one record per EXECUTION of an assigning statement, so a
// consumer can answer "what was x at iteration 7" when iteration 7 assigned x its old
// value. The write set is what turns "unchanged" into "assigned, unchanged" - and only
// that: a local a statement does not assign, and that did not change, still gets nothing.
//
// What counts as a write, from syntax alone (AlMemberSyntaxIndex.CollectWrites):
//   x := ...          x += ...        the assignment target's ROOT local: `Rec.Amount :=`
//                                     and `arr[1] :=` write `Rec` and `arr`
//   (for / foreach)                   NOT a write set: the loop variable's initial value is
//                                     observed at the loop statement's own hit, and the
//                                     assignment before every later pass is claimed by
//                                     AlLoopScopeTable.LoopVariablesAssignedBefore, which
//                                     knows the pass boundary - see AlLoopModel.cs
//   x.Add(5);         Rec.Insert();   a method-call STATEMENT's receiver - the receiver's
//                                     state is what such a call is for
//   Clear(x);         Evaluate(x, s); the by-reference first argument of these built-ins
// NOT claimed, because syntax cannot know it: a `var` parameter of a user procedure
// (`Helper(x)`), a receiver inside an expression (`n := Rec.Next()`), a global. The value
// diff still reports every real change to those; only a same-value re-assignment through
// one of them goes unreported. That limit is documented in docs/server-mode.md.
namespace AlRunner.Infrastructure;

/// <summary>One statement's write targets, keyed by the statement's start position - the
/// same position BC's [SourceSpans] entry for that statement starts at, which is how
/// <see cref="AlWriteSetTable.Build"/> pairs the two.</summary>
public readonly record struct AlStatementWrites(AlTextPosition Start, IReadOnlyList<string> Targets);

/// <summary>Statement id to write set for one compiled scope class.</summary>
public sealed class AlWriteSetTable
{
    private static readonly IReadOnlySet<string> NoTargets = new HashSet<string>();
    public static readonly AlWriteSetTable Empty = new(new Dictionary<int, IReadOnlySet<string>>());

    private readonly Dictionary<int, IReadOnlySet<string>> _byId;

    private AlWriteSetTable(Dictionary<int, IReadOnlySet<string>> byId) => _byId = byId;

    /// <summary>The AL locals statement <paramref name="statementId"/> assigns (case-
    /// insensitive, like AL identifiers); empty for an id with no assigning statement
    /// at its start (a condition, a block end, an unknown id) - never a throw.</summary>
    public IReadOnlySet<string> TargetsOf(int statementId) =>
        _byId.TryGetValue(statementId, out var s) ? s : NoTargets;

    /// <summary>
    /// Pairs each instrumented statement id with the write set of the statement that
    /// STARTS exactly where the id's span starts. Exact start, not containment: an `if`
    /// condition's id starts mid-line where no statement starts, a `for` statement's id
    /// starts at `for` where the loop-variable write is recorded, a block's closing `end`
    /// id starts at `end` where nothing is. <paramref name="instrumented"/> restricts the
    /// ids considered (null: every index).
    /// </summary>
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
