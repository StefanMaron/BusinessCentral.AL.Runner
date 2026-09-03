// AlLoopModel — the static half of `iterationTracking` (issue #2056): what a loop IS,
// in terms the runtime segmenter (AlIterationSegmenter) can check against BC's own
// StmtHit(N) stream.
//
// Two layers:
//   AlLoopSite       — one loop statement in AL SOURCE, from BC's syntax tree
//                      (AlMemberSyntaxIndex): kind, loop variable, and the TEXT RANGES of
//                      its header (the part that runs per evaluation / at entry) and of
//                      each body statement. Source-only; knows nothing about ids.
//   AlLoopSiteTable  — the same loop RESOLVED against one compiled scope class's
//                      [SourceSpans] table (AlLoopScopeTable.Build): which statement ids
//                      are header ids, which are body ids, and which single event opens
//                      an iteration (the "marker", see below). This is what the segmenter
//                      consumes; it never touches source text.
//
// HOW BC INSTRUMENTS LOOPS — measured, not assumed (al-runner's own `coverage:true`
// statement table for each shape, see ServerExecuteIterationsTests):
//   for / foreach   ONE statement id spanning the whole statement, hit ONCE at entry. The
//                   body's ids are hit once per iteration. There is NO per-iteration
//                   header hit — the increment/next-element step is not instrumented.
//   while           the condition is a CStmtHit, hit once PER EVALUATION including the
//                   final false one; the `while` keyword itself has no id.
//   repeat          body ids per pass, then the until-condition's CStmtHit per
//                   evaluation; the `repeat` keyword has no id.
//   begin..end body a `for ... do begin ... end` additionally gets an id on the closing
//                   `end`, hit ONCE after the loop finishes (normally or via `break`, not
//                   via `exit`). It is inside the loop statement's span but outside every
//                   body statement's span, so Build classifies it as neither header nor
//                   body — i.e. as the first "outside" hit that ends the loop instance.
//   break           no id at all. `exit` has one. `if` conditions are CStmtHits per
//                   evaluation; a `case` statement has one id per evaluation.
//   ids are assigned in document order (pre-order), so an `if`'s condition id is lower
//                   than its branch ids and a nested loop's own id is lower than its body's.
//
// THE MARKER — which event opens a new iteration. Every iteration executes the body's
// first statement exactly once, so that statement's FIRST id (its condition, for an
// `if`/`case`; itself, for a plain statement) fires once per iteration. Two exceptions
// make "first body statement" a rule rather than a lookup:
//   - if the first body statement is itself a loop, its own ids fire MANY times per
//     enclosing iteration (a nested `while`'s condition, a nested `repeat`'s body), so
//     the marker is "the nested loop ENTERS" (MarkerNestedSiteIndex), which the
//     segmenter observes exactly once per enclosing iteration;
//   - if the first body statement carries no id (e.g. `break;`), the next one is used.
// A loop whose body has NO instrumented statement at all (an empty body) cannot have
// its iterations counted from the hit stream; Build records WHY in Unsegmentable and
// the wire reports it as such rather than as "0 iterations" (.claude/rules/
// loud-failures.md — never a silently wrong segmentation).
namespace AlRunner.Infrastructure;

public enum AlLoopKind { For, ForEach, While, Repeat }

/// <summary>A 0-based (line, column) position in an AL source file — the same
/// coordinate space BC's [SourceSpans] decode to (AlSourceSpanCodec) and its syntax
/// tree's GetLineSpan() reports, so the two can be compared directly.</summary>
public readonly record struct AlTextPosition(int Line, int Column) : IComparable<AlTextPosition>
{
    public int CompareTo(AlTextPosition other) =>
        Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);

    public static bool operator <(AlTextPosition a, AlTextPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(AlTextPosition a, AlTextPosition b) => a.CompareTo(b) > 0;
    public static bool operator <=(AlTextPosition a, AlTextPosition b) => a.CompareTo(b) <= 0;
    public static bool operator >=(AlTextPosition a, AlTextPosition b) => a.CompareTo(b) >= 0;
}

/// <summary>An inclusive 0-based text range. Statement membership is decided by the
/// statement's START position falling inside the range — a `for` statement's own
/// [SourceSpans] entry spans the whole statement (body included), so end-containment
/// would misfile it.</summary>
public readonly record struct AlTextRange(AlTextPosition Start, AlTextPosition End)
{
    public bool ContainsStart(AlTextPosition p) => Start <= p && p <= End;
}

/// <summary>One statement of a loop body, in document order, `begin..end` blocks
/// flattened. <c>NestedSiteIndex</c> is set when the statement IS a loop (its site
/// index in the same member's site list).</summary>
public readonly record struct AlLoopBodyStatement(AlTextRange Range, int? NestedSiteIndex);

