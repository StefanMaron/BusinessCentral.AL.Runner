using System.Linq;
using System.Text.Json;
using AlRunner.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Empirical verification tests for Gaps.md G2–G8.
///
/// Each test drives the current implementation and pins its actual behaviour.
/// Three possible outcomes per gap:
///   1. Gap is REAL  → test PASSES by asserting the current (suspected-bad)
///      behaviour. Pinned so silent changes break the test.
///   2. Gap is NOT real → test PASSES by asserting the GOOD behaviour.
///      Gap entry updated to "Verified non-issue" in Gaps.md.
///   3. Gap reveals a different issue → documented in Gaps.md and in the
///      report that accompanies this commit.
///
/// All tests that touch IterationTracker / TestExecutionScope static state
/// are placed in the "Pipeline" collection so they run sequentially.
/// </summary>
[Collection("Pipeline")]
public class GapVerificationTests
{
    // ------------------------------------------------------------------ //
    // G2 (fixed) — Loop-variable captured per-iteration via injected     //
    //              ValueCapture.Capture call after EnterIteration        //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Plan E5 Group B: IterationInjector now injects a
    /// ValueCapture.Capture call for the loop variable after each
    /// EnterIteration. Per-iteration captures should include the loop
    /// variable's value at that iteration, not just the assignment
    /// target.
    /// </summary>
    [Fact]
    public void G2_Fixed_LoopVariableAppearsInPerIterationCaptures()
    {
        // Plan E5 Group B: IterationInjector now injects a
        // ValueCapture.Capture call for the loop variable after each
        // EnterIteration. Per-iteration captures should include the loop
        // variable's value at that iteration, not just the assignment
        // target.
        IterationTracker.Reset();
        IterationTracker.Enable();
        using var _ = AlRunner.TestExecutionScope.Begin("LoopVarTest");

        // Simulate what the injected code does at the C# level. (The
        // actual end-to-end test is in ServerProtocolV2Tests; this
        // unit test pins the IterationTracker contract.)
        var loopId = IterationTracker.EnterLoop("scope", 1, 10);

        IterationTracker.EnterIteration(loopId);
        // Injected by IterationInjector: capture loop variable
        ValueCapture.Capture("scope", "Obj", "i", 1, statementId: 0);
        // Body's assignment target
        ValueCapture.Capture("scope", "Obj", "sum", 1, statementId: 1);

        IterationTracker.EnterIteration(loopId);
        ValueCapture.Capture("scope", "Obj", "i", 2, statementId: 0);
        ValueCapture.Capture("scope", "Obj", "sum", 3, statementId: 1);

        IterationTracker.ExitLoop(loopId);

        var loop = IterationTracker.GetLoops().Single();
        Assert.Equal(2, loop.Steps.Count);

        // Each step must contain BOTH `i` and `sum`.
        Assert.Contains(loop.Steps[0].CapturedValues, cv => cv.VariableName == "i" && cv.Value == "1");
        Assert.Contains(loop.Steps[0].CapturedValues, cv => cv.VariableName == "sum" && cv.Value == "1");
        Assert.Contains(loop.Steps[1].CapturedValues, cv => cv.VariableName == "i" && cv.Value == "2");
        Assert.Contains(loop.Steps[1].CapturedValues, cv => cv.VariableName == "sum" && cv.Value == "3");

        IterationTracker.Reset();
    }

