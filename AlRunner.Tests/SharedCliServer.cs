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
///  (c) use bundle content unique enough (distinct app IDs / distinct
///      table+codeunit bodies) that one fact's compiled-and-cached AL output
///      cannot masquerade as another fact's expected-fresh compile — each
///      converted class in this repo satisfies this today because every
///      fixture bundle already carries its own fixed, distinct app ID.
///
/// ServerTests' shutdown-lifecycle fact and all of ServerCancelTests (each
/// fact independently exercises a fresh `cancel`/`runTests` race that #1809
/// deliberately de-serialized) are NOT converted — see #1804's PR description
/// for why.
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

    /// <summary>Returns the shared server, starting it on the first call only.</summary>
    public async Task<CliServer> GetAsync()
    {
        if (_server != null) return _server;
        await _gate.WaitAsync();
        try
        {
            if (_server == null)
            {
                _server = await CliServer.StartAsync();
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
