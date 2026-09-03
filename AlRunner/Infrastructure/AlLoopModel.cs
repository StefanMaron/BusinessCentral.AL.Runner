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
// several times per pass. Shapes the hit stream cannot segment are reported as such,
// never counted: an empty body, and a loop whose whole body is a nested repeat, a nested
// while containing `break`, or a nested loop that is itself unsegmentable (no outer id
// separates the nested loop's re-entry from its next pass).
namespace AlRunner.Infrastructure;

public enum AlLoopKind { For, ForEach, While, Repeat }

/// <summary>Stable reason codes for a loop whose iterations cannot be counted.</summary>
public static class AlLoopUnsegmentable
{
    public const string EmptyBody = "emptyBody";
    public const string SoleNestedRepeat = "soleNestedRepeat";
    public const string SoleNestedWhileWithBreak = "soleNestedWhileWithBreak";
    public const string SoleNestedUnsegmentable = "soleNestedUnsegmentable";
}

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

/// <summary>A loop as parsed from AL. Index is its position in the member's site list; ContainsBreak
/// is true when a `break` targets this loop (not a nested one).</summary>
public sealed record AlLoopSite(
    int Index,
    AlLoopKind Kind,
    string? LoopVariable,
    AlTextRange Range,
    IReadOnlyList<AlTextRange> HeaderRanges,
    IReadOnlyList<AlLoopBodyStatement> Body,
    int? ParentIndex,
    bool ContainsBreak);

/// <summary>A loop resolved to one scope class's statement ids.</summary>
public sealed class AlLoopSiteTable
{
    public AlLoopSiteTable(
        int index, AlLoopKind kind, string? loopVariable,
        int startLine, int startColumn, int endLine, int endColumn,
        IReadOnlySet<int> headerIds, IReadOnlySet<int> bodyIds,
        int? markerStatementId, int? markerNestedSiteIndex, int? parentIndex, string? unsegmentable)
    {
        Index = index;
        Kind = kind;
        LoopVariable = loopVariable;
        StartLine = startLine;
        StartColumn = startColumn;
        EndLine = endLine;
        EndColumn = endColumn;
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
    /// <summary>1-based source position of the loop statement.</summary>
    public int StartLine { get; }
    public int StartColumn { get; }
    public int EndLine { get; }
    public int EndColumn { get; }
    /// <summary>Ids hit at entry (for/foreach) or per evaluation (while/until condition).</summary>
    public IReadOnlySet<int> HeaderIds { get; }
    /// <summary>Ids inside the body, nested loops included.</summary>
    public IReadOnlySet<int> BodyIds { get; }
    /// <summary>The body statement whose hit opens an iteration, unless the body starts with a loop.</summary>
    public int? MarkerStatementId { get; }
    /// <summary>The nested loop whose entry opens an iteration of this one.</summary>
    public int? MarkerNestedSiteIndex { get; }
    public int? ParentIndex { get; }
    /// <summary>An AlLoopUnsegmentable code, or null when iterations can be counted.</summary>
    public string? Unsegmentable { get; }

    public bool IsCounted => Kind is AlLoopKind.For or AlLoopKind.ForEach;

    /// <summary>Directly nested loops.</summary>
    public IReadOnlyList<AlLoopSiteTable> Children => _children;
    private readonly List<AlLoopSiteTable> _children = new();
    internal AlLoopSiteTable? Parent { get; private set; }
    internal void AddChild(AlLoopSiteTable child)
    {
        _children.Add(child);
        child.Parent = this;
    }

    public bool Owns(int statementId) => HeaderIds.Contains(statementId) || BodyIds.Contains(statementId);
}

/// <summary>Every loop of one scope class, plus its [SourceSpans] for line resolution.</summary>
public sealed class AlLoopScopeTable
{
    private readonly Dictionary<int, AlLoopSiteTable> _innermostOwner = new();

    public AlLoopScopeTable(IReadOnlyList<AlLoopSiteTable> sites, long[] spans)
    {
        Sites = sites;
        Spans = spans;
        var roots = new List<AlLoopSiteTable>();
        foreach (var s in sites)
        {
            if (s.ParentIndex is int p) sites[p].AddChild(s);
            else roots.Add(s);
            // Nested sites have higher indices than their parents, so the last writer is the innermost.
            foreach (var id in s.HeaderIds) _innermostOwner[id] = s;
            foreach (var id in s.BodyIds) _innermostOwner[id] = s;
        }
        Roots = roots;
    }

    public IReadOnlyList<AlLoopSiteTable> Sites { get; }
    /// <summary>Loops not nested in another loop of this scope.</summary>
    public IReadOnlyList<AlLoopSiteTable> Roots { get; }
    public long[] Spans { get; }

    /// <summary>The innermost loop whose header or body contains the id, or null.</summary>
    public AlLoopSiteTable? OwnerOf(int statementId) =>
        _innermostOwner.TryGetValue(statementId, out var s) ? s : null;

    public AlLoopSiteTable? RootSiteOwning(int statementId)
    {
        var s = OwnerOf(statementId);
        while (s?.Parent != null) s = s.Parent;
        return s;
    }