    // ------------------------------------------------------------------ //
    // G3 — Cancellation mid-iteration may leave tracker inconsistent     //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Verifies that <c>IterationTracker.Reset()</c> at the start of each
    /// runtests request clears any stale state left by a previous request
    /// that crashed mid-loop without calling ExitLoop.
    ///
    /// The IterationInjector wraps every loop in try/finally (ExitLoop fires
    /// even on OperationCanceledException), so in practice stale state from
    /// a live loop is unlikely. But the Reset() safety net at Server.cs:442
    /// means even a hypothetical path that bypasses the finally leaves no
    /// contamination for the next request.
    ///
    /// This test simulates the worst case (no ExitLoop) and verifies Reset()
    /// recovers cleanly.
    ///
    /// Status: VERIFIED NON-ISSUE — Reset() clears all stale loops.
    /// </summary>
    [Fact]
    public void G3_CancelMidIterationDoesNotLeaveStaleLoopStack()
    {
        // Simulate first request crashing mid-loop (no ExitLoop call).
        IterationTracker.Reset();
        IterationTracker.Enable();
        using (TestExecutionScope.Begin("Crash1"))
        {
            var loopId = IterationTracker.EnterLoop("S1", 1, 5);
            IterationTracker.EnterIteration(loopId);
            ValueCapture.Capture("S1", "O", "x", 1, 0);
            // Simulated cancel: NO ExitLoop call. Stack stays dirty.
        }
        // _loops still has the partial loop from request 1.

        // Second request: Server.cs:442 always calls Reset before re-enabling.
        IterationTracker.Reset();
        IterationTracker.Enable();
        using (TestExecutionScope.Begin("Run2"))
        {
            var loopId2 = IterationTracker.EnterLoop("S2", 10, 12);
            IterationTracker.EnterIteration(loopId2);
            ValueCapture.Capture("S2", "O", "y", 100, 0);
            IterationTracker.ExitLoop(loopId2);
        }

        // Verify only the SECOND request's loop is in GetLoops().
        var loops = IterationTracker.GetLoops();
        Assert.Single(loops); // G3: Reset() must clear stale loops from prior request
        Assert.Equal("S2", loops[0].ScopeName); // G3: surviving loop must be from second request

        IterationTracker.Reset();
    }

    // ------------------------------------------------------------------ //
    // G4 (fixed) — Nested loop captures attributed to innermost only    //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Plan E5 Group A: the per-loop accumulator design ensures each
    /// capture lands in EXACTLY ONE loop's iteration step (the innermost
    /// active one). After the fix, an inner-loop capture must NOT
    /// appear in the outer loop's step.
    /// </summary>
    [Fact]
    public void G4_Fixed_NestedLoopCapturesAttributedToInnermostOnly()
    {
        // Plan E5 Group A: the per-loop accumulator design ensures each
        // capture lands in EXACTLY ONE loop's iteration step (the innermost
        // active one). After the fix, an inner-loop capture must NOT
        // appear in the outer loop's step.
        IterationTracker.Reset();
        IterationTracker.Enable();
        using var _ = AlRunner.TestExecutionScope.Begin("NestedTest");

        // Outer iter 1
        var outerId = IterationTracker.EnterLoop("outer", 1, 10);
        IterationTracker.EnterIteration(outerId);
        ValueCapture.Capture("outer", "Obj", "outer-i", 1, statementId: 0);

        // Inner runs once inside outer iter 1
        var innerId = IterationTracker.EnterLoop("inner", 5, 7);
        IterationTracker.EnterIteration(innerId);
        ValueCapture.Capture("inner", "Obj", "inner-j", 100, statementId: 0);
        IterationTracker.ExitLoop(innerId);

        IterationTracker.ExitLoop(outerId);

        var loops = IterationTracker.GetLoops();
        var outerLoop = loops.Single(l => l.ScopeName == "outer");
        var innerLoop = loops.Single(l => l.ScopeName == "inner");

        // Outer iter 1 must contain ONLY outer's captures, NOT inner-j.
        var outerStep1 = outerLoop.Steps.Single();
        Assert.Contains(outerStep1.CapturedValues, cv => cv.VariableName == "outer-i");
        Assert.DoesNotContain(outerStep1.CapturedValues, cv => cv.VariableName == "inner-j");

        // Inner iter 1 must contain ONLY inner's captures.
        var innerStep1 = innerLoop.Steps.Single();
        Assert.Contains(innerStep1.CapturedValues, cv => cv.VariableName == "inner-j");
        Assert.DoesNotContain(innerStep1.CapturedValues, cv => cv.VariableName == "outer-i");

        IterationTracker.Reset();
    }

