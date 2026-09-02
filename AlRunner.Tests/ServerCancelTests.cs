using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1641 (`cancel` command slice): --server's `cancel` side channel and the
/// `cancelled`/`ack` fields it lights up on the protocol-v2 wire.
///
/// Wire shapes follow v1 verbatim (PRs #1613/#1614, closed on the v1 architecture
/// but ported here per the issue's own instructions): the ack is
/// <c>{"type":"ack","command":"cancel","noop":bool}</c>, and the terminal
/// `summary` line carries <c>cancelled:true</c> only when the cancel actually
/// stopped the run early.
///
/// The no-active-run tests need no BC compile at all (cancel is answered
/// instantly regardless of whether anything has ever been compiled) and always
/// run. The tests that need a real run in flight need the BC artifact caches;
/// they report Skipped (not Passed) when absent — see TestArtifacts, same convention
/// as CacheKeyDependencyClosureTests.
///
/// See DefineFlagIntegrationTests for why this class used to be
/// [Collection("server-serial")] and no longer is — #1809. The one genuine
/// concurrency hazard this class ever exposed — a TOCTOU race in
/// HandleServerRunTests clearing activeRunCts too late relative to when a client
/// could observe the summary and fire `cancel` — is fixed at the source (see the
/// #1809 comment in Program.cs's HandleServerRunTests) rather than papered over
/// by serializing the tests that happened to be likely to notice it.
///
/// #1936: seven facts here each independently paid a cold `--server` boot
/// (~30-40s under CI contention; a warm request is ~1.6s), for 287s total in
/// CI. #1913/#1804 explicitly declined to share a server across THIS class's
/// facts ("they tear the process down or race it as part of what they
/// prove") — true for exactly one of the seven, not all: only
/// <see cref="RunTests_CancelDuringRun_StopsEarly_AckNoopFalse_SummaryCancelledTrue"/>
/// starts its server with a fact-specific env var
/// (<c>AL_RUNNER_TEST_BARRIER_DIR</c>) to deliberately block it mid-run — a
/// DIFFERENT startup flag per SharedCliServer rule (a), so it cannot share and
/// keeps its own dedicated <c>CliServer.StartAsync</c> call, unmodified. Every
/// other fact here neither blocks nor kills the server and wants the exact
/// same (optional) startup flags every time (<see cref="ExtraServerArgs"/>
/// always resolves to the same value), so they now share ONE server via
/// <see cref="SharedCliServer"/> — facts within a class already run
/// sequentially by default (see that fixture's own doc comment), so this
/// changes nothing about ordering, only about how many times BC cold-boots.
/// Each shared-server bundle generator call site got its own AppId/variant
/// (SharedCliServer doc condition (c)) even though the bundle CONTENT is
/// identical across variants, to route every call through a genuine fresh
/// compile rather than <c>DependencyLoader.TryGetByAppId</c>'s cross-path
/// module-identity cache.
/// </summary>
public class ServerCancelTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _shared;

    public ServerCancelTests(SharedCliServer shared) => _shared = shared;

    private static string[] ExtraServerArgs()
    {
        var platformApps = TestArtifacts.PlatformAppsDir();
        return Directory.Exists(platformApps)
            ? new[] { "--package-cache", platformApps }
            : Array.Empty<string>();
    }

    /// <summary>
    /// One trivial passing test — enough to prove "a run happened and finished",
    /// nothing more. Used by the tests that don't care about run duration.
    ///
    /// <paramref name="variant"/> (#1936): three call sites now share ONE server
    /// process (see class doc comment) — each gets its own AppId (last hex
    /// digit) and object-ID range (offset by variant*10 from the base 60310),
    /// same convention as ServerTestIsolationTests' MakeIsolationBundle, so
    /// every call routes through a genuine fresh compile instead of
    /// DependencyLoader.TryGetByAppId's cross-path module-identity cache.
    /// </summary>
    private static string MakeFastBundle(int variant)
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-server-cancel-fast", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var baseId = 60310 + variant * 10;
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "d1b2c3d4-e5f6-4708-a9ba-cbdcedfe0f3{{variant:x1}}",
          "name": "Runner Extras - Server Cancel Fast Probe {{variant}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "FastProbe.Codeunit.al"), $$"""
        codeunit {{baseId}} "Server Cancel Fast Probe SX {{variant}}"
        {
            Subtype = Test;

            [Test]
            procedure OnlyTest()
            begin
            end;
        }
        """);
        return dir;
    }

    /// <summary>
    /// <paramref name="testCount"/> trivial (non-spinning) [Test] methods — no CPU
    /// workload of any kind. #1845: the previous incarnation of this fixture
    /// (MakeSlowishBundle, removed here) sized a CPU-bound spin loop against a
    /// live-measured cancel round trip (#1785 then #1798) to manufacture a
    /// wall-clock window for the cancel to land in. That approach was BOTH still
    /// flaky (the calibration and the destructive run are two separate phases; a
    /// noisy shared runner can shift contention between them) AND, per CI's own
    /// TRX occupancy report, the single most expensive class in the whole unit
    /// suite (379.5s of a 408.1s four-thread floor) because "make the workload
    /// scale with measured contention" means more contention makes the suite
    /// slower, not just less flaky.
    ///
    /// RunTests_CancelDuringRun_* no longer needs any of that: it now blocks the
    /// server itself (via <see cref="AlRunner.Infrastructure.TestBarrier"/>, a
    /// filesystem-polling side channel wholly separate from the stdin/stdout
    /// protocol under test) right after the first test's completion event, so the
    /// in-flight window is unbounded and a property of the run's own state, not
    /// of CPU seconds burned. This bundle only needs enough tests (testCount,
    /// default 2) that "the run did not simply finish everything" is a real,
    /// checkable claim — sized by test COUNT, never by a multiple of any
    /// measurement.
    /// </summary>
    private static string MakeBarrierBundle(int testCount = 2)
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-server-cancel-barrier", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "e1b2c3d4-e5f6-4708-a9ba-cbdcedfe0f44",
          "name": "Runner Extras - Server Cancel Barrier Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60320, "to": 60329 } ],
          "runtime": "14.0"
        }
        """);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("codeunit 60320 \"Server Cancel Barrier SX\"");
        sb.AppendLine("{");
        sb.AppendLine("    Subtype = Test;");
        sb.AppendLine();
        for (var i = 1; i <= testCount; i++)
        {
            sb.AppendLine("    [Test]");
            sb.AppendLine($"    procedure Test{i:D2}()");
            sb.AppendLine("    begin");
            sb.AppendLine("    end;");
            sb.AppendLine();
        }
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(dir, "BarrierProbe.Codeunit.al"), sb.ToString());
        return dir;
    }

    private static string RunTestsReq(string bundleDir)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { bundleDir },
            packagePaths = Array.Empty<string>(),
        });

    // ------------------------------------------------------------------
    // Negative direction: cancel with nothing (or nothing ANYMORE) to
    // cancel. No BC compile needed — the server answers cancel before ever
    // looking at sourcePaths — so these always run, artifacts or not.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cancel_NoActiveRequest_AcksAsNoop()
    {
        var server = await _shared.GetAsync(ExtraServerArgs());
        var response = await server.SendAsync("{\"command\":\"cancel\"}");
        var doc = JsonDocument.Parse(response).RootElement;
        Assert.Equal("ack", doc.GetProperty("type").GetString());
        Assert.Equal("cancel", doc.GetProperty("command").GetString());
        Assert.True(doc.GetProperty("noop").GetBoolean());
    }

    [Fact]
    public async Task Cancel_TwiceWithoutActiveRequest_BothNoop()
    {
        var server = await _shared.GetAsync(ExtraServerArgs());
        var r1 = JsonDocument.Parse(await server.SendAsync("{\"command\":\"cancel\"}")).RootElement;
        var r2 = JsonDocument.Parse(await server.SendAsync("{\"command\":\"cancel\"}")).RootElement;
        Assert.True(r1.GetProperty("noop").GetBoolean());
        Assert.True(r2.GetProperty("noop").GetBoolean());
    }

    [Fact]
    public async Task Cancel_WithUnknownExtraFields_StillAcks()
    {
        // Forward-compat: a future protocol addition may put more fields on the
        // cancel request; the server must tolerate and still answer with the ack shape.
        var server = await _shared.GetAsync(ExtraServerArgs());
        var response = await server.SendAsync(
            "{\"command\":\"cancel\",\"reason\":\"user clicked stop\",\"requestId\":42}");
        var doc = JsonDocument.Parse(response).RootElement;
        Assert.Equal("ack", doc.GetProperty("type").GetString());
        Assert.Equal("cancel", doc.GetProperty("command").GetString());
        Assert.True(doc.GetProperty("noop").GetBoolean());
    }

    [SkippableFact]
    public async Task Cancel_AfterRunTestsCompletes_IsNoop()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = MakeFastBundle(variant: 0);
        var server = await _shared.GetAsync(ExtraServerArgs());

        // The single-test bundle finishes and returns its summary before we ever
        // send cancel — by construction (SendRequestStreamingAsync only returns
        // after the summary line), so activeRunCts is already cleared.
        var lines = await server.SendRequestStreamingAsync(RunTestsReq(bundle));
        Assert.Equal("summary", JsonDocument.Parse(lines[^1]).RootElement.GetProperty("type").GetString());

        var cancelResponse = JsonDocument.Parse(await server.SendAsync("{\"command\":\"cancel\"}")).RootElement;
        Assert.Equal("ack", cancelResponse.GetProperty("type").GetString());
        Assert.True(cancelResponse.GetProperty("noop").GetBoolean(),
            "cancel sent after the run's own summary line arrived must be a noop " +
            "— there is nothing left to cancel.");
    }

    /// <summary>
    /// #1809: HandleServerRunTests used to clear <c>activeRunCts</c> in its `finally`
    /// block — which runs AFTER the summary line is already written+flushed to the
    /// client. That left a real (if narrow) TOCTOU window: a client that reads the
    /// summary and immediately sends `cancel` could land inside the gap between "the
    /// summary is on the wire" and "the server thread has actually reached `finally`",
    /// and would observe the stale non-null cts — the same cts the ack path in
    /// HandleSideChannelCommand answers against — and get back noop:false for a run
    /// that had already finished. Fixed by clearing activeRunCts BEFORE the write, so
    /// there is no interleaving in which the client can observe the summary while the
    /// clear is still pending: program order on the single server thread guarantees it.
    ///
    /// One iteration already proves the ordering (the test right above this one). This
    /// repeats it against ONE reused server (no repeated cold BC boot — the AL-output
    /// cache also makes every iteration after the first a compile cache HIT) so a
    /// regression that reopens the window — e.g. a future refactor that moves work back
    /// between the write and the clear — has more than one roll of the dice to be
    /// caught by ordinary OS scheduling jitter, without resorting to artificial
    /// CPU/memory pressure (tried and discarded: on a contended shared box it just
    /// produces protocol timeouts, or gets a worker OOM-killed by something else
    /// entirely — noise indistinguishable from a real failure, not signal).
    ///
    /// Iteration count is 5, not the 20 first tried during development: each
    /// runTests+cancel round trip costs real wall clock even on a cache hit (server
    /// dispatch, JSON framing, process scheduling), measured at roughly 10s/iteration
    /// against this repo's shared dev box — 20 iterations cost ~4-5 minutes, which is
    /// indefensible inside a PR whose whole point is cutting CI time. 5 iterations
    /// (~1-2 minutes here, cheaper again on an uncontended CI runner) keeps the
    /// jitter-exposure argument above while keeping the tax small.
    /// </summary>
    [SkippableFact]
    public async Task Cancel_AfterRunTestsCompletes_IsNoop_RepeatedAcrossManyRuns()
    {
        TestArtifacts.SkipIfMissing();

        const int iterationCount = 5; // see this test's own doc comment for why 5, not 20.
        var bundle = MakeFastBundle(variant: 1);
        var server = await _shared.GetAsync(ExtraServerArgs());
        var failures = new List<string>();

        for (var i = 0; i < iterationCount; i++)
        {
            // 120s, not 30s: this test proves ordering (a correctness claim), not
            // latency, and a tight per-call timeout makes the test flaky under
            // ordinary shared-box CPU contention for a reason that has nothing to
            // do with the TOCTOU window it exists to catch — the exact kind of
            // "trades a slow suite for an intermittently red one" outcome #1809
            // was raised to avoid. 120s matches CliServer's own default timeout.
            var lines = await server.SendRequestStreamingAsync(RunTestsReq(bundle), TimeSpan.FromSeconds(120));
            var lastType = JsonDocument.Parse(lines[^1]).RootElement.GetProperty("type").GetString();
            if (lastType != "summary")
            {
                failures.Add($"iter {i}: last line type={lastType}, not summary");
                continue;
            }
            var cancelResponse = JsonDocument.Parse(
                await server.SendAsync("{\"command\":\"cancel\"}", TimeSpan.FromSeconds(120))).RootElement;
            if (!cancelResponse.GetProperty("noop").GetBoolean())
                failures.Add($"iter {i}: cancel-after-summary returned noop=false");
        }

        Assert.True(failures.Count == 0, $"{failures.Count}/{iterationCount} iterations hit the race:\n" + string.Join("\n", failures));
    }

    // ------------------------------------------------------------------
    // Positive direction: cancel actually lands mid-run. Proves (a) an ack comes
    // back, (b) FEWER test events streamed than the suite contains — a concrete,
    // asserted observable that the run stopped early, not just that a message
    // parsed — and (c) the terminal summary carries cancelled:true.
    // ------------------------------------------------------------------

    [SkippableFact]
    public async Task RunTests_CancelDuringRun_StopsEarly_AckNoopFalse_SummaryCancelledTrue()
    {
        TestArtifacts.SkipIfMissing();

        const int testCount = 2;

        // #1845: a test-only barrier directory, unique per test run, wired to the
        // server subprocess ONLY (via CliServer.StartAsync's extraEnv — never onto
        // the current xUnit worker process, see TestBarrier's doc comment) so it
        // cannot affect any other concurrently-running test's server. The server
        // blocks in TestBarrier.WaitForRelease() right after emitting each `test`
        // event; nothing releases it until this test explicitly does so below.
        var barrierDir = Path.Combine(Path.GetTempPath(), "al-runner-cancel-barrier", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(barrierDir);
        var releaseFile = Path.Combine(barrierDir, "release");

        try
        {
            await using var server = await CliServer.StartAsync(ExtraServerArgs(),
                extraEnv: new Dictionary<string, string> { ["AL_RUNNER_TEST_BARRIER_DIR"] = barrierDir });

            var bundle = MakeBarrierBundle(testCount);

            // Release the barrier only once the cancel's ack has actually been
            // observed (see SendRequestAndCancelAfterFirstTestAsync's onAckReceived
            // doc comment) — i.e. only once "cancel arrived while a run was active"
            // is already proven server-side, not merely "cancel was sent". Until
            // this file exists, the server CANNOT start test 2 — the window is not
            // a race, it is a guarantee.
            var (lines, ackLine) = await server.SendRequestAndCancelAfterFirstTestAsync(
                RunTestsReq(bundle),
                onAckReceived: () => File.WriteAllText(releaseFile, ""));

            // (a) the cancel was acknowledged during streaming — not silently swallowed.
            Assert.True(ackLine != null, "cancel was never acked before the run's summary arrived.");
            var ack = JsonDocument.Parse(ackLine!).RootElement;
            Assert.Equal("ack", ack.GetProperty("type").GetString());
            Assert.Equal("cancel", ack.GetProperty("command").GetString());
            // By construction the cancel is sent only after observing the first `test`
            // event, which can only have been emitted after the server published its
            // CancellationTokenSource — so a run WAS active when cancel arrived. This
            // is not a race: noop:false is a hard requirement here, not a fallback.
            Assert.False(ack.GetProperty("noop").GetBoolean(),
                "cancel arrived while a run was active (proven by having already seen a " +
                "`test` event) — the ack must be noop:false, not the no-active-run shape.");

            // (b) concrete proof the run stopped early: strictly fewer `test` events
            // than the fixture's testCount tests. A no-op cancel handler (or one wired
            // to nothing) would let both finish and this assertion would fail — and,
            // separately, would hang for up to 60s inside TestBarrier.WaitForRelease
            // on test 2's completion (never released a second time), which is itself a
            // loud signal that the cancel check between tests regressed.
            var testEventCount = lines.Count(l => l.Contains("\"type\":\"test\""));
            Assert.True(testEventCount < testCount,
                $"expected fewer than {testCount} test events after a mid-run cancel, got {testEventCount}. " +
                $"Lines:\n{string.Join('\n', lines)}");
            Assert.True(testEventCount >= 1, "the first test (that triggered the cancel) must still be reported.");

            // (c) the terminal summary must say so explicitly.
            var summaryLine = lines.Last(l => l.Contains("\"type\":\"summary\""));
            var summary = JsonDocument.Parse(summaryLine).RootElement;
            Assert.True(summary.TryGetProperty("cancelled", out var cancelledProp) && cancelledProp.GetBoolean(),
                $"expected cancelled:true on the summary. summary={summary.GetRawText()}");
            Assert.Equal(testEventCount, summary.GetProperty("total").GetInt32());
        }
        finally
        {
            // Belt-and-braces: if an assertion above threw before the barrier was
            // ever released, don't leave a directory the OS temp-cleaner has to find
            // on its own schedule.
            try { Directory.Delete(barrierDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [SkippableFact]
    public async Task RunTests_NoCancelSent_SummaryNeverCarriesCancelled()
    {
        // Negative companion to the cancel-during-run test above: an UNCANCELLED
        // run must run every test and must NOT carry `cancelled` on the summary at
        // all (never a literal false — see ServerProtocol.Summary's doc comment).
        TestArtifacts.SkipIfMissing();

        var bundle = MakeFastBundle(variant: 2);
        var server = await _shared.GetAsync(ExtraServerArgs());

        var lines = await server.SendRequestStreamingAsync(RunTestsReq(bundle));
        var summary = JsonDocument.Parse(lines[^1]).RootElement;

        Assert.Equal(1, summary.GetProperty("total").GetInt32());
        Assert.False(summary.TryGetProperty("cancelled", out _),
            "an uncancelled run's summary must omit `cancelled` entirely, not emit it as false.");
    }
}