/// <summary>One loop statement in AL source, from the syntax tree — see the file
/// header. <c>Index</c> is its position in the owning member's site list (document
/// order); <c>ParentIndex</c> the lexically enclosing loop's, if any.</summary>
public sealed record AlLoopSite(
    int Index,
    AlLoopKind Kind,
    string? LoopVariable,
    AlTextRange Range,
    IReadOnlyList<AlTextRange> HeaderRanges,
    IReadOnlyList<AlLoopBodyStatement> Body,
    int? ParentIndex);

/// <summary>One loop site resolved against a compiled scope's statement ids — what
/// AlIterationSegmenter checks each StmtHit(N) against. See the file header for the
/// header/body/marker rules.</summary>
public sealed class AlLoopSiteTable
{
    public AlLoopSiteTable(
        int index, AlLoopKind kind, string? loopVariable, int startLine, int endLine,
        IReadOnlySet<int> headerIds, IReadOnlySet<int> bodyIds,
        int? markerStatementId, int? markerNestedSiteIndex, int? parentIndex, string? unsegmentable)
    {
        Index = index;
        Kind = kind;
        LoopVariable = loopVariable;
        StartLine = startLine;
        EndLine = endLine;
        HeaderIds = headerIds;
        BodyIds = bodyIds;
        MarkerStatementId = markerStatementId;
        MarkerNestedSiteIndex = markerNestedSiteIndex;
        ParentIndex = parentIndex;
        Unsegmentable = unsegmentable;
    }

    public int Index { get; }
    public AlLoopKind Kind { get; }
    /// <summary>The `for`/`foreach` control variable's AL name; null for while/repeat.
    /// Drives the capture split rule in AlIterationSegmenter.</summary>
    public string? LoopVariable { get; }
    /// <summary>1-based AL source lines of the loop statement (first line of the
    /// keyword to last line of the body).</summary>
    public int StartLine { get; }
    public int EndLine { get; }
    /// <summary>Statement ids that fire at entry (`for`/`foreach`) or per evaluation
    /// (`while`/`until` condition) — inside the loop but not an iteration's body.</summary>
    public IReadOnlySet<int> HeaderIds { get; }
    /// <summary>Statement ids inside the body, nested loops' ids included.</summary>
    public IReadOnlySet<int> BodyIds { get; }
    /// <summary>The body statement id whose hit opens an iteration — or null when the
    /// first body statement is a nested loop (<see cref="MarkerNestedSiteIndex"/>).</summary>
    public int? MarkerStatementId { get; }
    /// <summary>The nested loop site whose ENTRY opens an iteration of this loop.</summary>
    public int? MarkerNestedSiteIndex { get; }
    public int? ParentIndex { get; }
    /// <summary>Non-null when iterations cannot be counted for this loop, with the
    /// reason; such a loop still reports that it was entered, never a fake count.</summary>
    public string? Unsegmentable { get; }

    /// <summary>Lexically nested loops (direct children). Wired by AlLoopScopeTable.</summary>
    public IReadOnlyList<AlLoopSiteTable> Children => _children;
    private readonly List<AlLoopSiteTable> _children = new();
    internal void AddChild(AlLoopSiteTable child) => _children.Add(child);

    public bool Owns(int statementId) => HeaderIds.Contains(statementId) || BodyIds.Contains(statementId);
}

/// <summary>Every loop of one compiled scope class, plus the scope's own [SourceSpans]
/// so executed statement ids can be turned back into AL lines on the wire.</summary>
public sealed class AlLoopScopeTable
{
    public AlLoopScopeTable(IReadOnlyList<AlLoopSiteTable> sites, long[] spans)
    {
        Sites = sites;
        Spans = spans;
        var roots = new List<AlLoopSiteTable>();
        foreach (var s in sites)
        {
            if (s.ParentIndex is int p) sites[p].AddChild(s);
            else roots.Add(s);
        }
        Roots = roots;
    }

    public IReadOnlyList<AlLoopSiteTable> Sites { get; }
    /// <summary>Loops not nested inside another loop of this scope.</summary>
    public IReadOnlyList<AlLoopSiteTable> Roots { get; }
    public long[] Spans { get; }

    public AlLoopSiteTable? RootSiteOwning(int statementId)
    {
        foreach (var r in Roots)
            if (r.Owns(statementId)) return r;
        return null;
    }

    /// <summary>
    /// The `for`/`foreach` loop variables that were assigned between the statement
    /// <paramref name="previous"/> just finished and statement <paramref name="current"/>
    /// about to run - i.e. <paramref name="current"/> opens a pass of a counted loop
    /// (its first body statement, following into a nested loop's entry when the body
    /// starts with one) and <paramref name="previous"/> is not that loop's header, so this
    /// is not the first pass (the header's own hit already observed the initial value;
    /// see AlValueCapture's header). AlValueCapture adds these to the write set so a
    /// `foreach` element equal to the previous one still gets its record: for a `for`
    /// the increment always changes the value, for a `foreach` it may not.
    /// </summary>
    public IEnumerable<string> LoopVariablesAssignedBefore(int current, int previous)
    {
        foreach (var site in Sites)
        {
            if (site.LoopVariable == null || site.Kind is not (AlLoopKind.For or AlLoopKind.ForEach)) continue;
            if (!OpensPassOf(site, current, previous)) continue;
            if (site.HeaderIds.Contains(previous)) continue; // first pass: the header hit observed it
            yield return site.LoopVariable;
        }
    }