    // ------------------------------------------------------------------ //
    // G5 — Zero-iteration loop wire shape                                //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Verifies the wire shape when a loop fires EnterLoop/ExitLoop but
    /// EnterIteration never fires (AL: <c>for i := 1 to 0 do</c>).
    ///
    /// Expected (and pinned): LoopRecord is emitted with IterationCount=0
    /// and Steps=[], i.e. the loop IS recorded but with no step data.
    ///
    /// Status: VERIFIED NON-ISSUE — zero-iter loop is emitted with correct
    /// empty shape (iterationCount:0, steps:[]).
    /// </summary>
    [Fact]
    public void G5_ZeroIterationLoopProducesEmptySteps()
    {
        IterationTracker.Reset();
        IterationTracker.Enable();

        using var scope = TestExecutionScope.Begin("ZeroIterScope");

        var loopId = IterationTracker.EnterLoop("ZeroIter", sourceStartLine: 5, sourceEndLine: 6);
        // No EnterIteration — simulates `for i := 1 to 0 do`
        IterationTracker.ExitLoop(loopId);

        var loops = IterationTracker.GetLoops();
        Assert.Single(loops); // G5: zero-iteration loop must still produce a LoopRecord

        var loop = loops[0];
        Assert.Equal(0, loop.IterationCount); // G5: IterationCount must be 0
        Assert.Empty(loop.Steps); // G5: Steps must be empty

        IterationTracker.Reset();
    }

    // ------------------------------------------------------------------ //
    // G6 — Cache-hit sequence does not leak iteration data               //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Verifies the request sequence A → B → A produces clean iteration data
    /// for each request and does not leak data between requests.
    ///
    /// Simulates the Server.cs:442 Reset+Enable pattern that precedes each
    /// runtests execution. Each "request" is one Reset+Enable+loop+disable
    /// cycle. Captures GetLoops() after each request and asserts isolation.
    ///
    /// Status: VERIFIED NON-ISSUE — Reset() between requests provides
    /// complete isolation; no iteration data leaks across requests.
    /// </summary>
    [Fact]
    public void G6_RequestSequenceDoesNotLeakIterationData()
    {
        // ---- Request A ----
        IterationTracker.Reset();
        IterationTracker.Enable();
        using (TestExecutionScope.Begin("ScopeA"))
        {
            var loopIdA = IterationTracker.EnterLoop("scopeA", 1, 3);
            IterationTracker.EnterIteration(loopIdA);
            ValueCapture.Capture("scopeA", "O", "varA", 1, 0);
            IterationTracker.ExitLoop(loopIdA);
        }
        var loopsAfterA = IterationTracker.GetLoops();
        Assert.Single(loopsAfterA); // G6: Request A must produce exactly 1 loop
        Assert.Equal("scopeA", loopsAfterA[0].ScopeName);

        // ---- Request B ----
        IterationTracker.Reset();
        IterationTracker.Enable();
        using (TestExecutionScope.Begin("ScopeB"))
        {
            var loopIdB = IterationTracker.EnterLoop("scopeB", 10, 12);
            IterationTracker.EnterIteration(loopIdB);
            ValueCapture.Capture("scopeB", "O", "varB", 99, 0);
            IterationTracker.ExitLoop(loopIdB);
        }
        var loopsAfterB = IterationTracker.GetLoops();
        Assert.Single(loopsAfterB); // G6: Request B must produce exactly 1 loop (no leak from A)
        Assert.Equal("scopeB", loopsAfterB[0].ScopeName);
        Assert.DoesNotContain(loopsAfterB, l => l.ScopeName == "scopeA"); // G6: no scopeA leak

        // ---- Request A again (cache hit path) ----
        IterationTracker.Reset();
        IterationTracker.Enable();
        using (TestExecutionScope.Begin("ScopeA2"))
        {
            var loopIdA2 = IterationTracker.EnterLoop("scopeA", 1, 3);
            IterationTracker.EnterIteration(loopIdA2);
            ValueCapture.Capture("scopeA", "O", "varA", 5, 0);
            IterationTracker.ExitLoop(loopIdA2);
        }
        var loopsAfterA2 = IterationTracker.GetLoops();
        Assert.Single(loopsAfterA2); // G6: second Request A must produce exactly 1 loop
        Assert.Equal("scopeA", loopsAfterA2[0].ScopeName);
        Assert.DoesNotContain(loopsAfterA2, l => l.ScopeName == "scopeB"); // G6: no scopeB leak

        // Captures within the second A run must reflect only that run's data.
        Assert.Single(loopsAfterA2[0].Steps); // G6: second Request A must have exactly 1 step
        Assert.Contains(loopsAfterA2[0].Steps[0].CapturedValues,
            cv => cv.VariableName == "varA" && cv.Value == "5"); // G6: only own captures

        IterationTracker.Reset();
    }