    /// <summary>The direct child of <paramref name="site"/> under which the id sits, or null.</summary>
    public AlLoopSiteTable? ChildOwning(AlLoopSiteTable site, int statementId)
    {
        var s = OwnerOf(statementId);
        while (s != null && s.Parent != site) s = s.Parent;
        return s;
    }

    /// <summary>The loop variable a `for` statement with this header id has assigned before its
    /// own hit, or null. A `foreach` assigns its element after the header hit, at the first
    /// body hit (see <see cref="LoopVariablesAssignedBefore"/>).</summary>
    public string? LoopVariableOfHeader(int statementId)
    {
        var s = OwnerOf(statementId);
        return s != null && s.Kind == AlLoopKind.For && s.HeaderIds.Contains(statementId) ? s.LoopVariable : null;
    }

    /// <summary>
    /// The for/foreach loop variables assigned between statement <paramref name="previous"/>
    /// finishing and <paramref name="current"/> starting: <paramref name="current"/> opens a pass.
    /// A `for` assigns before its header hit, so its first pass (previous is the header) is
    /// excluded; a `foreach` assigns after it, so its first pass counts. Needed for a foreach
    /// over equal consecutive elements, which the value diff cannot see.
    /// </summary>
    public IEnumerable<string> LoopVariablesAssignedBefore(int current, int previous)
    {
        foreach (var site in Sites)
        {
            if (site.LoopVariable == null || !site.IsCounted || site.Unsegmentable != null) continue;
            if (!OpensPassOf(site, current, previous)) continue;
            if (site.Kind == AlLoopKind.For && site.HeaderIds.Contains(previous)) continue; // first pass: the header hit observed it
            yield return site.LoopVariable;
        }
    }

    // `current` opens a pass of `site`: its marker, or the entry of the nested loop the body
    // starts with. A nested for/foreach header fires once per entry; a nested while condition
    // also fires mid-pass, but then always after one of its own body statements; a nested
    // repeat's first statement also fires after its until-condition. Nothing is inferred
    // through an unsegmentable nested loop.
    private bool OpensPassOf(AlLoopSiteTable site, int current, int previous)
    {
        if (site.MarkerStatementId is int m) return m == current;
        if (site.MarkerNestedSiteIndex is int n && n >= 0 && n < Sites.Count)
        {
            var nested = Sites[n];
            if (nested.Unsegmentable != null) return false;
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

        var header = new HashSet<int>[sites.Count];
        var body = new HashSet<int>[sites.Count];
        var markerId = new int?[sites.Count];
        var markerNested = new int?[sites.Count];
        var unsegmentable = new string?[sites.Count];
        foreach (var site in sites)
        {
            var h = header[site.Index] = new HashSet<int>();
            var b = body[site.Index] = new HashSet<int>();
            foreach (var (id, pos) in starts)
            {
                if (site.HeaderRanges.Any(r => r.ContainsStart(pos))) h.Add(id);
                else if (site.Body.Any(s => s.Range.ContainsStart(pos))) b.Add(id);
            }
            foreach (var stmt in site.Body)
            {
                if (stmt.NestedSiteIndex is int nested) { markerNested[site.Index] = nested; break; }
                var first = starts.Where(kv => stmt.Range.ContainsStart(kv.Value))
                    .Select(kv => (int?)kv.Key).OrderBy(k => k).FirstOrDefault();
                if (first is int f) { markerId[site.Index] = f; break; }
            }
            if (markerId[site.Index] == null && markerNested[site.Index] == null)
                unsegmentable[site.Index] = AlLoopUnsegmentable.EmptyBody;
        }

        // Structural ambiguity: a body that is nothing but one nested loop has no outer id
        // between the nested loop's re-entry and its next pass. Nested sites have higher
        // indices, so walking backwards sees a nested loop's verdict before its parent's.
        for (int i = sites.Count - 1; i >= 0; i--)
        {
            var site = sites[i];
            if (unsegmentable[i] != null || site.Body.Count != 1 || site.Body[0].NestedSiteIndex is not int n) continue;
            var nested = sites[n];
            if (unsegmentable[n] != null) unsegmentable[i] = AlLoopUnsegmentable.SoleNestedUnsegmentable;
            else if (nested.Kind == AlLoopKind.Repeat) unsegmentable[i] = AlLoopUnsegmentable.SoleNestedRepeat;
            else if (nested.Kind == AlLoopKind.While && nested.ContainsBreak) unsegmentable[i] = AlLoopUnsegmentable.SoleNestedWhileWithBreak;
        }

        var tables = new List<AlLoopSiteTable>(sites.Count);
        foreach (var site in sites)
        {
            tables.Add(new AlLoopSiteTable(
                site.Index, site.Kind, site.LoopVariable,
                startLine: site.Range.Start.Line + 1, startColumn: site.Range.Start.Column + 1,
                endLine: site.Range.End.Line + 1, endColumn: site.Range.End.Column + 1,
                header[site.Index], body[site.Index], markerId[site.Index], markerNested[site.Index],
                site.ParentIndex, unsegmentable[site.Index]));
        }
        return new AlLoopScopeTable(tables, spans);
    }
}
