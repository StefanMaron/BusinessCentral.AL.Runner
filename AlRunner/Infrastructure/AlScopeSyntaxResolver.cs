// AlScopeSyntaxResolver - one place that maps a compiled scope class to the syntax facts
// the capture hooks need (issue #2056): its loop table (AlIterationTracker) and its
// statement write sets (AlValueCapture). Both are derived from the same member of the
// same AL file, so resolving them together keeps the two consumers from ever
// disagreeing about which member a scope is.
//
// A scope Type maps to (file, member) exactly the way the statement table does
// (AlCoverageTracker.TryResolveScope: [SourceSpans] on the type, object label + id from
// the type name, file via AlCoverageSourceMap, member via [NavName]); AlMemberSyntaxIndex
// picks the member (disambiguating same-named triggers by the scope's first statement
// position) and AlLoopScopeTable.Build / AlWriteSetTable.Build classify the scope's
// instrumented ids. Memoised per Type INCLUDING misses (framework scopes, dependency apps
// outside the bundle) so the per-StmtHit cost is one dictionary lookup.
using System.Collections.Concurrent;

namespace AlRunner.Infrastructure;

/// <summary>Everything the hooks know about one compiled scope class's source.</summary>
public sealed record AlScopeSyntax(AlLoopScopeTable Loops, AlWriteSetTable Writes, string FilePath, string ScopeName);

public static class AlScopeSyntaxResolver
{
    private static AlMemberSyntaxIndex? _index;
    private static IReadOnlyDictionary<(string Label, int Id), string>? _sourceMap;
    private static readonly ConcurrentDictionary<Type, AlScopeSyntax?> _scopes = new();

    /// <summary>Installs the request's member index and file map (HandleServerExecute)
    /// and forgets every per-Type resolution from the previous request - a re-sent
    /// bundle is a new Assembly generation with new scope Types anyway.</summary>
    public static void Configure(AlMemberSyntaxIndex index, IReadOnlyDictionary<(string Label, int Id), string> sourceMap)
    {
        _index = index;
        _sourceMap = sourceMap;
        _scopes.Clear();
    }

    /// <summary>Forgets the index: a later request that did not ask for anything
    /// syntax-backed must not resolve against a previous request's bundle.</summary>
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
        // Any real statement's position identifies the member body among same-named
        // triggers; the lowest instrumented id is the body's first statement.
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
