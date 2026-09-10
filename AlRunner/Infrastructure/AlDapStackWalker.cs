// AlDapStackWalker — DAP `stackTrace`: walks a paused NavMethodScope's ParentScope
// chain outward, the same chain AlCallStackCapture already walks to format an AL
// error's call stack, and resolves each frame's CURRENT AL source line from its own
// [SourceSpansAttribute] (same decode DapBreakpointResolver uses for setBreakpoints).
namespace AlRunner.Infrastructure;

/// <summary>One live AL call-stack frame at a paused breakpoint. <c>Id</c> is a dense
/// small int (0 = innermost/paused frame), reused directly as the DAP
/// `stackTrace`/`scopes`/`variables` frame id — no separate id allocator needed.</summary>
public readonly record struct AlDapFrame(
    int Id, string ScopeName, string? SourcePath, int Line, Microsoft.Dynamics.Nav.Runtime.NavMethodScope Scope);

public static class AlDapStackWalker
{
    /// <summary>
    /// Walks from <paramref name="pausedScope"/> outward via ParentScope, stopping at
    /// the root scope (Ncl's own bookkeeping frame, never AL-compiler-generated — it
    /// carries no [SourceSpansAttribute] and IsRootScope is true).
    ///
    /// <paramref name="pausedStatementIndex"/> is the topmost (index-0) frame's
    /// CURRENT statement — pass AlDapSession's own `currentStatementNumber` parameter,
    /// NOT <c>pausedScope.StatementNumber</c>. This matters: the Cecil-rewritten hook
    /// runs BEFORE StmtHit's own `statementNumber = currentStatementNumber;`
    /// assignment (same "prepend runs first" mechanism as everywhere else in this
    /// file's family — see AlDapSession's file header), so
    /// <c>pausedScope.StatementNumber</c> is still the PREVIOUS statement's index at
    /// the exact instant a breakpoint fires. Every ANCESTOR frame does not have this
    /// problem — an ancestor's own StatementNumber was already correctly set by ITS
    /// earlier StmtHit call (it is mid-call, waiting on whatever led to the paused
    /// frame), so only frame 0 needs the override.
    ///
    /// <paramref name="sourceMap"/> resolves each frame's owning AL object to a file
    /// path (see AlCoverageSourceMap.Build); a frame whose object isn't in the map
    /// (e.g. a dependency app's procedure) still appears, with SourcePath null rather
    /// than the frame being dropped — a debugger UI can show "no source" for it,
    /// matching how ServerProtocol already treats a stack frame with no known file
    /// (loud-failures.md: never silently omit a frame the caller could act on). It also
    /// carries each object's line offset (#3786), which is what turns the object-relative
    /// line BC records into the file line a DAP client expects beside that path; an object
    /// the map does not know offsets by 0, so it keeps its previous line unchanged.
    /// </summary>
    public static List<AlDapFrame> Walk(
        Microsoft.Dynamics.Nav.Runtime.NavMethodScope pausedScope,
        int pausedStatementIndex,
        AlSourceLocationMap sourceMap)
    {
        var frames = new List<AlDapFrame>();
        Microsoft.Dynamics.Nav.Runtime.NavMethodScope? cur = pausedScope;
        int id = 0;
        while (cur != null && !cur.IsRootScope)
        {
            var (label, objId) = AlCallStackCapture.ParseObjectTypeAndId(cur.GetType());
            sourceMap.TryGetValue((label, objId), out var path);
            // #3786: ResolveLine answers the line within the OWNING OBJECT's text, which is
            // a file line only for the first object in a file. The frame's SourcePath and
            // its Line have to agree about which numbering they use, and a DAP client reads
            // both together, so the offset is added here where the pair is assembled rather
            // than inside ResolveLine — whose contract stays "object-relative", the number
            // BC actually recorded.
            var raw = id == 0 ? ResolveLine(cur, pausedStatementIndex) : ResolveCurrentLine(cur);
            // A frame that resolved no line at all stays 0; shifting that by an offset would
            // invent a line for a frame whose object carries no spans.
            var line = raw == 0 ? 0 : raw + sourceMap.LineOffset(label, objId);
            frames.Add(new AlDapFrame(id, cur.ScopeName ?? "?", path, line, cur));
            id++;
            cur = cur.ParentScope;
        }
        return frames;
    }

    /// <summary>The AL source line <paramref name="scope"/> is currently stopped at, per its
    /// OWN live StatementNumber — correct for any ANCESTOR frame, but NOT for the paused
    /// (topmost) frame itself; see <see cref="Walk"/>'s doc comment for why.
    /// <para>
    /// The line is relative to the owning OBJECT's text, which is the file line only for the
    /// first object in a file (#3786). <see cref="Walk"/> adds the object's offset; a direct
    /// caller of this method gets the raw number BC recorded and must add its own.
    /// </para></summary>
    public static int ResolveCurrentLine(Microsoft.Dynamics.Nav.Runtime.NavMethodScope scope)
        => ResolveLine(scope, scope.StatementNumber);

    private static int ResolveLine(Microsoft.Dynamics.Nav.Runtime.NavMethodScope scope, int statementIndex)
    {
        var spans = AlSourceSpansReflection.TryGetSpans(scope.GetType());
        if (spans == null) return 0;
        if (statementIndex < 0 || statementIndex >= spans.Length) return 0;
        return AlSourceSpanCodec.AbsoluteFromLine(spans[statementIndex]);
    }
}
