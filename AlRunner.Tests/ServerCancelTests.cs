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
    /// 20 [Test] methods, each spinning a deterministic, CPU-bound loop (not a
    /// timer/sleep) of 15,000,000 iterations. Calibrated against this runner
    /// (50,000,000 iterations measured at ~76ms on the dev box that authored this
    /// test — see the PR description) to ~20-25ms of real CPU work per test,
    /// ~450ms for the whole suite. Every test is equally "slow" (rather than one
    /// deliberately-slow test at a fixed position) so the proof does not depend on
    /// reflection's method enumeration order, which .NET does not guarantee to
    /// match declaration order.
    ///
    /// This is what makes RunTests_CancelDuringRun_* deterministic without a
    /// `Thread.Sleep`/timing guess in the TEST: cancellation is sent the instant
    /// the client observes the FIRST `{"type":"test"}` line (proving the
    /// server-side CancellationTokenSource already exists — no race there, see
    /// CliServer.SendRequestAndCancelAfterFirstTestAsync), and from that point on
    /// there are up to 19 more ~20ms tests providing repeated windows for the
    /// cancel's local-pipe round trip (microseconds, typically) to land before the
    /// LAST one starts. The remaining risk — the whole 20-test, ~450ms suite
    /// completing before a same-machine pipe round trip resolves — is not
    /// eliminated in the way a real synchronization primitive would eliminate it,
    /// but is many orders of magnitude smaller than the margin provided; see the
    /// PR description for why a stronger (AL-side polling/signal-file) mechanism
    /// was not used instead.
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
        var bundle = MakeSlowishBundle(testCount);
        await using var server = await CliServer.StartAsync(ExtraServerArgs());

        var (lines, ackLine) = await server.SendRequestAndCancelAfterFirstTestAsync(RunTestsReq(bundle));

        // (a) the cancel was acknowledged during streaming — not silently swallowed.
        Assert.NotNull(ackLine);
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
        // than the fixture's 20 tests. A no-op cancel handler (or one wired to
        // nothing) would let all 20 finish and this assertion would fail.
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
