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
    /// (loud-failures.md: never silently omit a frame the caller could act on).
    /// </summary>
    public static List<AlDapFrame> Walk(
        Microsoft.Dynamics.Nav.Runtime.NavMethodScope pausedScope,
        int pausedStatementIndex,
        IReadOnlyDictionary<(string Label, int Id), string> sourceMap)
    {
        var frames = new List<AlDapFrame>();
        Microsoft.Dynamics.Nav.Runtime.NavMethodScope? cur = pausedScope;
        int id = 0;
        while (cur != null && !cur.IsRootScope)
        {
            var (label, objId) = AlCallStackCapture.ParseObjectTypeAndId(cur.GetType());
            sourceMap.TryGetValue((label, objId), out var path);
            var line = id == 0 ? ResolveLine(cur, pausedStatementIndex) : ResolveCurrentLine(cur);
            frames.Add(new AlDapFrame(id, cur.ScopeName ?? "?", path, line, cur));
            id++;
            cur = cur.ParentScope;
        }
        return frames;
    }

    /// <summary>The absolute AL source line <paramref name="scope"/> is currently
    /// stopped at, per its OWN live StatementNumber — correct for any ANCESTOR frame,
    /// but NOT for the paused (topmost) frame itself; see <see cref="Walk"/>'s doc
    /// comment for why.</summary>
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
