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
    /// How two source paths are compared. Case-insensitive on Windows and macOS, where the
    /// filesystem is, and case-SENSITIVE elsewhere (#3786 review): on Linux <c>Foo.al</c> and
    /// <c>foo.al</c> are two files, and folding them together merges their object lists so a
    /// line can bind to a statement in the wrong file. That was harmless while the index held
    /// one object per path and simply evicted; it stops being harmless once the entries merge.
    /// Exposed so the DAP loop's own per-source breakpoint bookkeeping keys the same way —
    /// two components disagreeing about path identity is the same bug wearing a different hat.
    /// </summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Resolves each request against every AL object type currently loaded that the source
    /// map knows. Only objects present in <paramref name="sourceMap"/> (built from the SAME
    /// bundle roots the run compiled, e.g. via AlCoverageSourceMap.Build) can match — a
    /// breakpoint in a file outside the debugged bundle is unverified, not a crash.
    /// <para>
    /// "Every object" is bounded by what that map registers: seven top-level kinds — table,
    /// page, report, codeunit, query, xmlport, enum — plus tableextension, pageextension and
    /// reportextension since #3833, which also taught AlCallStackCapture's prefix map to parse
    /// their emitted identity. What is still outside it, and so still never resolves a
    /// breakpoint, is the kinds that carry no executable code: an interface, a controladdin, a
    /// permissionset, an enum extension. BC emits no scope class for those (measured for
    /// enumextension in #3833), so there is nothing for a breakpoint to bind to.
    /// </para>
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
        // with PathComparer.
        var byPath = new Dictionary<string, List<(string Label, int Id)>>(PathComparer);
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
                // Every object the file declares, not just one.
                //
                // The first exact match wins, and that is a real limitation rather than a
                // proof of uniqueness (#3786 review). AL permits two statements on one
                // physical line — CollectStatementTable's doc comment says so explicitly, and
                // keeps them apart by id and column precisely because a line cannot — so a
                // requested line can have more than one executable target, in one scope or
                // across several. Binding one of them means a breakpoint on such a line stops
                // only if execution reaches the target that was picked. #3820 tracks
                // registering every match behind the single DAP breakpoint the protocol
                // returns; it needs DapResolvedBreakpoint to carry a set, so it is not a
                // widening of this change.
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
