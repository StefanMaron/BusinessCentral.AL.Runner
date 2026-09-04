namespace AlRunner.Infrastructure;

/// <summary>
/// Issue #2669. One <see cref="BcCompiler"/> INSTANCE per source-dependency directory, kept
/// warm across repeated dependency-symbol synthesis calls within the SAME process
/// (<c>--watch</c>, <c>--server</c>).
///
/// A RAD incremental baseline (<c>BcCompiler.Incremental.cs</c>'s <c>_radBaselines</c>) lives on
/// the compiler INSTANCE, not anywhere process-wide. Before this fix, every call site that
/// synthesizes a dependency's <c>*.symbols.json</c> — <c>RunLayeredPrePass</c> and
/// <c>BuildSiblingSourceDeps</c> in Program.cs — constructed a brand new <c>BcCompiler()</c> on
/// every call, so nothing was ever warm: the SECOND-and-later re-synthesis of an unchanged (or
/// barely-changed) dependency app paid the exact same whole-module compile as the FIRST.
/// Measured: 22.25s to re-synthesize Pageworks's symbols after adding one no-op procedure, under
/// <c>--server</c>, on the second of two <c>runTests</c> requests in the same warm process.
///
/// Keyed by the dependency's own source directory rather than by AppId/moduleName/version,
/// because the directory is the only stable identity available BEFORE app.json is (re-)read on
/// this call — the caller has to look the directory up to learn those. This is a looser key than
/// "the app's true identity", but that costs nothing beyond an occasional avoidable fallback,
/// never a wrong answer: <c>TryEmitIncremental</c>'s own <c>ManifestFingerprint</c> /
/// <c>SharedRefsFingerprint</c> checks (see <c>BcCompiler.Incremental.cs</c>'s header) already
/// refuse to reuse a baseline the moment the app's identity, version, resolved dependency set, or
/// manifest-derived compiler inputs genuinely changed underneath this path — they just force a
/// fresh full compile for that one cycle instead of silently answering wrong.
///
/// Deliberately never evicted, for the same reason <c>RunLayeredPrePass</c>'s own
/// <c>workspace-deps</c> cache directory list is never pruned (see its #1821 comment): a long
/// server session holds at most one compiler per DISTINCT dependency app actually touched in that
/// session, each proportional to that one app's own compiled surface — not to how many times it
/// was re-synthesized, and not to the corpus's overall size.
/// </summary>
internal static class DepSymbolCompilerCache
{
    private static readonly Dictionary<string, BcCompiler> _byDir = new(StringComparer.OrdinalIgnoreCase);

    public static BcCompiler GetOrCreate(string dir)
    {
        var key = Path.GetFullPath(dir);
        if (!_byDir.TryGetValue(key, out var compiler))
            _byDir[key] = compiler = new BcCompiler();
        return compiler;
    }
}
