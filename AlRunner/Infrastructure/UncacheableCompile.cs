// UncacheableCompile — a key whose compile publishes nothing is not worth waiting for (#5238).
//
// The locks (CompilePhase, CacheCompileLock) make later workers wait for the holder's compile and
// then read what it published. A compile that never publishes (an EMIT-EXCLUDED bundle, a failed
// compile, a dependency whose source compile throws) leaves them nothing to read: they would wait
// their turn and then compile the same thing, one after another, on every run. So such a compile
// gives the locks up as soon as it knows, and leaves `<key>.uncacheable` beside where the entry
// would be. A process that sees the marker compiles without waiting, as it did before the locks.
//
// The marker is a hint about cost, never about correctness: it only skips the locks. A later
// compile that does publish the key removes it, and a stale one costs the locks and nothing else.
// See docs/jobs-unit-claiming.md § Cold cache.

namespace AlRunner.Infrastructure;

internal sealed class UncacheableCompile : IDisposable
{
    public const string Suffix = ".uncacheable";

    private readonly string _markerPath;
    private readonly CacheCompileLock? _gate;
    private readonly CompilePhase? _phase;
    private bool _done;

    public UncacheableCompile(string markerPath, CacheCompileLock? gate, CompilePhase? phase)
    {
        _markerPath = markerPath;
        _gate = gate;
        _phase = phase;
    }

    public static string MarkerPath(string cacheDir, string key) => Path.Combine(cacheDir, key + Suffix);

    public static bool Exists(string markerPath) => File.Exists(markerPath);

    /// <summary>The entry was published: the marker, if an earlier compile left one, is stale.</summary>
    public void Published()
    {
        _done = true;
        try { File.Delete(_markerPath); } catch (Exception) { /* a stale hint only costs the locks */ }
    }

    /// <summary>This compile will publish nothing: say so and let the waiting workers go.
    /// Idempotent.</summary>
    public void GiveUp()
    {
        if (_done) return;
        _done = true;
        try { File.WriteAllBytes(_markerPath, Array.Empty<byte>()); }
        catch (Exception) { /* a missing hint only costs the next run its wait */ }
        _gate?.Dispose();
        _phase?.ReleaseUnproductive();
    }

    /// <summary>Whatever ended the compile without publishing (a failure, an exception) is a give-up.</summary>
    public void Dispose() => GiveUp();
}
