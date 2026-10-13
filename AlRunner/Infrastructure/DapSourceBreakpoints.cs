namespace AlRunner.Infrastructure;

/// <summary>
/// The breakpoints a --dap session has installed, owned per SOURCE FILE. DAP's
/// <c>setBreakpoints</c> is the complete set for one source from then on, so a replacement
/// has to remove what that file armed before — not merely what the new request names
/// (#3526; the empty-list and moved-between-objects cases were first fixed in #3786's review).
/// AlDapSession keys breakpoints by runtime scope and does not know which file armed them;
/// this is where that is remembered. Not thread-safe: the DAP request loop is its only caller.
/// </summary>
internal sealed class DapSourceBreakpoints
{
    // Same comparer the resolver's own path index uses: two components disagreeing about
    // path identity is the bug the resolver's PathComparer doc describes.
    private readonly Dictionary<string, HashSet<System.Reflection.MemberInfo>> _scopesBySource =
        new(DapBreakpointResolver.PathComparer);

    /// <summary>The scopes <paramref name="fullSourcePath"/> currently has breakpoints in.</summary>
    internal IReadOnlyCollection<System.Reflection.MemberInfo> ScopesOf(string fullSourcePath) =>
        _scopesBySource.TryGetValue(fullSourcePath, out var s)
            ? s : Array.Empty<System.Reflection.MemberInfo>();

    /// <summary>
    /// Makes <paramref name="targets"/> the whole set for <paramref name="fullSourcePath"/>.
    /// Other sources' breakpoints are untouched. A scope reached for the first time here is
    /// also cleared before its first add, because a request naming a DIFFERENT file declaring
    /// the same object may have armed it.
    /// </summary>
    internal void Replace(string fullSourcePath,
        IReadOnlyCollection<(System.Reflection.MemberInfo ScopeType, int StatementIndex)> targets)
    {
        var nowRegistered = new HashSet<System.Reflection.MemberInfo>();
        foreach (var t in targets) nowRegistered.Add(t.ScopeType);
        var toClear = new HashSet<System.Reflection.MemberInfo>(nowRegistered);
        if (_scopesBySource.TryGetValue(fullSourcePath, out var previous)) toClear.UnionWith(previous);

        // One step, not Clear-then-Set: see AlDapSession.ReplaceBreakpoints.
        AlDapSession.ReplaceBreakpoints(toClear, targets);

        _scopesBySource[fullSourcePath] = nowRegistered;
    }
}
