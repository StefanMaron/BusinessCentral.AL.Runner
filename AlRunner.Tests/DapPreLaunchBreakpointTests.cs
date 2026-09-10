// DapPreLaunchBreakpointTests — #3821: a `setBreakpoints` that arrives after `initialized`
// but before `launch` was answered against a source map that did not exist yet.
//
// The DAP specification's own sequence has the client react to the `initialized` event by
// sending its configuration requests, and `launch` is a separate exchange that finishes
// after `configurationDone`. So a client sending breakpoints before launch is following the
// protocol, not misusing it. This adapter built the source map inside `launch`, so in that
// window `sourceMap` was still AlSourceLocationMap.Empty and EVERY breakpoint came back
// `verified: false` — a successful response carrying a wrong answer, with nothing to tell it
// apart from "that line has no statement".
//
// The fix defers RESOLUTION rather than the `initialized` event: whichever request needs the
// map first waits for the compile that is already running and builds it. Deferring the event
// would also work — compilation needs nothing from the client — but it holds the client's
// WHOLE configuration sequence behind the compile rather than only the requests that need a
// map. RunDapLoop's EnsureSourceMap carries the same note; keep the two in step.
//
// These facts drive a real DAP session against Fixtures/DapTwoObjects — the same fixture and
// line numbers DapMultiObjectFileTests pins — with launch and setBreakpoints swapped.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class DapPreLaunchBreakpointTests
{
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "DapTwoObjects"));

    private const string SourceFileName = "TwoObjects.Codeunit.al";

    // Fixtures/DapTwoObjects/TwoObjects.Codeunit.al, asserted against exactly.
    private const int SecondObjectSecondStatementLine = 32; // Counter := Helper.Bump(Counter);
    private const int NoStatementLine = 16;                 // the first codeunit's closing brace

    /// <summary>
    /// Holds the adapter's source-map preparation open for a known interval
    /// (`AL_RUNNER_DAP_TEST_DELAY_MAP_MS`, honoured only by `RunDapLoop`). The two liveness
    /// facts at the bottom of this file need "the map is not ready yet" to be a state that
    /// lasts long enough to assert against; an organically slow bundle would make them flaky.
    /// </summary>
    private static IReadOnlyDictionary<string, string> HoldMapPreparation(int ms) =>
        new Dictionary<string, string> { ["AL_RUNNER_DAP_TEST_DELAY_MAP_MS"] = ms.ToString() };

    /// <summary>Drives `initialize` and waits for the `initialized` event, leaving the
    /// session at exactly the point the specification says configuration requests may be
    /// sent — and BEFORE `launch`.</summary>
    private static async Task<DapClient> StartAndInitializeAsync(
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var dap = await DapClient.StartAsync(FixtureSrc, extraEnv: extraEnv);
        try
        {
            var initSeq = dap.SendRequest("initialize", new { adapterID = "al-runner-tests" });
            var initEvents = new List<JsonElement>();
            var initResp = await dap.ReadUntilResponseAsync(initSeq, initEvents);
            Assert.True(initResp.GetProperty("success").GetBoolean(), initResp.ToString());
            var sawInitialized = initEvents.Any(e => e.GetProperty("event").GetString() == "initialized")
                || (await dap.ReadUntilEventAsync("initialized")).GetProperty("event").GetString() == "initialized";
            Assert.True(sawInitialized);
            return dap;
        }
        catch
        {
            await dap.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// RED before the fix: <c>verified: false</c> on a line that verifies perfectly well when
    /// the same request is sent after `launch` (DapMultiObjectFileTests asserts exactly that
    /// line, in that order). GREEN: it verifies here too, reports the file line asked for,
    /// and execution actually stops there — the last part is what makes this more than a
    /// response-shape assertion, since an adapter could answer <c>verified: true</c> and arm
    /// nothing.
    /// </summary>
    [SkippableFact]
    public async Task SetBreakpointsBeforeLaunch_VerifiesAndArms()
    {
        TestArtifacts.SkipIfMissing();

        await using var dap = await StartAndInitializeAsync();

        var bpSeq = dap.SendRequest("setBreakpoints", new
        {
            source = new { path = Path.Combine(FixtureSrc, SourceFileName) },
            breakpoints = new[] { new { line = SecondObjectSecondStatementLine } },
        });
        // The same 120s the launch request gets in DapMultiObjectFileTests: this request is
        // now the one that waits for the compile, so it inherits that cost.
        var bpResp = await dap.ReadUntilResponseAsync(bpSeq, timeout: TimeSpan.FromSeconds(120));
        Assert.True(bpResp.GetProperty("success").GetBoolean(), bpResp.ToString());

        var bps = bpResp.GetProperty("body").GetProperty("breakpoints");
        Assert.Equal(1, bps.GetArrayLength());
        Assert.True(bps[0].GetProperty("verified").GetBoolean(),
            $"a pre-launch breakpoint at file line {SecondObjectSecondStatementLine} was not "
            + $"verified — it resolved against an empty source map: {bpResp}\n"
            + $"--- stderr ---\n{dap.StdErr}");
        Assert.Equal(SecondObjectSecondStatementLine, bps[0].GetProperty("line").GetInt32());

        var launchSeq = dap.SendRequest("launch", new { });
        var launchResp = await dap.ReadUntilResponseAsync(launchSeq, timeout: TimeSpan.FromSeconds(120));
        Assert.True(launchResp.GetProperty("success").GetBoolean(),
            $"launch failed: {launchResp}\n--- stderr ---\n{dap.StdErr}");

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal("breakpoint", stopped.GetProperty("body").GetProperty("reason").GetString());
        Assert.Equal(SecondObjectSecondStatementLine, stopped.GetProperty("body").GetProperty("line").GetInt32());

        // Paused BEFORE the statement runs, so Counter still holds the previous statement's
        // 1. Reading it proves the arm landed on the requested statement rather than on some
        // other statement of the same object.
        var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
        var stResp = await dap.ReadUntilResponseAsync(stSeq);
        var frameId = stResp.GetProperty("body").GetProperty("stackFrames")[0].GetProperty("id").GetInt32();
        var scSeq = dap.SendRequest("scopes", new { frameId });
        var scResp = await dap.ReadUntilResponseAsync(scSeq);
        var variablesReference = scResp.GetProperty("body").GetProperty("scopes")[0]
            .GetProperty("variablesReference").GetInt32();
        var varSeq = dap.SendRequest("variables", new { variablesReference });
        var varResp = await dap.ReadUntilResponseAsync(varSeq);
        var vars = varResp.GetProperty("body").GetProperty("variables").EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("value").GetString());
        Assert.Equal("1", vars["Counter"]);
    }

    /// <summary>
    /// The negative direction, and the half #3821 says must stay distinguishable: a line that
    /// genuinely carries no executable statement is still unverified after the fix — the wait
    /// must not turn every request into a yes — and it now says WHY.
    ///
    /// Before the fix both cases answered <c>verified: false</c> with no message at all, so a
    /// client could not tell "nothing is loaded yet" from "that line has no statement". The
    /// assertion is on the message's substance, not its exact wording.
    /// </summary>
    [SkippableFact]
    public async Task SetBreakpointsBeforeLaunch_OnALineWithNoStatement_IsUnverifiedAndSaysWhy()
    {
        TestArtifacts.SkipIfMissing();

        await using var dap = await StartAndInitializeAsync();

        var bpSeq = dap.SendRequest("setBreakpoints", new
        {
            source = new { path = Path.Combine(FixtureSrc, SourceFileName) },
            breakpoints = new[] { new { line = NoStatementLine } },
        });
        var bpResp = await dap.ReadUntilResponseAsync(bpSeq, timeout: TimeSpan.FromSeconds(120));
        Assert.True(bpResp.GetProperty("success").GetBoolean(), bpResp.ToString());

        var bp = bpResp.GetProperty("body").GetProperty("breakpoints")[0];
        Assert.False(bp.GetProperty("verified").GetBoolean(),
            $"file line {NoStatementLine} is a closing brace and carries no statement, so it "
            + $"must not verify: {bpResp}");
        Assert.Equal(NoStatementLine, bp.GetProperty("line").GetInt32());
        var message = bp.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "" : "";
        Assert.True(message.Contains("no executable AL statement", StringComparison.OrdinalIgnoreCase),
            $"an unverified breakpoint must say which of the two reasons applies; got '{message}': {bpResp}");
    }

    /// <summary>
    /// The OTHER reason, and the one the fact above cannot reach: a bundle that does not
    /// compile. Before the fix a pre-launch request answered `verified: false` here too, so
    /// the two states were the same response — and the wrong one to act on, since "wait, it
    /// is still loading" and "this will never bind" call for opposite client behaviour.
    ///
    /// The bundle is written to a temp directory rather than checked in, so the repository
    /// does not carry a fixture that is deliberately broken.
    /// </summary>
    [SkippableFact]
    public async Task SetBreakpointsBeforeLaunch_OnABundleThatDoesNotCompile_SaysSoRatherThanBlamingTheLine()
    {
        TestArtifacts.SkipIfMissing();

        var dir = Path.Combine(Path.GetTempPath(), "al-runner-dap-3821-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // No application/platform floor — .claude/rules/no-base-app-in-csharp-tests.md.
            File.WriteAllText(Path.Combine(dir, "app.json"), """
            {
              "id": "b1f2c3d4-e5a6-4b7c-8d9e-0f1a2b3c4d5e",
              "name": "Runner Tests Fixture - DAP Uncompilable",
              "publisher": "AL Runner",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 60300, "to": 60309 } ],
              "runtime": "14.0"
            }
            """);
            // NoSuchType is not a type, so this bundle cannot compile.
            File.WriteAllText(Path.Combine(dir, "Broken.Codeunit.al"), """
            codeunit 60300 "Dap Broken"
            {
                procedure Nope()
                var
                    X: NoSuchType;
                begin
                    X := 1;
                end;
            }
            """);

            await using var dap = await DapClient.StartAsync(dir);

            var initSeq = dap.SendRequest("initialize", new { adapterID = "al-runner-tests" });
            var initEvents = new List<JsonElement>();
            var initResp = await dap.ReadUntilResponseAsync(initSeq, initEvents);
            Assert.True(initResp.GetProperty("success").GetBoolean(), initResp.ToString());

            var bpSeq = dap.SendRequest("setBreakpoints", new
            {
                source = new { path = Path.Combine(dir, "Broken.Codeunit.al") },
                breakpoints = new[] { new { line = 7 } },
            });
            var bpResp = await dap.ReadUntilResponseAsync(bpSeq, timeout: TimeSpan.FromSeconds(120));
            Assert.True(bpResp.GetProperty("success").GetBoolean(), bpResp.ToString());

            var bp = bpResp.GetProperty("body").GetProperty("breakpoints")[0];
            Assert.False(bp.GetProperty("verified").GetBoolean(), bpResp.ToString());
            var message = bp.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "" : "";
            Assert.True(message.Contains("did not compile", StringComparison.OrdinalIgnoreCase),
                $"a breakpoint in a bundle that does not compile must say so, not blame the "
                + $"line; got '{message}': {bpResp}\n--- stderr ---\n{dap.StdErr}");
            // And it must not be the other reason, which is what makes this fact more than
            // an assertion that SOME message is present.
            Assert.DoesNotContain("no executable AL statement", message, StringComparison.OrdinalIgnoreCase);

            // A later launch must report the same failure rather than succeeding against an
            // empty map it now believes is resolved.
            //
            // What this does NOT cover, stated because an earlier version of this comment
            // claimed it did (#3845 round-two review): a compile DIAGNOSTIC takes the
            // ordinary branch of EnsureSourceMap and never enters its catch, so this fact
            // would pass against the earlier implementation that latched before the work.
            // The catch is for a bundleRunTask that FAULTS or an AlCoverageSourceMap.Build
            // that throws, and proving that needs a seam to force one — #3846.
            var launchSeq = dap.SendRequest("launch", new { });
            var launchResp = await dap.ReadUntilResponseAsync(launchSeq, timeout: TimeSpan.FromSeconds(120));
            Assert.False(launchResp.GetProperty("success").GetBoolean(),
                $"launch must fail on a bundle that does not compile: {launchResp}");
            var launchMsg = launchResp.TryGetProperty("message", out var lm) ? lm.GetString() ?? "" : "";
            // The REAL compiler diagnostic, in both answers — not a generic "compile failed",
            // which every other assertion here would accept (#3845 round-two review).
            Assert.Contains("AL0134", launchMsg, StringComparison.Ordinal);
            Assert.Contains("NoSuchType", launchMsg, StringComparison.Ordinal);
            // launch reports the diagnostic bare; the breakpoint message wraps the same
            // string. Containment ties the two answers together — it does not by itself prove
            // one cached value, since recomputing the same deterministic diagnostic would
            // satisfy it too.
            Assert.Contains(launchMsg, message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// #3846: the request loop must stay able to read while a breakpoint request is waiting
    /// for the source map. The fix for #3821 first made `setBreakpoints` BLOCK the loop for
    /// the whole compile, which is worse than it sounds: nothing else is read meanwhile, so a
    /// client that gives up and disconnects is not heard until the compile ends, and a
    /// compile that never ends is an adapter that never exits.
    ///
    /// Map preparation is held open for 25 seconds and the client disconnects immediately.
    /// The response must come back in a small fraction of that.
    ///
    /// Mutation that turns this red: make the `setBreakpoints` case call
    /// AnswerSetBreakpoints unconditionally instead of deferring — the disconnect is then
    /// answered ~25s later and the 10s read below times out.
    /// </summary>
    [SkippableFact]
    public async Task DisconnectWhileABreakpointRequestWaitsForTheMap_IsAnsweredAtOnce()
    {
        TestArtifacts.SkipIfMissing();

        await using var dap = await StartAndInitializeAsync(HoldMapPreparation(25_000));

        // Deferred by construction: the map cannot be ready for another 25 seconds.
        dap.SendRequest("setBreakpoints", new
        {
            source = new { path = Path.Combine(FixtureSrc, SourceFileName) },
            breakpoints = new[] { new { line = SecondObjectSecondStatementLine } },
        });

        var started = System.Diagnostics.Stopwatch.StartNew();
        var discSeq = dap.SendRequest("disconnect", new { });
        var discResp = await dap.ReadUntilResponseAsync(discSeq, timeout: TimeSpan.FromSeconds(10));
        started.Stop();

        Assert.True(discResp.GetProperty("success").GetBoolean(), discResp.ToString());
        // Well inside the 25s hold, so this cannot pass by the hold having elapsed. The bound
        // is deliberately loose against a loaded CI box; the failing behaviour is 25s.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15),
            $"disconnect took {started.Elapsed.TotalSeconds:F1}s while a breakpoint request was "
            + $"waiting for the map — the loop was blocked rather than reading");

        // The session really is being torn down, not merely answered.
        await dap.ReadUntilEventAsync("terminated", TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// The other half: a deferred request is not merely postponed, it is ANSWERED, and
    /// without the client sending anything further. Between the request and the response the
    /// client is silent, so the only thing that can produce it is the loop's race between the
    /// outstanding transport read and the map becoming available.
    ///
    /// The answer is the real one — verified, on the file line asked for — so a version that
    /// drained the queue by writing a placeholder would not pass.
    /// </summary>
    [SkippableFact]
    public async Task ABreakpointRequestDeferredForTheMap_IsAnsweredWhenTheMapArrives()
    {
        TestArtifacts.SkipIfMissing();

        await using var dap = await StartAndInitializeAsync(HoldMapPreparation(4_000));

        var bpSeq = dap.SendRequest("setBreakpoints", new
        {
            source = new { path = Path.Combine(FixtureSrc, SourceFileName) },
            breakpoints = new[] { new { line = SecondObjectSecondStatementLine } },
        });

        // Nothing else is sent. No launch, no configurationDone.
        var bpResp = await dap.ReadUntilResponseAsync(bpSeq, timeout: TimeSpan.FromSeconds(120));
        Assert.True(bpResp.GetProperty("success").GetBoolean(), bpResp.ToString());
        var bp = bpResp.GetProperty("body").GetProperty("breakpoints")[0];
        Assert.True(bp.GetProperty("verified").GetBoolean(),
            $"the deferred request was answered, but not resolved: {bpResp}\n"
            + $"--- stderr ---\n{dap.StdErr}");
        Assert.Equal(SecondObjectSecondStatementLine, bp.GetProperty("line").GetInt32());
    }
}
