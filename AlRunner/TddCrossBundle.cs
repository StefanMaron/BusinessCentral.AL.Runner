// --tdd across source bundles (#5037): `al-runner --tdd app test`, where the test bundle calls a
// member the app bundle does not declare yet. The app compiles separately, so the member is
// generated into the app's source IN MEMORY (TddSourceOverlay), and the cycle is re-run so the app
// is recompiled — its symbols, its workspace package and its own module — before the test bundle
// compiles again. Nothing is written to the watched tree; see TddGeneration.cs's header.
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner;

/// <summary>
/// Process-wide source text that replaces a file's on-disk content for every AL source reader
/// (the compiler, the dependency-symbol compile, the RAD incremental path, the workspace-package
/// key and packager, the table-metadata parser). Empty outside a --tdd run that generated into
/// another bundle, and then every read is the plain file read.
/// </summary>
public static class TddSourceOverlay
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal);
    private static volatile int _count;

    public static bool IsEmpty => _count == 0;

    public static bool TryGet(string path, out string text)
    {
        text = "";
        if (_count == 0) return false;
        var full = Path.GetFullPath(path);
        lock (Sync)
        {
            if (!Texts.TryGetValue(full, out var t)) return false;
            text = t;
            return true;
        }
    }

    /// <summary><see cref="File.ReadAllText(string)"/>, or the overlay text for this path.</summary>
    public static string ReadAllText(string path)
        => TryGet(path, out var t) ? t : File.ReadAllText(path);

    /// <summary>The bytes a reader hashing or packaging the file must see.</summary>
    public static byte[] ReadAllBytes(string path)
        => TryGet(path, out var t) ? System.Text.Encoding.UTF8.GetBytes(t) : File.ReadAllBytes(path);

    public static void Set(string path, string text)
    {
        lock (Sync)
        {
            Texts[Path.GetFullPath(path)] = text;
            _count = Texts.Count;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Texts.Clear();
            _count = 0;
        }
    }
}

