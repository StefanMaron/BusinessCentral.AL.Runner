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
    /// </summary>
    public static List<DapResolvedBreakpoint> Resolve(
        IReadOnlyList<DapBreakpointRequest> requests,
        IReadOnlyDictionary<(string Label, int Id), string> sourceMap)
    {
        // Invert (label,id)->path into full-path -> (label,id). Both sides are real
        // filesystem paths (not bare filenames — see docs/archive/dap.md's filename-only
        // caveat, which this improves on), compared case-insensitively for
        // cross-platform DAP clients.
        var byPath = new Dictionary<string, (string Label, int Id)>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in sourceMap)
            byPath[Path.GetFullPath(kv.Value)] = kv.Key;

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

                var instrumented = AlCoverageInstrumentedStatements.Find(t);
                var lineByStmt = new Dictionary<int, int>();
                foreach (var i in instrumented)
                {
                    if (i < 0 || i >= spans.Length) continue; // defensive: BC shape drift
                    lineByStmt[i] = AlSourceSpanCodec.AbsoluteFromLine(spans[i]);
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
            if (byPath.TryGetValue(full, out var objKey) && byObject.TryGetValue(objKey, out var scopes))
            {
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
            }

            result.Add(match is { } m
                ? new DapResolvedBreakpoint(req.SourcePath, req.Line, true, m.Line, m.Type, m.Stmt)
                : new DapResolvedBreakpoint(req.SourcePath, req.Line, false, 0, null, -1));
        }
        return result;
    }
}
