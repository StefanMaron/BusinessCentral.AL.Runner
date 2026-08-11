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
/// they skip (not fail) when absent — see ArtifactsPresent()/PlatformAppsDir(),
/// same convention as CacheKeyDependencyClosureTests.
/// </summary>
[Collection("server-serial")]
public class ServerCancelTests
{
    private static bool ArtifactsPresent()
    {
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stdCache = Path.Combine(home, ".local", "share", "al-runner", "artifacts");
        return Directory.Exists(stdCache) && Directory.EnumerateDirectories(stdCache).Any();
    }

    private static string PlatformAppsDir()
    {
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".al-runner", "platform-apps");
    }

    private static string[] ExtraServerArgs()
    {
        var platformApps = PlatformAppsDir();
        return Directory.Exists(platformApps)
            ? new[] { "--package-cache", platformApps }
            : Array.Empty<string>();
    }

    /// <summary>
    /// One trivial passing test — enough to prove "a run happened and finished",
    /// nothing more. Used by the tests that don't care about run duration.
    /// </summary>
    private static string MakeFastBundle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-server-cancel-fast", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "d1b2c3d4-e5f6-4708-a9ba-cbdcedfe0f33",
          "name": "Runner Extras - Server Cancel Fast Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 60310, "to": 60319 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "FastProbe.Codeunit.al"), """
        codeunit 60310 "Server Cancel Fast Probe SX"
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
    /// <paramref name="testCount"/> [Test] methods, each spinning a deterministic,
    /// CPU-bound loop (not a timer/sleep) of <paramref name="spinIterations"/>
    /// iterations. Every test is equally "slow" (rather than one deliberately-slow
    /// test at a fixed position) so the proof does not depend on reflection's
    /// method enumeration order, which .NET does not guarantee to match
    /// declaration order.
    ///
    /// #1785: <paramref name="spinIterations"/> used to be a fixed constant
    /// (15,000,000, calibrated once on one dev box against a "50,000,000
    /// iterations ~= 76ms" measurement) that gave RunTests_CancelDuringRun_* a
    /// ~450ms wall-clock margin for the cancel's local-pipe round trip to land in
    /// — a margin that does not adapt to slower or CI-contended hardware. The
    /// fixed constant is gone: callers now derive <paramref name="spinIterations"/>
    /// from <see cref="MeasureMsPerIterationAsync"/>/<see cref="MeasureCancelRoundTripMsAsync"/>,
    /// which measure this machine's actual AL-loop speed and actual cancel
    /// round-trip latency, right before the destructive run, on the SAME server
    /// process under whatever contention is present at that moment — see
    /// RunTests_CancelDuringRun_StopsEarly_AckNoopFalse_SummaryCancelledTrue.
    /// This method itself is also reused, unmodified, AS that calibration probe
    /// (testCount: 1) — one code path, no drift between what's measured and what's
    /// asserted against.
    ///
    /// This is what makes RunTests_CancelDuringRun_* deterministic without a
    /// `Thread.Sleep`/timing guess in the TEST: cancellation is sent the instant
    /// the client observes the FIRST `{"type":"test"}` line (proving the
    /// server-side CancellationTokenSource already exists — no race there, see
    /// CliServer.SendRequestAndCancelAfterFirstTestAsync), and from that point on
    /// there are up to <paramref name="testCount"/> - 1 more tests providing
    /// repeated windows for the cancel's local-pipe round trip to land before the
    /// LAST one starts. The remaining risk — the whole suite completing before a
    /// same-machine pipe round trip resolves — is not eliminated in the way a real
    /// synchronization primitive would eliminate it (that would need a signal
    /// channel from the xUnit process INTO the running AL loop inside the server
    /// process; the only such channel is the stdin/stdout protocol itself, which
    /// is the thing under test — using it to also pace the workload would prove
    /// nothing). What changed is that the margin now scales with a live
    /// measurement instead of a stale constant, so a slower box or a contended
    /// CI leg gets a proportionally bigger margin instead of the same fixed one.
    /// </summary>
    private static string MakeSlowishBundle(int testCount = 20, int spinIterations = 15_000_000)
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-server-cancel-slow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "e1b2c3d4-e5f6-4708-a9ba-cbdcedfe0f44",
          "name": "Runner Extras - Server Cancel Slowish Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 60320, "to": 60329 } ],
          "runtime": "14.0"
        }
        """);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("codeunit 60320 \"Server Cancel Slowish SX\"");
        sb.AppendLine("{");
        sb.AppendLine("    Subtype = Test;");
        sb.AppendLine();
        sb.AppendLine("    local procedure Spin()");
        sb.AppendLine("    var");
        sb.AppendLine("        i: Integer;");
        sb.AppendLine("        n: Integer;");
        sb.AppendLine("    begin");
        sb.AppendLine("        n := 0;");
        sb.AppendLine($"        for i := 1 to {spinIterations} do");
        sb.AppendLine("            n += 1;");
        sb.AppendLine("    end;");
        sb.AppendLine();
        for (var i = 1; i <= testCount; i++)
        {
            sb.AppendLine("    [Test]");
            sb.AppendLine($"    procedure Test{i:D2}()");
            sb.AppendLine("    begin");
            sb.AppendLine("        Spin();");
            sb.AppendLine("    end;");
            sb.AppendLine();
        }
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(dir, "SlowishProbe.Codeunit.al"), sb.ToString());
        return dir;
    }

    // ------------------------------------------------------------------
    // #1785 live calibration: replaces MakeSlowishBundle's old fixed
    // 15,000,000-iteration constant with two measurements taken on THIS
    // machine, against THIS server process, immediately before the destructive
    // cancel-during-run assertion — so the workload margin scales with actual
    // machine speed and actual contention instead of a historical dev-box
    // number. Both helpers reuse real protocol round trips (no new surface, no
    // AL-side polling/signal-file mechanism — see MakeSlowishBundle's doc
    // comment for why that direction was rejected).
    // ------------------------------------------------------------------

    /// <summary>
    /// Measures actual AL-loop wall-clock cost per <c>Spin()</c> iteration on
    /// THIS machine, via a real compile + run of a one-test MakeSlowishBundle
    /// probe and the <c>durationMs</c> the server itself reports on the
    /// resulting <c>test</c> event (execution time only — excludes compile, so
    /// compile-time variance never pollutes the ratio). If a probe finishes too
    /// fast to register a nonzero millisecond reading, the probe is retried with
    /// 4x the iterations (up to 3 retries) rather than dividing by a truncated
    /// zero. If even that fails to produce a nonzero reading (an implausibly
    /// fast box), falls back to the historical "50,000,000 iterations ~= 76ms"
    /// dev-box measurement that used to be this fixture's only calibration.
    /// </summary>
    private static async Task<double> MeasureMsPerIterationAsync(CliServer server)
    {
        var iterations = 10_000_000;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var probe = MakeSlowishBundle(testCount: 1, spinIterations: iterations);
            var lines = await server.SendRequestStreamingAsync(RunTestsReq(probe));
            var testLine = lines.First(l => l.Contains("\"type\":\"test\""));
            var durationMs = JsonDocument.Parse(testLine).RootElement.GetProperty("durationMs").GetInt64();
            if (durationMs > 0)
                return (double)durationMs / iterations;
            iterations *= 4;
        }
        return 76.0 / 50_000_000;
    }

    /// <summary>
    /// Measures the ACTUAL round-trip latency of the <c>cancel</c> side-channel
    /// command against THIS server, right now, by sending it with no active run
    /// (a supported no-op path — see Cancel_NoActiveRequest_AcksAsNoop). That
    /// exercises the exact same stdin-reader-thread -&gt;
    /// HandleSideChannelCommand -&gt; outputLock-guarded-write path the real
    /// mid-run cancel exercises, without disturbing anything. Several samples,
    /// max taken: a single sample can be spuriously fast (no contention at that
    /// instant); a max is the conservative choice for a value everything
    /// downstream treats as a floor to build margin on top of.
    /// </summary>
    private static async Task<double> MeasureCancelRoundTripMsAsync(CliServer server, int samples = 5)
    {
        var maxMs = 0.0;
        for (var i = 0; i < samples; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await server.SendAsync("{\"command\":\"cancel\"}");
            sw.Stop();
            if (sw.Elapsed.TotalMilliseconds > maxMs) maxMs = sw.Elapsed.TotalMilliseconds;
        }
        return maxMs;
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
        await using var server = await CliServer.StartAsync(ExtraServerArgs());
        var response = await server.SendAsync("{\"command\":\"cancel\"}");
        var doc = JsonDocument.Parse(response).RootElement;
        Assert.Equal("ack", doc.GetProperty("type").GetString());
        Assert.Equal("cancel", doc.GetProperty("command").GetString());
        Assert.True(doc.GetProperty("noop").GetBoolean());
    }

    [Fact]
    public async Task Cancel_TwiceWithoutActiveRequest_BothNoop()
    {
        await using var server = await CliServer.StartAsync(ExtraServerArgs());
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
        await using var server = await CliServer.StartAsync(ExtraServerArgs());
        var response = await server.SendAsync(
            "{\"command\":\"cancel\",\"reason\":\"user clicked stop\",\"requestId\":42}");
        var doc = JsonDocument.Parse(response).RootElement;
        Assert.Equal("ack", doc.GetProperty("type").GetString());
        Assert.Equal("cancel", doc.GetProperty("command").GetString());
        Assert.True(doc.GetProperty("noop").GetBoolean());
    }

    [Fact]
    public async Task Cancel_AfterRunTestsCompletes_IsNoop()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifacts not present"); return; }

        var bundle = MakeFastBundle();
        await using var server = await CliServer.StartAsync(ExtraServerArgs());

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

    // ------------------------------------------------------------------
    // Positive direction: cancel actually lands mid-run. Proves (a) an ack comes
    // back, (b) FEWER test events streamed than the suite contains — a concrete,
    // asserted observable that the run stopped early, not just that a message
    // parsed — and (c) the terminal summary carries cancelled:true.
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunTests_CancelDuringRun_StopsEarly_AckNoopFalse_SummaryCancelledTrue()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifacts not present"); return; }

        const int testCount = 20;
        await using var server = await CliServer.StartAsync(ExtraServerArgs());

        // #1785: calibrate the workload against THIS server, on THIS machine,
        // right now — instead of trusting a historical dev-box constant. Both
        // measurements are real protocol round trips against the same process
        // that will run the destructive request below, so whatever contention
        // is present at this moment (this test runs concurrently with 7 other
        // BC-version legs in CI) inflates both the measured round trip AND,
        // proportionally, the margin computed from it.
        var msPerIteration = await MeasureMsPerIterationAsync(server);
        var roundTripMs = await MeasureCancelRoundTripMsAsync(server);

        // The (testCount - 1) tests remaining after the one that triggers the
        // cancel are the TOTAL window available for the round trip to land in —
        // cancellation only needs the run to still be mid-suite when it's
        // observed, not "still on test 2" specifically (see MakeSlowishBundle's
        // doc comment on the 19-checkpoint amplification). safetyFactor gives
        // that combined window a wide margin over the just-measured round trip;
        // the floor keeps the original ~20ms/test baseline as a lower bound so a
        // fast, idle box doesn't shrink the workload to near-nothing.
        const double safetyFactor = 30.0;
        const double perTestFloorMs = 20.0;
        var targetRemainingMs = Math.Max((testCount - 1) * perTestFloorMs, safetyFactor * roundTripMs);
        var perTestMs = targetRemainingMs / (testCount - 1);
        var spinIterations = Math.Max(15_000_000, (int)Math.Ceiling(perTestMs / msPerIteration));
        // Sanity cap: guards a single freak round-trip sample from turning this
        // into a multi-minute test rather than a hardening. 300,000,000 is
        // already ~20x the historical per-test iteration count.
        spinIterations = Math.Min(spinIterations, 300_000_000);

        Console.Error.WriteLine(
            $"[calibration] msPerIteration={msPerIteration:G4} roundTripMs={roundTripMs:G4} " +
            $"perTestMs={perTestMs:G4} spinIterations={spinIterations}");

        var bundle = MakeSlowishBundle(testCount, spinIterations);

        // Diagnostic prefix carried on every assertion below: if the margin ever
        // proves insufficient on some CI leg, this is the calibration data that
        // was live-measured for THIS run, not the historical constant — enough to
        // tell "the measurement itself was too optimistic" from "the round trip
        // spiked well past even 30x its own just-measured baseline".
        string CalibrationDiag() =>
            $"calibration: msPerIteration={msPerIteration:G4}, roundTripMs={roundTripMs:G4}, " +
            $"perTestMs={perTestMs:G4}, spinIterations={spinIterations}";

        var (lines, ackLine) = await server.SendRequestAndCancelAfterFirstTestAsync(RunTestsReq(bundle));

        // (a) the cancel was acknowledged during streaming — not silently swallowed.
        Assert.True(ackLine != null, $"cancel was never acked before the run's summary arrived. {CalibrationDiag()}");
        var ack = JsonDocument.Parse(ackLine!).RootElement;
        Assert.Equal("ack", ack.GetProperty("type").GetString());
        Assert.Equal("cancel", ack.GetProperty("command").GetString());
        // By construction the cancel is sent only after observing the first `test`
        // event, which can only have been emitted after the server published its
        // CancellationTokenSource — so a run WAS active when cancel arrived. This
        // is not a race: noop:false is a hard requirement here, not a fallback.
        Assert.False(ack.GetProperty("noop").GetBoolean(),
            "cancel arrived while a run was active (proven by having already seen a " +
            $"`test` event) — the ack must be noop:false, not the no-active-run shape. {CalibrationDiag()}");

        // (b) concrete proof the run stopped early: strictly fewer `test` events
        // than the fixture's 20 tests. A no-op cancel handler (or one wired to
        // nothing) would let all 20 finish and this assertion would fail.
        var testEventCount = lines.Count(l => l.Contains("\"type\":\"test\""));
        Assert.True(testEventCount < testCount,
            $"expected fewer than {testCount} test events after a mid-run cancel, got {testEventCount}. " +
            $"{CalibrationDiag()}. Lines:\n{string.Join('\n', lines)}");
        Assert.True(testEventCount >= 1, "the first test (that triggered the cancel) must still be reported.");

        // (c) the terminal summary must say so explicitly.
        var summaryLine = lines.Last(l => l.Contains("\"type\":\"summary\""));
        var summary = JsonDocument.Parse(summaryLine).RootElement;
        Assert.True(summary.TryGetProperty("cancelled", out var cancelledProp) && cancelledProp.GetBoolean(),
            $"expected cancelled:true on the summary. summary={summary.GetRawText()}");
        Assert.Equal(testEventCount, summary.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task RunTests_NoCancelSent_SummaryNeverCarriesCancelled()
    {
        // Negative companion to the cancel-during-run test above: an UNCANCELLED
        // run must run every test and must NOT carry `cancelled` on the summary at
        // all (never a literal false — see ServerProtocol.Summary's doc comment).
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifacts not present"); return; }

        var bundle = MakeFastBundle();
        await using var server = await CliServer.StartAsync(ExtraServerArgs());

        var lines = await server.SendRequestStreamingAsync(RunTestsReq(bundle));
        var summary = JsonDocument.Parse(lines[^1]).RootElement;

        Assert.Equal(1, summary.GetProperty("total").GetInt32());
        Assert.False(summary.TryGetProperty("cancelled", out _),
            "an uncancelled run's summary must omit `cancelled` entirely, not emit it as false.");
    }
}