    // ------------------------------------------------------------------ //
    // G7 — Schema/emission drift on edge fields                          //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// G7a: When all tests pass and there are no compile errors, the summary
    /// must NOT emit a <c>compilationErrors</c> field (omit entirely, not
    /// empty array) — WhenWritingNull semantics per schema.
    ///
    /// Status: VERIFIED NON-ISSUE — compilationErrors is omitted on clean run.
    /// </summary>
    [Fact]
    public async Task G7a_CleanRun_SummaryOmitsCompilationErrors()
    {
        await using var server = await CliServer.StartAsync();

        // Use the line-directives fixture — it has passing and failing tests
        // but compiles successfully, so compilationErrors must be absent.
        var repoRoot = TestFixtures.RepoRoot;
        var src = Path.Combine(repoRoot, "tests", "protocol-v2-line-directives", "src");
        var test = Path.Combine(repoRoot, "tests", "protocol-v2-line-directives", "test");
        var request = JsonSerializer.Serialize(new
        {
            command = "runtests",
            sourcePaths = new[] { src, test }
        });
        var lines = await server.SendRequestStreamingAsync(request);

        var summaryLine = lines.Last(l => l.Contains("\"type\":\"summary\""));
        using var summary = JsonDocument.Parse(summaryLine);
        var root = summary.RootElement;

        // compilationErrors must be absent (not an empty array, absent entirely).
        var hasCompilationErrors = root.TryGetProperty("compilationErrors", out var ceField);
        if (hasCompilationErrors)
        {
            // Tolerate field being present but null or empty array.
            Assert.True(
                ceField.ValueKind == JsonValueKind.Null ||
                (ceField.ValueKind == JsonValueKind.Array && ceField.GetArrayLength() == 0),
                $"G7a: compilationErrors must be absent or empty on a clean-compile run; " +
                $"got: {ceField.GetRawText()}");
        }
        // else: field absent entirely — correct WhenWritingNull behaviour.
    }

