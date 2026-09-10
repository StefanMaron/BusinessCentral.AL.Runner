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
// map first waits for the compile that is already running and builds it. That keeps both
// client orderings working, which emitting `initialized` late would not.
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

    /// <summary>Drives `initialize` and waits for the `initialized` event, leaving the
    /// session at exactly the point the specification says configuration requests may be
    /// sent — and BEFORE `launch`.</summary>
    private static async Task<DapClient> StartAndInitializeAsync()
    {
        var dap = await DapClient.StartAsync(FixtureSrc);
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

            // The diagnostic is CACHED, and a later launch must report it rather than
            // succeeding against an empty map it now believes is resolved (#3845 review).
            // This is the assertion that pins the latch: EnsureSourceMap marks itself
            // resolved on the way out, so a version that recorded no reason would leave a
            // green launch behind a bundle that cannot run.
            var launchSeq = dap.SendRequest("launch", new { });
            var launchResp = await dap.ReadUntilResponseAsync(launchSeq, timeout: TimeSpan.FromSeconds(120));
            Assert.False(launchResp.GetProperty("success").GetBoolean(),
                $"launch must fail on a bundle that does not compile: {launchResp}");
            var launchMsg = launchResp.TryGetProperty("message", out var lm) ? lm.GetString() ?? "" : "";
            Assert.NotEqual("", launchMsg);
            // launch reports the diagnostic bare; the breakpoint message wraps the same
            // string. Asserting containment ties the two answers to one cached value rather
            // than to two independently-produced ones.
            Assert.Contains(launchMsg, message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
