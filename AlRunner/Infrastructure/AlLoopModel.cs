// Static loop model for iterationTracking (#2056): AlLoopSite is a loop as parsed from
// AL syntax, AlLoopSiteTable the same loop resolved to a scope class's statement ids.
//
// BC instruments loops like this (measured on the statement table, see ServerExecuteIterationsTests):
//   for / foreach   one id for the statement, hit once at entry; body ids once per pass
//   while           the condition is hit per evaluation, including the final false one
//   repeat          body ids per pass, then the until-condition per evaluation
//   do begin..end   the closing `end` gets an id, hit once after the loop; `break` has none
// Ids are numbered in document order.
//
// An iteration opens when the body's first statement is hit (the "marker"). If that
// statement is itself a loop, the marker is that loop's entry, because its own ids fire
// several times per pass. A body with no instrumented statement is unsegmentable and
// reported as such, never counted.
namespace AlRunner.Infrastructure;

public enum AlLoopKind { For, ForEach, While, Repeat }

/// <summary>0-based (line, column): the coordinate space both [SourceSpans] and the syntax tree use.</summary>
public readonly record struct AlTextPosition(int Line, int Column) : IComparable<AlTextPosition>
{
    public int CompareTo(AlTextPosition other) =>
        Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);

    public static bool operator <(AlTextPosition a, AlTextPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(AlTextPosition a, AlTextPosition b) => a.CompareTo(b) > 0;
    public static bool operator <=(AlTextPosition a, AlTextPosition b) => a.CompareTo(b) <= 0;
    public static bool operator >=(AlTextPosition a, AlTextPosition b) => a.CompareTo(b) >= 0;
}

/// <summary>Inclusive 0-based range. Membership is by a statement's start, since a `for` statement's span covers its body.</summary>
public readonly record struct AlTextRange(AlTextPosition Start, AlTextPosition End)
{
    public bool ContainsStart(AlTextPosition p) => Start <= p && p <= End;
}

/// <summary>A body statement in document order, blocks flattened. NestedSiteIndex is set when it is a loop.</summary>
public readonly record struct AlLoopBodyStatement(AlTextRange Range, int? NestedSiteIndex);

/// <summary>A loop as parsed from AL. Index is its position in the member's site list.</summary>
public sealed record AlLoopSite(
    int Index,
    AlLoopKind Kind,
    string? LoopVariable,
    AlTextRange Range,
    IReadOnlyList<AlTextRange> HeaderRanges,
    IReadOnlyList<AlLoopBodyStatement> Body,
    int? ParentIndex);

/// <summary>A loop resolved to one scope class's statement ids.</summary>
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
    /// <summary>The for/foreach control variable; null for while/repeat.</summary>
    public string? LoopVariable { get; }
    /// <summary>1-based source lines of the loop statement.</summary>
    public int StartLine { get; }
    public int EndLine { get; }
    /// <summary>Ids hit at entry (for/foreach) or per evaluation (while/until condition).</summary>
    public IReadOnlySet<int> HeaderIds { get; }
    /// <summary>Ids inside the body, nested loops included.</summary>
    public IReadOnlySet<int> BodyIds { get; }
    /// <summary>The body statement whose hit opens an iteration, unless the body starts with a loop.</summary>
    public int? MarkerStatementId { get; }
    /// <summary>The nested loop whose entry opens an iteration of this one.</summary>
    public int? MarkerNestedSiteIndex { get; }
    public int? ParentIndex { get; }
    /// <summary>Why iterations cannot be counted for this loop, or null.</summary>
    public string? Unsegmentable { get; }

    /// <summary>Directly nested loops.</summary>
    public IReadOnlyList<AlLoopSiteTable> Children => _children;
    private readonly List<AlLoopSiteTable> _children = new();
    internal void AddChild(AlLoopSiteTable child) => _children.Add(child);

    public bool Owns(int statementId) => HeaderIds.Contains(statementId) || BodyIds.Contains(statementId);
}

/// <summary>Every loop of one scope class, plus its [SourceSpans] for line resolution.</summary>
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
    /// <summary>Loops not nested in another loop of this scope.</summary>
    public IReadOnlyList<AlLoopSiteTable> Roots { get; }
    public long[] Spans { get; }

    public AlLoopSiteTable? RootSiteOwning(int statementId)
    {
        foreach (var r in Roots)
            if (r.Owns(statementId)) return r;
        return null;
    }

    /// <summary>
    /// The for/foreach loop variables assigned between statement <paramref name="previous"/>
    /// finishing and <paramref name="current"/> starting: <paramref name="current"/> opens a pass and
    /// <paramref name="previous"/> is not that loop's header (the first pass is observed at the
    /// header hit). Needed for a foreach over equal consecutive elements, which the value diff
    /// cannot see.
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

    // `current` opens a pass of `site`: its marker, or the entry of the nested loop the body
    // starts with. A nested for/foreach header fires once per entry; a nested while condition
    // also fires mid-pass, but then always after one of its own body statements; a nested
    // repeat's first statement also fires after its until-condition.
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

    /// <summary>1-based line of a statement id; null when the id is outside the span table.</summary>
    public int? LineOf(int statementId)
    {
        if (statementId < 0 || statementId >= Spans.Length) return null;
        return AlSourceSpanCodec.Decode(Spans[statementId]).FromLine + 1;
    }

    /// <summary>
    /// Resolves parsed sites against a scope's [SourceSpans]: an id is a header id when its span
    /// starts in a header range, a body id when it starts in a body statement. <paramref
    /// name="instrumented"/> excludes BC's trailing never-instrumented sentinel entry.
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
