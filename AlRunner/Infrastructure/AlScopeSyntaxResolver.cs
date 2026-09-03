// Maps a compiled scope class to its member's loop table and write sets (#2056), the
// same way AlCoverageTracker maps it to a file and scope name. Memoised per Type, misses
// included, so a StmtHit costs one dictionary lookup.
using System.Collections.Concurrent;

namespace AlRunner.Infrastructure;

/// <summary>What the hooks know about one scope class's source.</summary>
public sealed record AlScopeSyntax(AlLoopScopeTable Loops, AlWriteSetTable Writes, string FilePath, string ScopeName);

public static class AlScopeSyntaxResolver
{
    private static AlMemberSyntaxIndex? _index;
    private static IReadOnlyDictionary<(string Label, int Id), string>? _sourceMap;
    private static readonly ConcurrentDictionary<Type, AlScopeSyntax?> _scopes = new();

    /// <summary>Installs the request's index and file map and forgets earlier resolutions.</summary>
    public static void Configure(AlMemberSyntaxIndex index, IReadOnlyDictionary<(string Label, int Id), string> sourceMap)
    {
        _index = index;
        _sourceMap = sourceMap;
        _scopes.Clear();
    }

    /// <summary>Forgets the index, so a request without syntax-backed features resolves nothing.</summary>
    public static void Clear()
    {
        _index = null;
        _sourceMap = null;
        _scopes.Clear();
    }

    public static AlScopeSyntax? Resolve(Type scopeType)
    {
        if (_scopes.TryGetValue(scopeType, out var cached)) return cached;
        var resolved = ResolveUncached(scopeType);
        _scopes[scopeType] = resolved;
        return resolved;
    }

    private static AlScopeSyntax? ResolveUncached(Type scopeType)
    {
        var index = _index;
        var sourceMap = _sourceMap;
        if (index == null || sourceMap == null) return null;
        if (AlCoverageTracker.TryResolveScope(scopeType, sourceMap) is not { } scope) return null;

        var instrumented = AlCoverageInstrumentedStatements.Find(scopeType);
        if (instrumented.Count == 0) return null;
        // The first statement's position tells same-named triggers apart.
        int first = instrumented.Min();
        AlTextPosition? anchor = null;
        if (first >= 0 && first < scope.Spans.Length)
        {
            var (fromLine, fromColumn, _, _) = AlSourceSpanCodec.Decode(scope.Spans[first]);
            anchor = new AlTextPosition(fromLine, fromColumn);
        }
        var member = index.FindMember(scope.FilePath, scope.ScopeName, anchor);
        if (member == null) return null;

        return new AlScopeSyntax(
            AlLoopScopeTable.Build(member.Sites, scope.Spans, instrumented),
            AlWriteSetTable.Build(member.Writes, scope.Spans, instrumented),
            scope.FilePath,
            scope.ScopeName);
    }
}
