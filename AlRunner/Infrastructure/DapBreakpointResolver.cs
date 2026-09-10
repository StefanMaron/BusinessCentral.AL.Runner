// DapBreakpointResolver — DAP `setBreakpoints`: turns (source file, line) requests
// into (AL scope Type, statement index) pairs AlDapSession can register, by scanning
// every currently-loaded [SourceSpansAttribute] scope class — the same reflection
// scan AlCoverageTracker.Collect performs for --coverage — and matching on (a) the AL
// object the file declares, via a (label,id)->path source map built the same way
// --coverage's is (AlCoverageSourceMap), and (b) an EXACT absolute-line match against
// one of that object's INSTRUMENTED statements (AlCoverageInstrumentedStatements —
// the sentinel/CStmtHit-aware set, not the raw SourceSpans count, which carries a
// trailing never-instrumented entry — see that file's header).
//
// No "nearest line" heuristic: a requested line with no exact instrumented-statement
// match comes back unverified rather than silently moved to a line the caller didn't
// ask for (.claude/rules/loud-failures.md applied to protocol correctness — a
// debugger that silently relocates a breakpoint is worse than one that says so).
using System.Reflection;

namespace AlRunner.Infrastructure;

public readonly record struct DapBreakpointRequest(string SourcePath, int Line);

public readonly record struct DapResolvedBreakpoint(
    string SourcePath, int RequestedLine, bool Verified, int ActualLine, Type? ScopeType, int StatementIndex);

public static class DapBreakpointResolver
{
    /// <summary>
    /// Resolves each request against every AL object type currently loaded. Only
    /// objects present in <paramref name="sourceMap"/> (built from the SAME bundle
    /// roots the run compiled, e.g. via AlCoverageSourceMap.Build) can match — a
    /// breakpoint in a file outside the debugged bundle is unverified, not a crash.
    /// <para>
    /// Takes <see cref="AlSourceLocationMap"/> rather than the dictionary interface it
    /// implements because #3786 needs its <see cref="AlSourceLocationMap.LineOffset"/>: the
    /// path alone cannot say where an object's text begins, and a caller that supplied only
    /// paths would silently get the pre-#3786 behaviour for every object after the first in
    /// a file. The DAP path in Program.cs already builds one.
    /// </para>
    /// </summary>
    public static List<DapResolvedBreakpoint> Resolve(
        IReadOnlyList<DapBreakpointRequest> requests,
        AlSourceLocationMap sourceMap)
    {
        // Invert path -> (label,id), MANY per path. One .al file may declare any number of
        // objects, and this used to be a one-value dictionary, so the last object written
        // for a file evicted every earlier one and a breakpoint could only ever match
        // whichever survived (#3786). That is why a request inside the FIRST object of a
        // two-object file came back unverified as readily as one inside the second — not
        // the mis-resolution the issue predicted, but a whole object missing from the index.
        //
        // Both sides are real filesystem paths (not bare filenames — see
        // docs/archive/dap.md's filename-only caveat, which this improves on), compared
        // case-insensitively for cross-platform DAP clients.
        var byPath = new Dictionary<string, List<(string Label, int Id)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in sourceMap)
        {
            var full = Path.GetFullPath(kv.Value);
            if (!byPath.TryGetValue(full, out var keys)) byPath[full] = keys = new();
            keys.Add(kv.Key);
        }

        // (label,id) -> every loaded scope type for that object, each with its own
        // (statement index -> absolute AL line) map.
        var byObject = new Dictionary<(string, int), List<(Type Type, Dictionary<int, int> LineByStmt)>>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }

            foreach (var t in types)
            {
                var spans = AlSourceSpansReflection.TryGetSpans(t);
                if (spans == null) continue;

                var (label, id) = AlCallStackCapture.ParseObjectTypeAndId(t);
                if (id == 0) continue;

                // #3786: a [SourceSpans] line is relative to the OWNING OBJECT's text, not
                // to the file. For the first object in a file the two coincide and nothing
                // is visible; for any later one every line is short by the number of lines
                // its text starts down the file. LineOffset is what AlCoverageSourceMap
                // recorded when it parsed the file, and it is 0 for an object the map does
                // not know — so an unmapped object keeps exactly its old behaviour rather
                // than being shifted by a guess.
                var lineOffset = sourceMap.LineOffset(label, id);
                var instrumented = AlCoverageInstrumentedStatements.Find(t);
                var lineByStmt = new Dictionary<int, int>();
                foreach (var i in instrumented)
                {
                    if (i < 0 || i >= spans.Length) continue; // defensive: BC shape drift
                    lineByStmt[i] = AlSourceSpanCodec.AbsoluteFromLine(spans[i]) + lineOffset;
                }
                if (!byObject.TryGetValue((label, id), out var list))
                    byObject[(label, id)] = list = new();
                list.Add((t, lineByStmt));
            }
        }

        var result = new List<DapResolvedBreakpoint>(requests.Count);
        foreach (var req in requests)
        {
            var full = Path.GetFullPath(req.SourcePath);
            (Type Type, int Stmt, int Line)? match = null;
            if (byPath.TryGetValue(full, out var objKeys))
            {
                // Every object the file declares, not just one. The lines are file lines on
                // both sides now, so the first exact match is the right one whichever object
                // owns it — two objects cannot claim the same file line.
                foreach (var objKey in objKeys)
                {
                    if (!byObject.TryGetValue(objKey, out var scopes)) continue;
                    foreach (var (type, lineByStmt) in scopes)
                    {
                        foreach (var kv in lineByStmt)
                        {
                            if (kv.Value != req.Line) continue;
                            match = (type, kv.Key, kv.Value);
                            break;
                        }
                        if (match != null) break;
                    }
                    if (match != null) break;
                }
            }

            result.Add(match is { } m
                ? new DapResolvedBreakpoint(req.SourcePath, req.Line, true, m.Line, m.Type, m.Stmt)
                : new DapResolvedBreakpoint(req.SourcePath, req.Line, false, 0, null, -1));
        }
        return result;
    }
}