/// <summary>
/// The source bundles of this run that another bundle depends on, and what --tdd generated into
/// them. Registered by the layered pre-pass (<c>RunLayeredPrePass</c>); a precompiled dependency
/// is never registered, so it stays out of scope (#5037's precompiled half).
/// </summary>
public static class TddCrossBundle
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, (string Dir, string? AppJson, Guid AppId)> Impls = new(StringComparer.OrdinalIgnoreCase);
    // #5264: the call edges (callee key -> caller keys) of every source bundle another bundle depends on,
    // by TddCallGraph.ProcKey, filled when that bundle compiles. A subscriber in a LATER bundle to one
    // of its publishers is reached by whatever raises the publisher, which only these edges say.
    private static readonly Dictionary<string, HashSet<string>> KeyCallers = new(StringComparer.Ordinal);
    private static readonly HashSet<Guid> KeyGraphApps = new();
    private static readonly HashSet<string> RunBundles = new(StringComparer.OrdinalIgnoreCase);
    private static bool? _anySubscriber;
    // #5266: codeunits of source bundles this run compiled and dropped, by id. Not per bundle: a library
    // dropped in its own bundle iteration is still missing when a later bundle's test calls it.
    private static readonly Dictionary<int, DroppedCodeunit> Dropped = new();

    /// <summary>A codeunit a source bundle's compile dropped: the bundle, the codeunit's name and the
    /// first AL diagnostic that identified it.</summary>
    internal sealed record DroppedCodeunit(string App, string Name, string Diagnostic);

    internal static void RegisterDroppedCodeunit(int id, DroppedCodeunit dropped)
    {
        lock (Sync) Dropped[id] = dropped;
    }

    // #5271: objects a DEPENDENCY LOAD dropped under --tdd, by display name, so the closing line does not say
    // "no test referenced a missing symbol" when the library that referenced it was compiled only there.
    private static readonly HashSet<string> DependencyDropped = new(StringComparer.Ordinal);

    internal static void NoteDependencyDropped(IEnumerable<string> objectDisplayNames)
    {
        lock (Sync) foreach (var n in objectDisplayNames) DependencyDropped.Add(n);
    }

    internal static IReadOnlyList<string> DependencyDroppedNames()
    {
        lock (Sync) return DependencyDropped.ToList();
    }

    internal static DroppedCodeunit? DroppedCodeunitById(int id)
    {
        lock (Sync) return Dropped.TryGetValue(id, out var d) ? d : null;
    }
    // Members generated into another bundle, with the module whose compile asked for them. Kept
    // across the re-run: on the re-run the dependent compiles clean and reports nothing itself.
    private static readonly List<(string DependentModule, TddGeneratedMember Member)> Generated = new();
    // #5271: members generated into the compile that declares them, in a bundle another bundle depends on.
    private static readonly List<TddGeneratedMember> GeneratedHere = new();
    // (file, kind, member) keys never tried again in this cycle, so a refused guess cannot re-run the
    // cycle forever: every key tried (Attempted), and the ones rolled back after a failed recompile (Refused).
    private static readonly HashSet<string> Refused = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Attempted = new(StringComparer.Ordinal);
    // #5161: procedures of a bundle that reach a member generated into ANOTHER bundle, by
    // TddCallGraph.ProcKey. A bundle compiled later (a test bundle calling a test library, which
    // calls the generated member) is not the compile that held the missing call, so it reads this to
    // name the stub on the tests that reach it. Kept across the re-run, like Generated.
    private static readonly Dictionary<string, List<TddGeneratedMember>> Reaching = new(StringComparer.Ordinal);
    private static bool _pending;
    // The members of the batch waiting for the recompile (#5265): when the re-run limit stops that
    // recompile, these are the members generated and never compiled in.
    private static readonly List<TddGeneratedMember> PendingMembers = new();

    public static void RegisterSourceImpl(string dir, string? appJsonPath, Guid appId = default)
    {
        lock (Sync) Impls[Path.GetFullPath(dir)] = (Path.GetFullPath(dir), appJsonPath, appId);
    }

    /// <summary>Every bundle of a multi-bundle run, impl or not (#5264): the dirs whose text says whether
    /// anything in the run subscribes to an event, which decides whether call edges are worth recording.</summary>
    public static void RegisterRunBundle(string dir)
    {
        lock (Sync)
        {
            RunBundles.Add(Path.GetFullPath(dir));
            _anySubscriber = null;
        }
    }

    /// <summary>True when a bundle of this run declares an <c>[EventSubscriber]</c>: the only way a
    /// bundle's call edges are ever read (<see cref="CallersOf"/>). A text probe, so it may say yes for
    /// a subscriber behind a disabled <c>#if</c>, never no for a real one. Computed once per cycle.</summary>
    internal static bool AnyBundleSubscribes()
    {
        List<string> dirs;
        lock (Sync)
        {
            if (_anySubscriber is { } known) return known;
            dirs = RunBundles.ToList();
        }
        var found = false;
        foreach (var dir in dirs)
        {
            foreach (var file in AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.al"))
            {
                try
                {
                    if (TddSourceOverlay.ReadAllText(file).Contains("EventSubscriber", StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                catch (IOException) { found = true; break; } // unreadable: assume yes, never lose an edge
            }
            if (found) break;
        }
        lock (Sync) _anySubscriber = found;
        return found;
    }

    /// <summary>The app of a source bundle another bundle depends on, when this run has a subscriber for
    /// its call edges to serve (#5264).</summary>
    internal static bool WantsKeyGraph(Guid appId) => IsSourceImpl(appId) && AnyBundleSubscribes();

    /// <summary>True for the app of a source bundle another bundle of this run depends on.</summary>
    internal static bool IsSourceImpl(Guid appId)
    {
        lock (Sync) return appId != Guid.Empty && Impls.Values.Any(i => i.AppId == appId);
    }

    /// <summary>True when that bundle has compiled in this cycle and left its call edges
    /// (<see cref="RecordKeyGraph"/>). A compiled-deps cache hit compiles nothing, so
    /// <c>DependencyLoader</c> recompiles such a bundle while this is false.</summary>
    internal static bool HasKeyGraph(Guid appId)
    {
        lock (Sync) return KeyGraphApps.Contains(appId);
    }

    internal static void RecordKeyGraph(Guid appId, IEnumerable<(string Callee, string Caller)> edges)
    {
        lock (Sync)
        {
            KeyGraphApps.Add(appId);
            foreach (var (callee, caller) in edges)
            {
                if (!KeyCallers.TryGetValue(callee, out var set)) KeyCallers[callee] = set = new(StringComparer.Ordinal);
                set.Add(caller);
            }
        }
    }

    /// <summary><paramref name="key"/> and every procedure key that, through the recorded bundles'
    /// call edges, transitively calls it. Keys are object and procedure name only (the
    /// <see cref="TddCallGraph.ProcKey"/> over-approximation).</summary>
    internal static IReadOnlyList<string> CallersOf(string key)
    {
        lock (Sync)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { key };
            var order = new List<string> { key };
            for (var i = 0; i < order.Count; i++)
                if (KeyCallers.TryGetValue(order[i], out var callers))
                    foreach (var c in callers)
                        if (seen.Add(c)) order.Add(c);
            return order;
        }
    }

    /// <summary>--server (#5034): each request registers its own bundles; one from an earlier
    /// request is not a bundle this request compiles, so nothing may be generated into it.</summary>
    public static void ClearSourceImpls()
    {
        lock (Sync)
        {
            Impls.Clear();
            RunBundles.Clear();
            Dropped.Clear();
            DependencyDropped.Clear();
            _anySubscriber = null;
            KeyCallers.Clear();
            KeyGraphApps.Clear();
        }
    }

    internal static IReadOnlyList<(string Dir, string? AppJson)> SourceImpls()
    {
        lock (Sync) return Impls.Values.Select(i => (i.Dir, i.AppJson)).ToList();
    }

    /// <summary>A new --tdd cycle: generation starts from the files on disk again, so a member
    /// the developer has since written is never generated a second time.</summary>
    public static void ResetForNewCycle()
    {
        lock (Sync)
        {
            Generated.Clear();
            GeneratedHere.Clear();
            PendingMembers.Clear();
            Refused.Clear();
            Attempted.Clear();
            Reaching.Clear();
            KeyCallers.Clear();
            KeyGraphApps.Clear();
            Dropped.Clear();
            DependencyDropped.Clear();
            _anySubscriber = null;
            _pending = false;
        }
        TddSourceOverlay.Clear();
    }

    internal static void RecordReaching(IEnumerable<string> procKeys, TddGeneratedMember member)
    {
        lock (Sync)
            foreach (var key in procKeys)
            {
                if (!Reaching.TryGetValue(key, out var list)) Reaching[key] = list = new();
                if (!list.Contains(member)) list.Add(member);
            }
    }

    internal static bool HasReaching
    {
        get { lock (Sync) return Reaching.Count > 0; }
    }

    private static IReadOnlyList<TddGeneratedMember> ReachingMembers(string procKey)
    {
        lock (Sync) return Reaching.TryGetValue(procKey, out var list) ? list.ToList() : Array.Empty<TddGeneratedMember>();
    }

    /// <summary>
    /// A clean compile of <paramref name="moduleName"/> after --tdd generated into another bundle
    /// (#5161): the [Test]s of it that reach a generated member through another bundle's procedures,
    /// as one member per generated member with those tests as its dependents. Its procedures that do
    /// are recorded too, so a bundle compiled after this one is followed through it.
    /// </summary>
    internal static IReadOnlyList<TddGeneratedMember> ReachThroughDependencies(TddCallGraph graph)
    {
        var (procedures, tests) = graph.ReachThroughDependencies(ReachingMembers);
        foreach (var group in procedures.GroupBy(p => p.Member))
            RecordReaching(group.Select(p => p.Key), group.Key);
        return tests.GroupBy(t => t.Member)
            .Select(g => g.Key with { DependentTests = g.Select(t => t.TestLabel).Distinct().ToList() })
            .ToList();
    }

    internal static bool TryBeginAttempt(string key)
    {
        lock (Sync)
        {
            if (Refused.Contains(key) || Attempted.Contains(key)) return false;
            Attempted.Add(key);
            return true;
        }
    }

    internal static void RecordGenerated(string dependentModule, TddGeneratedMember member)
    {
        lock (Sync)
        {
            Generated.Add((dependentModule, member));
            PendingMembers.Add(member);
            _pending = true;
        }
    }

    /// <summary>A member generated into the compile of a bundle other bundles depend on, that compile
    /// being the one that declares it (#5271). Nothing is pending: the compile holds it already. Listed
    /// by <see cref="AllGenerated"/>, because a bundle compiled only as a dependency of another one
    /// reports nothing through its own bundle iteration.</summary>
    internal static void RecordGeneratedHere(TddGeneratedMember member)
    {
        lock (Sync) GeneratedHere.Add(member);
    }

    /// <summary>Every member generated into another bundle for <paramref name="dependentModule"/>
    /// this cycle.</summary>
    public static IReadOnlyList<TddGeneratedMember> GeneratedFor(string dependentModule)
    {
        lock (Sync)
            return Generated.Where(g => g.DependentModule == dependentModule).Select(g => g.Member).ToList();
    }

    /// <summary>Every member generated into another bundle this cycle, whichever module's compile asked
    /// for it. A bundle run only as a dependency of another one (its module reused, not compiled again)
    /// never reports its members through <see cref="GeneratedFor"/>.</summary>
    internal static IReadOnlyList<TddGeneratedMember> AllGenerated()
    {
        lock (Sync) return Generated.Select(g => g.Member).Concat(GeneratedHere).ToList();
    }

    /// <summary>True while members generated into another bundle wait for that bundle's recompile.
    /// Reads without taking: <see cref="TakePendingRecompile"/> stays the caller's one decision.</summary>
    public static bool HasPendingRecompile()
    {
        lock (Sync) return _pending;
    }

    /// <summary>True once per batch of new members: the caller re-runs the cycle so the bundle
    /// they were generated into is recompiled before the dependent compiles again.</summary>
    public static bool TakePendingRecompile()
    {
        lock (Sync)
        {
            var p = _pending;
            _pending = false;
            PendingMembers.Clear();
            return p;
        }
    }

    /// <summary>The re-run limit stopped the recompile that members generated into another bundle wait
    /// for (#5265): they were never compiled in, so they are taken out of <see cref="Generated"/> (no
    /// list names them as generated) and returned for the run to report. Empty when nothing waits.</summary>
    public static IReadOnlyList<TddGeneratedMember> AbandonPending()
    {
        lock (Sync)
        {
            var batch = PendingMembers.ToList();
            PendingMembers.Clear();
            _pending = false;
            Generated.RemoveAll(g => batch.Any(b => TddReport.Describe(b) == TddReport.Describe(g.Member)));
            return batch;
        }
    }

    /// <summary>The recompile of the generated-into bundle failed: drop every member generated in
    /// the batch that caused it and never try them again this cycle, so the dependent's tests fall
    /// through to the refuse path (reported FAILED naming the missing symbol).</summary>
    public static void RollBackPending(IEnumerable<string> keys)
    {
        lock (Sync)
        {
            foreach (var k in keys) Refused.Add(k);
            Generated.Clear();
            PendingMembers.Clear();
            Reaching.Clear();
        }
        TddSourceOverlay.Clear();
    }

    internal static IReadOnlyList<string> AttemptedKeys()
    {
        lock (Sync) return Attempted.ToList();
    }

    /// <summary>
    /// The one registered source file declaring an object of <paramref name="kind"/> named
    /// <paramref name="objectName"/>, parsed from its overlay-aware text. Null when none or more
    /// than one does — an ambiguous target is refused, never guessed.
    /// </summary>
    internal static (string FilePath, string? AppJson, NavSyntax.SyntaxTree Tree)? FindObject(
        Type syntaxKind, string objectName)
    {
        (string, string?, NavSyntax.SyntaxTree)? found = null;
        foreach (var (dir, appJson) in SourceImpls())
        {
            var parseOpts = BcCompiler.BuildParseOptions(BcCompiler.ReadManifestCompilerInputs(appJson));
            foreach (var file in AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.al"))
            {
                string text;
                try { text = TddSourceOverlay.ReadAllText(file); }
                catch (IOException) { continue; }
                // Cheap pre-filter before a parse: the name must appear in the file at all.
                if (text.IndexOf(objectName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var tree = NavSyntax.SyntaxTree.ParseObjectText(text, path: file, encoding: null!, parseOpts, default);
                if (tree.GetRoot() is not NavSyntax.CompilationUnitSyntax root) continue;
                foreach (var obj in root.Objects)
                {
                    if (!syntaxKind.IsInstanceOfType(obj)) continue;
                    if (!string.Equals(TddGeneration.ObjectNameOf(obj), objectName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (found != null) return null;
                    found = (file, appJson, tree);
                }
            }
        }
        return found;
    }
}
