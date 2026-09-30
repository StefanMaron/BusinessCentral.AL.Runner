// --tdd across source bundles (#5037): `al-runner --tdd app test`, where the test bundle calls a
// member the app bundle does not declare yet. The app compiles separately, so the member is
// generated into the app's source IN MEMORY (TddSourceOverlay), and the cycle is re-run so the app
// is recompiled — its symbols, its workspace package and its own module — before the test bundle
// compiles again. Nothing is written to the watched tree; see TddGeneration.cs's header.
// Design notes: docs/tdd-mode.md#generating-into-another-source-bundle
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
    private static readonly Dictionary<string, (string Dir, string? AppJson)> Impls = new(StringComparer.OrdinalIgnoreCase);
    // Members generated into another bundle, with the module whose compile asked for them. Kept
    // across the re-run: on the re-run the dependent compiles clean and reports nothing itself.
    private static readonly List<(string DependentModule, TddGeneratedMember Member)> Generated = new();
    // (file, kind, member) keys whose generation was refused or rolled back — never retried in
    // this cycle, so a refused guess cannot re-run the cycle forever.
    private static readonly HashSet<string> Refused = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Attempted = new(StringComparer.Ordinal);
    private static bool _pending;

    public static void RegisterSourceImpl(string dir, string? appJsonPath)
    {
        lock (Sync) Impls[Path.GetFullPath(dir)] = (Path.GetFullPath(dir), appJsonPath);
    }

    internal static IReadOnlyList<(string Dir, string? AppJson)> SourceImpls()
    {
        lock (Sync) return Impls.Values.ToList();
    }

    /// <summary>A new --tdd cycle: generation starts from the files on disk again, so a member
    /// the developer has since written is never generated a second time.</summary>
    public static void ResetForNewCycle()
    {
        lock (Sync)
        {
            Generated.Clear();
            Refused.Clear();
            Attempted.Clear();
            _pending = false;
        }
        TddSourceOverlay.Clear();
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

    internal static void Refuse(string key)
    {
        lock (Sync) Refused.Add(key);
    }

    internal static void RecordGenerated(string dependentModule, TddGeneratedMember member)
    {
        lock (Sync)
        {
            Generated.Add((dependentModule, member));
            _pending = true;
        }
    }

    /// <summary>Every member generated into another bundle for <paramref name="dependentModule"/>
    /// this cycle.</summary>
    public static IReadOnlyList<TddGeneratedMember> GeneratedFor(string dependentModule)
    {
        lock (Sync)
            return Generated.Where(g => g.DependentModule == dependentModule).Select(g => g.Member).ToList();
    }

    /// <summary>True once per batch of new members: the caller re-runs the cycle so the bundle
    /// they were generated into is recompiled before the dependent compiles again.</summary>
    public static bool TakePendingRecompile()
    {
        lock (Sync)
        {
            var p = _pending;
            _pending = false;
            return p;
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