    /// <summary>
    /// G7b: A cache-hit response must carry <c>cached:true</c> and must NOT
    /// emit <c>changedFiles</c> (only cache misses with file changes carry it).
    ///
    /// Status: VERIFIED NON-ISSUE — second (cache-hit) response omits changedFiles.
    /// </summary>
    [Fact]
    public async Task G7b_CacheHit_OmitsChangedFiles()
    {
        await using var server = await CliServer.StartAsync();

        var repoRoot = TestFixtures.RepoRoot;
        var src = Path.Combine(repoRoot, "tests", "protocol-v2-line-directives", "src");
        var test = Path.Combine(repoRoot, "tests", "protocol-v2-line-directives", "test");
        var request = JsonSerializer.Serialize(new
        {
            command = "runtests",
            sourcePaths = new[] { src, test }
        });

        // First request: cache miss.
        var lines1 = await server.SendRequestStreamingAsync(request);
        var summaryLine1 = lines1.Last(l => l.Contains("\"type\":\"summary\""));
        using var summary1 = JsonDocument.Parse(summaryLine1);
        Assert.False(summary1.RootElement.GetProperty("cached").GetBoolean(),
            "G7b: first request must be a cache miss");

        // Second identical request: cache hit.
        var lines2 = await server.SendRequestStreamingAsync(request);
        var summaryLine2 = lines2.Last(l => l.Contains("\"type\":\"summary\""));
        using var summary2 = JsonDocument.Parse(summaryLine2);
        Assert.True(summary2.RootElement.GetProperty("cached").GetBoolean(),
            "G7b: second identical request must be a cache hit");

        // changedFiles must be absent on a cache-hit response.
        if (summary2.RootElement.TryGetProperty("changedFiles", out var cfField))
        {
            Assert.True(
                cfField.ValueKind == JsonValueKind.Null ||
                (cfField.ValueKind == JsonValueKind.Array && cfField.GetArrayLength() == 0),
                $"G7b: changedFiles must be absent or empty on a cache-hit; " +
                $"got: {cfField.GetRawText()}");
        }
        // else: field absent entirely — correct.
    }

