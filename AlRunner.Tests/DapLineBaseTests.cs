// DapLineBaseTests — #3881: the DAP loop ignored `linesStartAt1`.
//
// DAP's `initialize` lets a client say whether it counts lines from 1 or from 0. Absent means
// 1, per the specification. `RunDapLoop` never read the field, and every line it compared or
// reported was 1-based — the numbering `AlSourceSpanCodec.AbsoluteFromLine` produces:
//
//   * `setBreakpoints` resolved the requested line against 1-based spans, so a 0-based
//     client's breakpoint landed one line ABOVE the statement it meant;
//   * the breakpoint response's `line`, the `stopped` event's `line` and every `stackTrace`
//     frame's `line` went back 1-based whatever the client had asked for.
//
// PR #3879 fixed the sibling capability `columnsStartAt1` for the one field it introduced, so
// until this landed the adapter converted columns and not lines — harder to reason about than
// converting neither. It also left `stackTrace`'s own hardcoded `column = 1` unconverted,
// which is the same defect one surface over and is fixed here with it.
//
// The fixture is Fixtures/DapMultiTarget, shared with DapMultiTargetLineTests: line 35 is
// `First := 1;`, the single-statement control, and line 36 carries two statements. A 0-based
// client names them 34 and 35.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class DapLineBaseTests
{
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "DapMultiTarget"));

    private const string SourceFileName = "MultiTarget.Codeunit.al";

    /// <summary>`First := 1;` — one statement, so one stop. 1-based, as the file is numbered.</summary>
    private const int SingleStatementLine = 35;

    /// <summary>`procedure Seven() ... end;  procedure Nine() ... end;` — two one-line
    /// procedures, so a breakpoint here arms two scopes and each stop has a caller. 1-based.
    /// </summary>
    private const int TwoProceduresLine = 26;

    /// <summary>`Total := Seven() + Nine();` — the statement that calls both, and so the line
    /// the CALLER frame sits on at each of those stops. 1-based.</summary>
    private const int CallerOfTheTwoProceduresLine = 48;

    /// <summary>The blank line after the two one-line procedures: no executable AL statement
    /// on it. 1-based. Its predecessor, line 26, carries two — which is what makes the number
    /// a 0-based client writes for it discriminating.</summary>
    private const int BlankLine = 27;

    /// <summary>Drives initialize / launch / setBreakpoints with the client's own line base
    /// and returns the live session together with the setBreakpoints response. Line numbers
    /// passed in are in the CLIENT's base, exactly as a real client would send them.</summary>
    private static async Task<(DapClient Dap, JsonElement BpResponse)> StartAndSetBreakpointAsync(
        int line, bool linesStartAt1, bool columnsStartAt1 = true)
    {
        var dap = await DapClient.StartAsync(FixtureSrc);
        try
        {
            var initSeq = dap.SendRequest("initialize",
                new { adapterID = "al-runner-tests", linesStartAt1, columnsStartAt1 });
            var initEvents = new List<JsonElement>();
            var initResp = await dap.ReadUntilResponseAsync(initSeq, initEvents);
            Assert.True(initResp.GetProperty("success").GetBoolean(), initResp.ToString());
            var sawInitialized = initEvents.Any(e => e.GetProperty("event").GetString() == "initialized")
                || (await dap.ReadUntilEventAsync("initialized")).GetProperty("event").GetString() == "initialized";
            Assert.True(sawInitialized);

            var launchSeq = dap.SendRequest("launch", new { });
            var launchResp = await dap.ReadUntilResponseAsync(launchSeq, timeout: TimeSpan.FromSeconds(120));
            Assert.True(launchResp.GetProperty("success").GetBoolean(),
                $"launch failed: {launchResp}\n--- stderr ---\n{dap.StdErr}");

            var bpSeq = dap.SendRequest("setBreakpoints", new
            {
                source = new { path = Path.Combine(FixtureSrc, SourceFileName) },
                breakpoints = new[] { new { line } },
            });
            var bpResp = await dap.ReadUntilResponseAsync(bpSeq);
            Assert.True(bpResp.GetProperty("success").GetBoolean(), bpResp.ToString());
            return (dap, bpResp);
        }
        catch
        {
            await dap.DisposeAsync();
            throw;
        }
    }

    /// <summary>Drives the same session with the LEGACY <c>lines[]</c> array instead of
    /// <c>breakpoints[]</c>. DAP's older spelling carries a bare line and no column, and it is
    /// a separate parse in the handler — so it is a separate conversion, which is why it needs
    /// its own session rather than a parameter on the one above.</summary>
    private static async Task<(DapClient Dap, JsonElement BpResponse)> StartAndSetLegacyLineBreakpointAsync(
        int line, bool linesStartAt1)
    {
        var dap = await DapClient.StartAsync(FixtureSrc);
        try
        {
            var initSeq = dap.SendRequest("initialize",
                new { adapterID = "al-runner-tests", linesStartAt1 });
            var initEvents = new List<JsonElement>();
            var initResp = await dap.ReadUntilResponseAsync(initSeq, initEvents);
            Assert.True(initResp.GetProperty("success").GetBoolean(), initResp.ToString());
            if (!initEvents.Any(e => e.GetProperty("event").GetString() == "initialized"))
                await dap.ReadUntilEventAsync("initialized");

            var launchSeq = dap.SendRequest("launch", new { });
            var launchResp = await dap.ReadUntilResponseAsync(launchSeq, timeout: TimeSpan.FromSeconds(120));
            Assert.True(launchResp.GetProperty("success").GetBoolean(),
                $"launch failed: {launchResp}\n--- stderr ---\n{dap.StdErr}");

            var bpSeq = dap.SendRequest("setBreakpoints", new
            {
                source = new { path = Path.Combine(FixtureSrc, SourceFileName) },
                lines = new[] { line },
            });
            var bpResp = await dap.ReadUntilResponseAsync(bpSeq);
            Assert.True(bpResp.GetProperty("success").GetBoolean(), bpResp.ToString());
            return (dap, bpResp);
        }
        catch
        {
            await dap.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// RED before the fix: the breakpoint is unverified. A 0-based client naming
    /// <c>First := 1;</c> as line 34 had that number compared against 1-based spans, so it
    /// resolved against the `begin` above it — which carries no instrumented statement — and
    /// the client was told the line it asked for has no executable AL on it.
    ///
    /// <para>GREEN: verified, and every line that crosses the wire comes back in the client's
    /// own base — the breakpoint response, the <c>stopped</c> event and the <c>stackTrace</c>
    /// frame all say 34. The locals pin WHICH statement the pause is in front of: the stop is
    /// before the statement's own effect, so First is still 0.</para>
    /// </summary>
    [SkippableFact]
    public async Task ZeroBasedClient_BreakpointOnItsOwnLineNumber_BindsTheStatementAndReportsItBack()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(
            SingleStatementLine - 1, linesStartAt1: false);
        await using var _ = dap;

        var bp = bpResp.GetProperty("body").GetProperty("breakpoints")[0];
        Assert.True(bp.GetProperty("verified").GetBoolean(),
            $"{bpResp}\n--- stderr ---\n{dap.StdErr}");
        Assert.Equal(SingleStatementLine - 1, bp.GetProperty("line").GetInt32());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal(SingleStatementLine - 1, stopped.GetProperty("body").GetProperty("line").GetInt32());

        var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
        var stResp = await dap.ReadUntilResponseAsync(stSeq);
        var topFrame = stResp.GetProperty("body").GetProperty("stackFrames")[0];
        Assert.Equal(SingleStatementLine - 1, topFrame.GetProperty("line").GetInt32());

        // The pause is BEFORE `First := 1;` runs, so the assignment has not happened yet.
        // Without this an adapter that bound some other statement of the procedure would
        // satisfy the line assertions above by echoing the requested number back.
        var frameId = topFrame.GetProperty("id").GetInt32();
        var scSeq = dap.SendRequest("scopes", new { frameId });
        var scResp = await dap.ReadUntilResponseAsync(scSeq);
        var variablesReference = scResp.GetProperty("body").GetProperty("scopes")[0]
            .GetProperty("variablesReference").GetInt32();
        var varSeq = dap.SendRequest("variables", new { variablesReference });
        var varResp = await dap.ReadUntilResponseAsync(varSeq);
        var locals = varResp.GetProperty("body").GetProperty("variables").EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("value").GetString());
        Assert.Equal("0", locals["First"]);

        var contSeq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq);
        var events = new List<JsonElement>();
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60), events);
        Assert.DoesNotContain(events, e => e.GetProperty("event").GetString() == "stopped");
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// The refusal direction, and the one that shows the old behaviour ARMING something rather
    /// than merely mis-numbering it. A 0-based client writes 26 for the blank line 27; read as
    /// 1-based, 26 is the line carrying the fixture's two one-line procedures.
    ///
    /// <para>RED before the fix: verified, and two scopes armed — the client is told a
    /// breakpoint is set on a blank line, and execution then stops somewhere it never asked
    /// for. GREEN: unverified, with the number it sent echoed back and a message saying why.
    /// A refusal fact anchored on a line that reads the same in both bases would pass either
    /// way and prove nothing, which is why this one is anchored on a line whose NEIGHBOUR
    /// resolves.</para>
    /// </summary>
    [SkippableFact]
    public async Task ZeroBasedClient_ALineWhosePredecessorResolves_IsRefusedRatherThanArmed()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(
            BlankLine - 1, linesStartAt1: false);
        await using var _ = dap;

        var bp = bpResp.GetProperty("body").GetProperty("breakpoints")[0];
        Assert.False(bp.GetProperty("verified").GetBoolean(),
            $"{bpResp}\n--- stderr ---\n{dap.StdErr}");
        Assert.Equal(BlankLine - 1, bp.GetProperty("line").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(bp.GetProperty("message").GetString()),
            $"an unverified breakpoint must say why: {bpResp}");

        // Nothing is armed, so the run goes straight through to `exited` with no stop.
        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);
        var events = new List<JsonElement>();
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(120), events);
        Assert.DoesNotContain(events, e => e.GetProperty("event").GetString() == "stopped");
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// A client that sends <c>linesStartAt1: true</c> explicitly is unchanged by the
    /// conversion: line 35 asked for, line 35 verified, line 35 reported by the <c>stopped</c>
    /// event and by the frame, column 1 on the frame.
    ///
    /// <para>This is the control the in/out conversions are measured against: a fix that
    /// converted unconditionally, or that subtracted where it should add, turns this red while
    /// leaving the 0-based facts green. It does NOT cover the absent-property default — this
    /// helper sends both booleans — which is what
    /// <see cref="DefaultBases_WhenTheClientSendsNeitherProperty_AreOneBased"/> is for.</para>
    /// </summary>
    [SkippableFact]
    public async Task OneBasedClient_IsUnaffected_AndTheFrameColumnStaysOne()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(
            SingleStatementLine, linesStartAt1: true);
        await using var _ = dap;

        var bp = bpResp.GetProperty("body").GetProperty("breakpoints")[0];
        Assert.True(bp.GetProperty("verified").GetBoolean(),
            $"{bpResp}\n--- stderr ---\n{dap.StdErr}");
        Assert.Equal(SingleStatementLine, bp.GetProperty("line").GetInt32());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal(SingleStatementLine, stopped.GetProperty("body").GetProperty("line").GetInt32());

        var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
        var stResp = await dap.ReadUntilResponseAsync(stSeq);
        var topFrame = stResp.GetProperty("body").GetProperty("stackFrames")[0];
        Assert.Equal(SingleStatementLine, topFrame.GetProperty("line").GetInt32());
        Assert.Equal(1, topFrame.GetProperty("column").GetInt32());

        var contSeq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq);
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60));
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// The sibling capability on the surface PR #3879 did not reach: <c>stackTrace</c> writes
    /// <c>column = 1</c> for every frame, unconverted, so a client that counts columns from 0
    /// is told a frame starts one column right of where it does.
    ///
    /// <para>RED before the fix: 1. GREEN: 0, the 0-based spelling of the same first column.
    /// Filed as part of #3881 rather than separately because it is the identical conversion at
    /// the identical boundary, in the handler the line conversion has to touch anyway.</para>
    /// </summary>
    [SkippableFact]
    public async Task ZeroBasedColumnsClient_FrameColumn_ComesBackInItsOwnBase()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(
            SingleStatementLine, linesStartAt1: true, columnsStartAt1: false);
        await using var _ = dap;

        Assert.True(bpResp.GetProperty("body").GetProperty("breakpoints")[0]
            .GetProperty("verified").GetBoolean(), $"{bpResp}\n--- stderr ---\n{dap.StdErr}");

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);
        await dap.ReadUntilEventAsync("stopped");

        var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
        var stResp = await dap.ReadUntilResponseAsync(stSeq);
        Assert.Equal(0, stResp.GetProperty("body").GetProperty("stackFrames")[0]
            .GetProperty("column").GetInt32());

        var contSeq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq);
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60));
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// A second <c>initialize</c> must be refused, so a base negotiated once cannot move under
    /// breakpoints that were armed and acknowledged in it. DAP permits <c>initialize</c> only
    /// as the first request and only once; nothing enforced that here, and until lines were
    /// converted the only thing a repeat could move was a column.
    ///
    /// <para>RED before the guard: the second request succeeds and flips the line base, so the
    /// <c>stopped</c> event reports 35 for the breakpoint the adapter had already acknowledged
    /// as 34 — the client is shown two different numbers for one breakpoint it set once. GREEN:
    /// the second request fails with a message, the negotiated base stands, and the stop is
    /// still reported at 34.</para>
    ///
    /// <para>Raised by an adversarial review of PR #3899 (gpt-5.6-sol), which reached it through
    /// the deferred-breakpoint path: a request deferred while the source map is being built
    /// stores numbers already converted into the base in force when it arrived, so a base that
    /// changes before <c>DrainDeferredBreakpoints</c> runs would answer in a base the request
    /// was not written in. Refusing the repeat closes both routes at the one point they share.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task ASecondInitialize_IsRefused_SoTheNegotiatedLineBaseCannotMove()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(
            SingleStatementLine - 1, linesStartAt1: false);
        await using var _ = dap;
        Assert.Equal(SingleStatementLine - 1,
            bpResp.GetProperty("body").GetProperty("breakpoints")[0].GetProperty("line").GetInt32());

        var reinitSeq = dap.SendRequest("initialize",
            new { adapterID = "al-runner-tests", linesStartAt1 = true, columnsStartAt1 = true });
        var reinitResp = await dap.ReadUntilResponseAsync(reinitSeq);
        Assert.False(reinitResp.GetProperty("success").GetBoolean(),
            $"a second initialize must be refused: {reinitResp}");
        Assert.Contains("initialize", reinitResp.GetProperty("message").GetString() ?? "",
            StringComparison.OrdinalIgnoreCase);

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        // Still the base the session was initialized in, not the one the refused request asked for.
        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal(SingleStatementLine - 1, stopped.GetProperty("body").GetProperty("line").GetInt32());

        var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
        var stResp = await dap.ReadUntilResponseAsync(stSeq);
        Assert.Equal(SingleStatementLine - 1, stResp.GetProperty("body").GetProperty("stackFrames")[0]
            .GetProperty("line").GetInt32());

        var contSeq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq);
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60));
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// The legacy <c>lines[]</c> array is a second, independent parse of the same request, and
    /// it converts too. Nothing in the rest of this suite sends that spelling, so a regression
    /// leaving this one path 1-based would keep every other fact green — which is exactly the
    /// shape that makes a test suite read as coverage while protecting nothing.
    ///
    /// <para>RED against <c>origin/main</c>, and NOT for the reason this comment first gave.
    /// The legacy path did not merely skip the conversion there — it was unreachable, because
    /// an unbraced <c>else</c> bound it to the inner <c>if (bp.TryGetProperty("line"))</c>. So
    /// the request resolved nothing and came back <c>{"success":true,"breakpoints":[]}</c>, and
    /// this fact failed indexing element zero rather than on a wrong line number. GREEN:
    /// verified, echoed as 34, and the stop is at the statement it meant — pinned by the
    /// locals, since <c>First</c> is still 0 in front of <c>First := 1;</c>.</para>
    ///
    /// <para>Raised by GitHub Copilot's automatic review of PR #3899; the account above was
    /// corrected after an adversarial review (gpt-6-astra) caught it describing a defect that
    /// was never there.</para>
    /// </summary>
    [SkippableFact]
    public async Task ZeroBasedClient_UsingTheLegacyLinesArray_BindsAndReportsInItsOwnBase()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetLegacyLineBreakpointAsync(
            SingleStatementLine - 1, linesStartAt1: false);
        await using var _ = dap;

        var bp = bpResp.GetProperty("body").GetProperty("breakpoints")[0];
        Assert.True(bp.GetProperty("verified").GetBoolean(),
            $"{bpResp}\n--- stderr ---\n{dap.StdErr}");
        Assert.Equal(SingleStatementLine - 1, bp.GetProperty("line").GetInt32());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal(SingleStatementLine - 1, stopped.GetProperty("body").GetProperty("line").GetInt32());

        var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
        var stResp = await dap.ReadUntilResponseAsync(stSeq);
        var topFrame = stResp.GetProperty("body").GetProperty("stackFrames")[0];
        Assert.Equal(SingleStatementLine - 1, topFrame.GetProperty("line").GetInt32());

        var frameId = topFrame.GetProperty("id").GetInt32();
        var scSeq = dap.SendRequest("scopes", new { frameId });
        var scResp = await dap.ReadUntilResponseAsync(scSeq);
        var variablesReference = scResp.GetProperty("body").GetProperty("scopes")[0]
            .GetProperty("variablesReference").GetInt32();
        var varSeq = dap.SendRequest("variables", new { variablesReference });
        var varResp = await dap.ReadUntilResponseAsync(varSeq);
        var locals = varResp.GetProperty("body").GetProperty("variables").EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("value").GetString());
        Assert.Equal("0", locals["First"]);

        var contSeq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq);
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60));
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// The specification's default, driven as a client actually sends it: an <c>initialize</c>
    /// carrying NEITHER <c>linesStartAt1</c> nor <c>columnsStartAt1</c> means both are true.
    ///
    /// <para>Every other fact here sends both properties explicitly, so a regression reading an
    /// absent property as <c>false</c> would leave the whole suite green while breaking every
    /// client that omits them — which is most of them. Raised by an adversarial review of
    /// PR #3899 (gpt-6-astra).</para>
    /// </summary>
    [SkippableFact]
    public async Task DefaultBases_WhenTheClientSendsNeitherProperty_AreOneBased()
    {
        TestArtifacts.SkipIfMissing();

        var dap = await DapClient.StartAsync(FixtureSrc);
        await using var _ = dap;

        // No `linesStartAt1`, no `columnsStartAt1`. That absence is the whole fact.
        var initSeq = dap.SendRequest("initialize", new { adapterID = "al-runner-tests" });
        var initEvents = new List<JsonElement>();
        var initResp = await dap.ReadUntilResponseAsync(initSeq, initEvents);
        Assert.True(initResp.GetProperty("success").GetBoolean(), initResp.ToString());
        if (!initEvents.Any(e => e.GetProperty("event").GetString() == "initialized"))
            await dap.ReadUntilEventAsync("initialized");

        var launchSeq = dap.SendRequest("launch", new { });
        var launchResp = await dap.ReadUntilResponseAsync(launchSeq, timeout: TimeSpan.FromSeconds(120));
        Assert.True(launchResp.GetProperty("success").GetBoolean(),
            $"launch failed: {launchResp}\n--- stderr ---\n{dap.StdErr}");

        var bpSeq = dap.SendRequest("setBreakpoints", new
        {
            source = new { path = Path.Combine(FixtureSrc, SourceFileName) },
            breakpoints = new[] { new { line = SingleStatementLine } },
        });
        var bpResp = await dap.ReadUntilResponseAsync(bpSeq);
        var bp = bpResp.GetProperty("body").GetProperty("breakpoints")[0];
        Assert.True(bp.GetProperty("verified").GetBoolean(),
            $"{bpResp}\n--- stderr ---\n{dap.StdErr}");
        Assert.Equal(SingleStatementLine, bp.GetProperty("line").GetInt32());

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        var stopped = await dap.ReadUntilEventAsync("stopped");
        Assert.Equal(SingleStatementLine, stopped.GetProperty("body").GetProperty("line").GetInt32());

        var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
        var stResp = await dap.ReadUntilResponseAsync(stSeq);
        var topFrame = stResp.GetProperty("body").GetProperty("stackFrames")[0];
        Assert.Equal(SingleStatementLine, topFrame.GetProperty("line").GetInt32());
        Assert.Equal(1, topFrame.GetProperty("column").GetInt32());

        var contSeq = dap.SendRequest("continue", new { threadId = 1 });
        await dap.ReadUntilResponseAsync(contSeq);
        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60));
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// EVERY frame converts, not just the paused one. A breakpoint inside a one-line procedure
    /// gives a stack with a caller, and the caller's line comes from a different branch of
    /// <c>AlDapStackWalker.Walk</c> than frame 0's — `ResolveCurrentLine` rather than the
    /// paused statement index.
    ///
    /// <para>So a conversion applied only where the paused line is computed would answer this
    /// suite's other facts correctly and still hand a 0-based client a 1-based caller. Line 26
    /// declares <c>Seven()</c> and <c>Nine()</c>; both are armed (#3820), and the call that
    /// reaches them is line 48, so a 0-based client must be told 25 and 47.</para>
    ///
    /// <para>Raised by an adversarial review of PR #3899 (gpt-6-astra).</para>
    /// </summary>
    [SkippableFact]
    public async Task ZeroBasedClient_ACallerFrame_ConvertsToo()
    {
        TestArtifacts.SkipIfMissing();

        var (dap, bpResp) = await StartAndSetBreakpointAsync(
            TwoProceduresLine - 1, linesStartAt1: false);
        await using var _ = dap;
        Assert.True(bpResp.GetProperty("body").GetProperty("breakpoints")[0]
            .GetProperty("verified").GetBoolean(), $"{bpResp}\n--- stderr ---\n{dap.StdErr}");

        var cfgSeq = dap.SendRequest("configurationDone");
        await dap.ReadUntilResponseAsync(cfgSeq);

        // Seven() then Nine(), in call order; both stops have the same caller line.
        foreach (var expectedFrame in new[] { "Seven", "Nine" })
        {
            var stopped = await dap.ReadUntilEventAsync("stopped", TimeSpan.FromSeconds(60));
            Assert.Equal(TwoProceduresLine - 1, stopped.GetProperty("body").GetProperty("line").GetInt32());

            var stSeq = dap.SendRequest("stackTrace", new { threadId = 1 });
            var stResp = await dap.ReadUntilResponseAsync(stSeq);
            var frames = stResp.GetProperty("body").GetProperty("stackFrames");
            Assert.True(frames.GetArrayLength() >= 2,
                $"expected a caller frame behind {expectedFrame}: {stResp}");

            var top = frames[0];
            var frameName = top.GetProperty("name").GetString() ?? "";
            Assert.True(frameName.Contains(expectedFrame, StringComparison.OrdinalIgnoreCase),
                $"paused frame is '{frameName}', not {expectedFrame}: {stResp}");
            Assert.Equal(TwoProceduresLine - 1, top.GetProperty("line").GetInt32());

            // The caller, whose line comes from the scope's own live StatementNumber.
            Assert.Equal(CallerOfTheTwoProceduresLine - 1, frames[1].GetProperty("line").GetInt32());

            var contSeq = dap.SendRequest("continue", new { threadId = 1 });
            await dap.ReadUntilResponseAsync(contSeq);
        }

        var exited = await dap.ReadUntilEventAsync("exited", TimeSpan.FromSeconds(60));
        Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
    }
}
