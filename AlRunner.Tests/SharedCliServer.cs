using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1804: one BC cold start per test CLASS, not one per <c>[Fact]</c>. Wraps a
/// single lazily-started <see cref="CliServer"/> so multiple facts in the same
/// class can share the one BC boot (measured ~4-7s, mostly BC metadata
/// construction inside BcRuntime.EnsureApplied — see the issue for the
/// measurements that ruled out shaving the cold start itself) instead of each
/// paying it independently via its own <c>CliServer.StartAsync</c> call.
///
/// Used as an xUnit <c>IClassFixture&lt;SharedCliServer&gt;</c>: xUnit constructs
/// exactly one instance per test class and passes the SAME instance to every
/// fact's constructor, then disposes it once after the last fact in the class
/// finishes. xUnit runs facts WITHIN one class sequentially by default (only
/// DIFFERENT classes/collections run in parallel with each other) — see #1809's
/// own reasoning for why the per-class collection split is what buys
/// cross-class parallelism, which this class deliberately does not touch. So
/// there is no concurrent-access race on <see cref="GetAsync"/> to guard beyond
/// the one-time startup.
///
/// Lazy, not eager: <see cref="InitializeAsync"/> does NOT spawn — a class whose
/// every fact skips (<c>TestArtifacts.SkipIfMissing()</c> runs before any fact
/// ever calls <see cref="GetAsync"/>) must not pay for a server nobody used.
/// The first <see cref="GetAsync"/> call spawns; every later call (from any
/// fact in the class) returns the SAME process.
///
/// Not a blanket replacement for <c>CliServer.StartAsync</c> everywhere. Safe to
/// share ONLY across facts that:
///  (a) don't need a DIFFERENT server-startup flag from each other — flags like
///      <c>--cache</c>/<c>--package-cache</c> are supplied at server STARTUP,
///      not per request (ServerProtocol's request shape exposes only
///      <c>command</c>, <c>sourcePaths</c>, <c>packagePaths</c>, <c>stubPaths</c>,
///      <c>code</c>, <c>captureValues</c>, <c>testIsolation</c> — no cache
///      override), so two facts wanting two different startup flags cannot
///      share one process;
///  (b) don't tear the process down (shutdown/kill) as part of what they're
///      proving — a fact that shuts the shared server down would break every
///      fact that runs after it in the same class;
///  (c) give each bundle-generating call site its OWN app ID, distinct from
///      every other call site sharing this server — not merely "distinct
///      content". <c>DependencyLoader.TryGetByAppId</c> (see
///      DependencyLoader.cs) caches a compiled module by AppId for the
///      lifetime of the SERVER PROCESS and returns that cached module for
///      ANY later request whose bundle reports a MATCHING AppId at a
///      DIFFERENT SourcePath, regardless of whether the bundle's actual
///      source content differs — it is not the AL-output content cache, and
///      content equality does not make a shared AppId safe, only
///      coincidentally harmless until someone edits one call site's AL
///      without also editing the others. (The one exception:
///      <c>TryGetByAppId</c> deliberately does NOT reuse when the cached
///      entry's SourcePath equals the one being asked about — that is one
///      fact re-running the SAME bundle path more than once, e.g.
///      ServerTests' edit-and-rerun test, which is fine.) Every converted
///      class's bundle generator in this repo now takes a variant/index
///      parameter for exactly this reason — see ServerTestIsolationTests and
///      ServerStreamingTests. SharedCliServerTests deliberately does the
///      opposite on purpose, reusing one fixed AppId across two calls, to
///      prove isolation holds even when this cross-request reuse fires.
///
/// ServerTests' shutdown-lifecycle fact is NOT converted (it tears the process
/// down as part of what it proves). #1804/#1913 originally also excluded ALL of
/// ServerCancelTests on the same blanket rationale, but #1936 found that only
/// one of its seven facts — the one that starts its server with a fact-specific
/// <c>AL_RUNNER_TEST_BARRIER_DIR</c> env var to deliberately block it mid-run —
/// actually needs a dedicated process; the other six neither block nor kill the
/// server and now share one via this fixture. See ServerCancelTests' own class
/// doc comment for the per-fact breakdown, and PhaseLogServerKillTests (SIGKILLs
/// its server) for another class that genuinely cannot share.
/// </summary>
public sealed class SharedCliServer : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CliServer? _server;

    /// <summary>
    /// How many times THIS fixture instance has actually spawned a server
    /// process — 0 or 1, never more. Instance-scoped rather than reading
    /// <see cref="CliServer.StartCount"/>'s process-wide count, specifically
    /// so the proving test in SharedCliServerTests.cs is immune to other test
    /// classes spawning their own servers concurrently (xUnit runs different
    /// classes' tests in parallel with each other by default).
    /// </summary>
    public int SpawnCount => _spawnCount;
    private int _spawnCount;

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Returns the shared server, starting it on the first call only.
    /// <paramref name="extraArgs"/> is only consulted on that first (spawning)
    /// call — per rule (a) above, every fact sharing this fixture must want the
    /// SAME startup flags, so a caller passes a fixed, non-per-test value here
    /// (e.g. ServerCancelTests' <c>ExtraServerArgs()</c>, which always resolves
    /// to the same optional <c>--package-cache</c> pointer regardless of which
    /// fact calls it) — never one that varies by test, which would silently be
    /// ignored on every call after the first.
    /// </summary>
    public async Task<CliServer> GetAsync(IEnumerable<string>? extraArgs = null)
    {
        if (_server != null) return _server;
        await _gate.WaitAsync();
        try
        {
            if (_server == null)
            {
                _server = await CliServer.StartAsync(extraArgs);
                _spawnCount++;
            }
            return _server;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisposeAsync()
    {
        if (_server != null)
            await _server.DisposeAsync();
    }
}