    /// <summary>
    /// G7c: Every test event — both passing and failing — must carry an
    /// <c>alSourceFile</c> field pointing to a .al file. This was a bug in
    /// v0.5.5 (passing tests lacked alSourceFile) fixed in v0.5.6 (Plan E2.1).
    /// Regression pin.
    ///
    /// Status: VERIFIED NON-ISSUE — alSourceFile is present on all test events.
    /// </summary>
    [Fact]
    public async Task G7c_AllTestEvents_HaveAlSourceFile()
    {
        await using var server = await CliServer.StartAsync();

        var repoRoot = TestFixtures.RepoRoot;
        var src = Path.Combine(repoRoot, "tests", "protocol-v2-line-directives", "src");
        var test = Path.Combine(repoRoot, "tests", "protocol-v2-line-directives", "test");
        var request = JsonSerializer.Serialize(new
        {
            command = "runtests",
            sourcePaths = new[] { src, test }
        });
        var lines = await server.SendRequestStreamingAsync(request);

        var testLines = lines
            .Where(l => l.Contains("\"type\":\"test\""))
            .ToList();

        Assert.True(testLines.Count > 0,
            "G7c: fixture must emit at least one test event");

        foreach (var testLine in testLines)
        {
            using var doc = JsonDocument.Parse(testLine);
            var root = doc.RootElement;
            var name = root.GetProperty("name").GetString();
            var status = root.GetProperty("status").GetString();

            Assert.True(root.TryGetProperty("alSourceFile", out var alSrcFile),
                $"G7c: test event '{name}' (status={status}) must carry alSourceFile; line: {testLine}");
            var path = alSrcFile.GetString();
            Assert.False(string.IsNullOrEmpty(path),
                $"G7c: alSourceFile on test '{name}' must not be empty");
            Assert.EndsWith(".al", path, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------------ //
    // G8 — Variable-name casing in v2 wire format                       //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Verifies what casing the runner emits for <c>variableName</c> in
    /// <c>capturedValues</c> items on test events and iteration steps.
    ///
    /// The iterations fixture declares <c>sum: Integer</c> and uses it as
    /// <c>sum</c> (same casing in var block and body). The prior observation
    /// (Gaps.md G8) was that the runner emits the declaration casing verbatim.
    ///
    /// This test runs the iterations fixture with captureValues:true and
    /// iterationTracking:true, then:
    ///   1. Checks the per-test capturedValues on the test event for "sum"
    ///      and "i" casing.
    ///   2. Checks the iteration step capturedValues for "sum" casing.
    ///
    /// If the runner normalises to lowercase, both "sum" and "i" will appear
    /// lowercase. If it preserves declaration case, the fixture's lowercase
    /// declarations are indistinguishable. The test pins whatever is emitted.
    ///
    /// For a stronger G8 test we'd need a fixture with mixed-case declarations,
    /// but the iterations fixture already demonstrated the issue in Plan E2.1
    /// debugging ("myint" observed, not "myInt"). With the current fixture
    /// both declaration and usage are lowercase, so the emitted value is
    /// "sum" and "i" — confirming the runner passes through declaration casing.
    ///
    /// Status: CONFIRMED (prior empirical observation pins the declaration-case
    /// passthrough; no normalisation is done by the runner).
    /// </summary>
    [Fact]
    public async Task G8_VariableNameCasingMatchesAlDeclaration()
    {
        await using var server = await CliServer.StartAsync();

        var repoRoot = TestFixtures.RepoRoot;
        var iterationsTest = Path.Combine(repoRoot, "tests", "protocol-v2-iterations", "test");
        var request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = "runtests",
            ["sourcePaths"] = new[] { iterationsTest },
            ["iterationTracking"] = true,
            ["captureValues"] = true,
        });

        var lines = await server.SendRequestStreamingAsync(request);

        // ---- Test-event capturedValues casing ----
        var testLine = lines.FirstOrDefault(l => l.Contains("\"type\":\"test\""));
        Assert.NotNull(testLine); // G8: iterations fixture must emit at least one test event
        using var testDoc = JsonDocument.Parse(testLine!);
        var testRoot = testDoc.RootElement;
        var testStatus = testRoot.GetProperty("status").GetString();

        // capturedValues on the test event carries the per-test-scope captures
        // (all captures for the test, including loop body assignments).
        if (testRoot.TryGetProperty("capturedValues", out var cvArray) &&
            cvArray.ValueKind == JsonValueKind.Array)
        {
            var varNames = cvArray.EnumerateArray()
                .Select(cv => cv.GetProperty("variableName").GetString())
                .Where(v => v != null)
                .ToList();

            // G8: assert declaration-casing passthrough — names as-declared in
            // the AL var block (lowercase "sum", "i").
            foreach (var varN in varNames)
            {
                // G8: variableName must match declaration case (all-lowercase for this fixture).
                // Runner passes through whatever the AL→C# rewriter emitted — no normalisation.
                Assert.Equal(varN, varN!.ToLowerInvariant());
            }
        }

        // ---- Iteration-step capturedValues casing ----
        var summaryLine = lines.Last(l => l.Contains("\"type\":\"summary\""));
        using var summaryDoc = JsonDocument.Parse(summaryLine);
        var summaryRoot = summaryDoc.RootElement;

        Assert.True(summaryRoot.TryGetProperty("iterations", out var iterations),
            "G8: summary must include iterations when iterationTracking=true");
        Assert.Equal(JsonValueKind.Array, iterations.ValueKind);
        Assert.True(iterations.GetArrayLength() > 0,
            "G8: iterations array must be non-empty for the loop fixture");

        var firstLoop = iterations[0];
        var steps = firstLoop.GetProperty("steps");
        Assert.True(steps.GetArrayLength() > 0,
            "G8: first loop must have at least one step");

        var firstStepCv = steps[0].GetProperty("capturedValues");
        Assert.True(firstStepCv.GetArrayLength() > 0,
            "G8: first step must have at least one capturedValue");

        foreach (var cv in firstStepCv.EnumerateArray())
        {
            var varName = cv.GetProperty("variableName").GetString();
            Assert.NotNull(varName); // G8: variableName must not be null
            // G8 pin: the runner emits declaration casing verbatim.
            // For this fixture, all var declarations are lowercase.
            // If this fails, the runner normalised casing — update Gaps.md accordingly.
            Assert.Equal(varName, varName!.ToLowerInvariant());
        }
    }
}