    // `current` opens a pass of `site`: it is the site's marker statement, or - when the
    // body starts with a nested loop - that nested loop's ENTRY. What "entry" is depends
    // on the nested kind, because its ids fire more than once per enclosing pass:
    //   for/foreach  its header fires exactly once per entry: any header hit is an entry.
    //   while        its condition fires per evaluation; a mid-pass re-evaluation always
    //                follows one of its own body statements, an entry never does.
    //   repeat       no header before the body; its first statement fires per pass. A
    //                pass that follows the until-condition is a re-pass, not an entry.
    //                (A repeat that IS the whole enclosing body re-enters right after its
    //                final until-condition too, which this cannot tell apart; the diff
    //                still reports the enclosing loop variable's change for a `for`.)
    private bool OpensPassOf(AlLoopSiteTable site, int current, int previous)
    {
        if (site.MarkerStatementId is int m) return m == current;
        if (site.MarkerNestedSiteIndex is int n && n >= 0 && n < Sites.Count)
        {
            var nested = Sites[n];
            switch (nested.Kind)
            {
                case AlLoopKind.For:
                case AlLoopKind.ForEach:
                    return nested.HeaderIds.Contains(current);
                case AlLoopKind.While:
                    return nested.HeaderIds.Contains(current) && !nested.BodyIds.Contains(previous);
                case AlLoopKind.Repeat:
                    return OpensPassOf(nested, current, previous) && !nested.Owns(previous);
            }
        }
        return false;
    }

    /// <summary>1-based AL line of a statement id, or null when the id is outside the
    /// span table (BC shape drift — reported as absent, never as a fake line).</summary>
    public int? LineOf(int statementId)
    {
        if (statementId < 0 || statementId >= Spans.Length) return null;
        return AlSourceSpanCodec.Decode(Spans[statementId]).FromLine + 1;
    }

    /// <summary>
    /// Resolves syntax-level sites against a compiled scope's [SourceSpans]: a statement
    /// id belongs to a site's header when its span STARTS inside one of the site's
    /// header ranges, and to its body when it starts inside one of the body statements'
    /// ranges (nested loops' ids therefore belong to every enclosing body too). The
    /// marker is derived per the file header. <paramref name="instrumented"/> restricts
    /// which indices are considered (BC emits a trailing never-instrumented sentinel
    /// entry — see AlCoverageInstrumentedStatements); null means every index.
    /// </summary>
    public static AlLoopScopeTable Build(IReadOnlyList<AlLoopSite> sites, long[] spans, IEnumerable<int>? instrumented = null)
    {
        var ids = instrumented?.ToArray() ?? Enumerable.Range(0, spans.Length).ToArray();
        var starts = new Dictionary<int, AlTextPosition>(ids.Length);
        foreach (var i in ids)
        {
            if (i < 0 || i >= spans.Length) continue; // defensive: BC shape drift
            var (fromLine, fromColumn, _, _) = AlSourceSpanCodec.Decode(spans[i]);
            starts[i] = new AlTextPosition(fromLine, fromColumn);
        }

        var tables = new List<AlLoopSiteTable>(sites.Count);
        foreach (var site in sites)
        {
            var header = new HashSet<int>();
            var body = new HashSet<int>();
            foreach (var (id, pos) in starts)
            {
                if (site.HeaderRanges.Any(r => r.ContainsStart(pos))) header.Add(id);
                else if (site.Body.Any(b => b.Range.ContainsStart(pos))) body.Add(id);
            }

            int? markerId = null;
            int? markerNested = null;
            foreach (var stmt in site.Body)
            {
                if (stmt.NestedSiteIndex is int nested) { markerNested = nested; break; }
                var first = starts.Where(kv => stmt.Range.ContainsStart(kv.Value))
                    .Select(kv => (int?)kv.Key).OrderBy(k => k).FirstOrDefault();
                if (first is int f) { markerId = f; break; }
            }
            string? unsegmentable = markerId == null && markerNested == null
                ? "loop body has no instrumented statement (for example an empty body), so iterations cannot be counted from the statement hit stream"
                : null;

            tables.Add(new AlLoopSiteTable(
                site.Index, site.Kind, site.LoopVariable,
                startLine: site.Range.Start.Line + 1, endLine: site.Range.End.Line + 1,
                header, body, markerId, markerNested, site.ParentIndex, unsegmentable));
        }
        return new AlLoopScopeTable(tables, spans);
    }
}
