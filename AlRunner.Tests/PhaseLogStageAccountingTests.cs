using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #3474 — the mutation check for <see cref="PhaseLogIntegrationTests.AssertAppStagesAccountForTheRunTurn"/>.
///
/// That function's stage-accounting bound was an absolute `Σ stages &lt;= run_ms + 50`, which
/// fired on CI twice as a false "a stage is double-counting" report (72ms against 19ms on
/// 60f7e953; 82ms against 13ms on 3b939eb5, where `ordered-dep-ids` alone was 75ms). The
/// replacement is proportional AND excludes the two compile-gate stages that are not inside the
/// run turn at all.
///
/// A weakened bound is the obvious failure mode of that change, and a real double-count is rare
/// enough that no real run would catch it. So this suite drives the live function with synthetic
/// rows and asserts BOTH directions: the recorded CI records now pass, and a stage deliberately
/// counted twice still fails. Without the second half the fix would be indistinguishable from
/// deleting the assertion.
/// </summary>
public sealed class PhaseLogStageAccountingTests
{
    /// <summary>
    /// Every stage the runner emits inside the run turn, at 0ms unless overridden — so a row
    /// built here satisfies the "required stage present" and "install-seed order" checks and
    /// reaches the arithmetic this suite is actually about.
    /// </summary>
    private static readonly string[] RunTurnOrder =
    {
        "set-test-assembly", "type-discovery",
        "install-seed-reset-per-test", "install-seed-reset-for-new-bundle",
        "install-seed-set-test-assembly", "install-seed-arm-event-subscribers",
        "install-seed-dep-company-baseline",
        "install-seed-user-row", "install-seed-company-row",
        "install-seed-published-application-row",
        // Order is load-bearing and separately asserted: after the per-bundle reset, before the
        // baseline capture (#3176).
        "install-seed-access-control-row",
        "install-seed-run-own-install-triggers", "install-seed-capture-baseline",
        "codeunit-scan", "event-subscriber-inject", "codeunit-reset", "codeunit-instantiate",
        "resolve-display-name", "run-test-methods", "codeunit-dispose",
    };

    /// <summary>
    /// Builds one app row. <paramref name="stages"/> overrides individual stage values;
    /// <paramref name="extra"/> appends stages not in the standard set (used for the
    /// compile-gate marks and for the unclassified-stage check).
    /// </summary>
    private static JsonElement AppRow(
        long runMs,
        IDictionary<string, long>? stages = null,
        IEnumerable<KeyValuePair<string, long>>? extra = null)
    {
        var ordered = new List<KeyValuePair<string, long>>();
        // Compile-gate marks are entered first on a real run (the cache gate precedes the run
        // turn), so emitting them first keeps the synthetic row faithful to the real ordering.
        foreach (var kv in extra ?? Enumerable.Empty<KeyValuePair<string, long>>())
            ordered.Add(kv);
        foreach (var name in RunTurnOrder)
            ordered.Add(new KeyValuePair<string, long>(
                name, stages != null && stages.TryGetValue(name, out var v) ? v : 0));

        var stageJson = string.Join(",", ordered.Select(kv => $"\"{kv.Key}\":{kv.Value}"));
        return ParseRow(runMs, stageJson);
    }

    /// <summary>
    /// Assembles the app-row JSON. Written with concatenation rather than a raw string literal
    /// because the stage object needs a brace immediately against an interpolation hole, which
    /// raw-string brace counting cannot express readably.
    /// </summary>
    private static JsonElement ParseRow(long runMs, string stageJson)
    {
        var json =
            "{\"kind\":\"app\",\"pid\":1,\"bundle\":\"b\",\"bundle_index\":1,"
            + "\"bundles_in_process\":1,\"app\":\"synthetic\",\"app_index\":1,"
            + "\"apps_in_bundle\":1,\"run_ms\":" + runMs
            + ",\"stages\":{" + stageJson + "}}";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static void Run(JsonElement row) =>
        PhaseLogIntegrationTests.AssertAppStagesAccountForTheRunTurn(new List<JsonElement> { row });

    // ── The two recorded CI false positives now pass ───────────────────────────────────────

    /// <summary>
    /// Tonight's floor record (3b939eb5, BC 28.4, run 34552096539): `ordered-dep-ids` 75ms
    /// against a 13ms run turn, every other stage 0-4ms. Under the old absolute bound this
    /// summed to 82ms and failed. The stage is entered from the AL-output cache gate, outside
    /// every AddAppRun span, so it is not part of run_ms and must not be summed against it.
    /// </summary>
    [Fact]
    public void RecordedFloorFailure_OrderedDepIdsDominating_NoLongerReadsAsDoubleCounting()
    {
        var row = AppRow(
            runMs: 13,
            stages: new Dictionary<string, long>
            {
                ["install-seed-dep-company-baseline"] = 4,
                ["run-test-methods"] = 2,
                ["install-seed-user-row"] = 1,
            },
            extra: new[] { new KeyValuePair<string, long>("ordered-dep-ids", 75) });

        Run(row);   // the claim: this no longer throws
    }

    /// <summary>
    /// The original report (60f7e953): 72ms of stages against a 19ms run turn, of which
    /// `ordered-dep-ids` was 59ms — more than the entire old 50ms allowance on its own.
    /// </summary>
    [Fact]
    public void RecordedOriginalFailure_NoLongerReadsAsDoubleCounting()
    {
        var row = AppRow(
            runMs: 19,
            stages: new Dictionary<string, long>
            {
                ["install-seed-dep-company-baseline"] = 7,
                ["install-seed-published-application-row"] = 3,
                ["run-test-methods"] = 2,
            },
            extra: new[] { new KeyValuePair<string, long>("ordered-dep-ids", 59) });

        Run(row);
    }

    // ── ...and a genuine double-count still fails ──────────────────────────────────────────

    /// <summary>
    /// THE mutation check. A stage nested inside another — or one mark banked twice — makes the
    /// parts sum past the whole, which is exactly what #1861's invariant exists to catch. Here
    /// `install-seed-dep-company-baseline` is counted twice inside a run turn that only ever
    /// contained it once: run_ms 100ms, one 90ms stage, and a second 90ms stage standing in for
    /// the duplicate. 180 &gt; 100 + 10%.
    ///
    /// This is the assertion that makes the rest of the change safe: a bound that survives load
    /// but no longer fires here would be a deleted test wearing an assertion.
    /// </summary>
    [Fact]
    public void AStageCountedTwice_StillFailsAsDoubleCounting()
    {
        var row = AppRow(
            runMs: 100,
            stages: new Dictionary<string, long>
            {
                ["install-seed-dep-company-baseline"] = 90,
                // The duplicate: the same 90ms of work banked under a second mark.
                ["install-seed-user-row"] = 90,
            });

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Run(row));
        Assert.Contains("double-counting", ex.Message, StringComparison.Ordinal);
        Assert.Contains("180ms", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The proportional bound must not be a blank cheque: a double-count is ~100% of the
    /// duplicated stage, so even a modest one on a large run turn breaks it. run_ms 1,000ms with
    /// 1,150ms of stages — 15% over, comfortably inside what CI load produced in the recorded
    /// records as a FRACTION (82/13 is 530% over) but still a real accounting error.
    /// </summary>
    [Fact]
    public void AModestOvershootOnALargeRunTurn_StillFails()
    {
        var row = AppRow(
            runMs: 1000,
            stages: new Dictionary<string, long>
            {
                ["install-seed-dep-company-baseline"] = 600,
                ["run-test-methods"] = 550,
            });

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Run(row));
        Assert.Contains("double-counting", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The 10% band is not so wide that it swallows a duplicate of a mark this suite's own
    /// fixtures produce: on an idle box `install-seed-user-row` was 38ms of a 130ms run turn
    /// (measured, this branch). Counting it twice is 168 against 130 + 13 — still caught.
    /// </summary>
    [Fact]
    public void DuplicatingAMeasuredIdleBoxStage_StillFails()
    {
        var row = AppRow(
            runMs: 130,
            stages: new Dictionary<string, long>
            {
                ["install-seed-reset-per-test"] = 6,
                ["install-seed-dep-company-baseline"] = 17,
                ["install-seed-user-row"] = 38,
                ["install-seed-company-row"] = 2,
                ["install-seed-published-application-row"] = 8,
                ["install-seed-access-control-row"] = 21,
                ["install-seed-capture-baseline"] = 2,
                ["run-test-methods"] = 16,
                // The duplicate of the 38ms user-row seed.
                ["install-seed-arm-event-subscribers"] = 38,
            });

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Run(row));
    }

    // ── The bound survives load in both directions ─────────────────────────────────────────

    /// <summary>
    /// Measured on this branch under 4x CPU oversubscription: run_ms 27ms with
    /// `ordered-dep-ids` at 24ms, so `Σ ALL stages` (39ms) genuinely exceeded run_ms — the exact
    /// condition the old message called double-counting. The run-turn stages sum to 15ms, well
    /// inside 27ms, because nothing was double-counted.
    /// </summary>
    [Fact]
    public void MeasuredUnderLoad_OutOfTurnStageExceedingTheRunTurn_Passes()
    {
        var row = AppRow(
            runMs: 27,
            stages: new Dictionary<string, long>
            {
                ["install-seed-dep-company-baseline"] = 5,
                ["install-seed-published-application-row"] = 4,
                ["install-seed-access-control-row"] = 1,
                ["run-test-methods"] = 5,
            },
            extra: new[] { new KeyValuePair<string, long>("ordered-dep-ids", 24) });

        Run(row);
    }

    /// <summary>
    /// The mirror direction, and the one measured failing outright on this branch: under 16x CPU
    /// oversubscription the unattributed residual reached 515ms against a 5,781ms run turn —
    /// 14.6x the old absolute 250ms allowance — because the UNMARKED work inside the run turn
    /// slows down exactly as the marked work does. `unattributed &lt;= max(250, run_ms/4)`
    /// accommodates it; a constant cannot.
    /// </summary>
    [Fact]
    public void MeasuredUnderLoad_ResidualScalingWithTheRunTurn_Passes()
    {
        var row = AppRow(
            runMs: 5781,
            stages: new Dictionary<string, long>
            {
                ["install-seed-reset-per-test"] = 427,
                ["install-seed-reset-for-new-bundle"] = 1,
                ["install-seed-arm-event-subscribers"] = 51,
                ["install-seed-dep-company-baseline"] = 925,
                ["install-seed-user-row"] = 2045,
                ["install-seed-company-row"] = 91,
                ["install-seed-published-application-row"] = 328,
                ["install-seed-access-control-row"] = 689,
                ["install-seed-run-own-install-triggers"] = 16,
                ["install-seed-capture-baseline"] = 47,
                ["codeunit-scan"] = 15,
                ["codeunit-reset"] = 9,
                ["codeunit-instantiate"] = 1,
                ["run-test-methods"] = 536,
                ["codeunit-dispose"] = 35,
            },
            extra: new[]
            {
                new KeyValuePair<string, long>("ordered-dep-ids", 49),
                new KeyValuePair<string, long>("query-decl-probe", 1),
            });

        Run(row);
    }

    /// <summary>
    /// The residual bound is still real: half a run turn attributed to nothing means a stage
    /// mark is missing, whatever the machine speed. 3,000ms unmarked of a 6,000ms run turn is
    /// over the 25% band.
    /// </summary>
    [Fact]
    public void HalfTheRunTurnUnmarked_StillFailsAsUnattributed()
    {
        var row = AppRow(
            runMs: 6000,
            stages: new Dictionary<string, long> { ["run-test-methods"] = 3000 });

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Run(row));
        Assert.Contains("attributed to nothing", ex.Message, StringComparison.Ordinal);
    }

    // ── The partition stays closed ─────────────────────────────────────────────────────────

    /// <summary>
    /// #3474, and the reason this is not merely a wider tolerance: a NEW AppStage added to the
    /// AL-output cache gate would otherwise be summed against a run turn it was never part of
    /// and reproduce this very defect under a new name — passing on an idle box, so nothing
    /// would say so until CI went red. An unclassified stage fails here instead, naming itself
    /// and both sets.
    ///
    /// This is the third state `guards-need-a-third-state.md` asks for: "I cannot tell whether
    /// this stage belongs in the sum" is reported as a refusal, never resolved toward a pass.
    /// </summary>
    [Fact]
    public void AnUnclassifiedStage_IsRefusedRatherThanCountedAgainstTheRunTurn()
    {
        var row = AppRow(
            runMs: 50,
            stages: new Dictionary<string, long> { ["run-test-methods"] = 40 },
            extra: new[] { new KeyValuePair<string, long>("some-new-cache-gate-mark", 5) });

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Run(row));
        Assert.Contains("some-new-cache-gate-mark", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CompileGateStages", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RunTurnStages", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stage vanishing from the run-turn set is still caught by the required-stage check, so
    /// making the partition closed did not weaken the "deleting a mark fails" property that
    /// #1861 and #3176 rely on.
    /// </summary>
    [Fact]
    public void ADeletedRequiredStage_StillFails()
    {
        var ordered = RunTurnOrder
            .Where(n => n != "install-seed-access-control-row")
            .Select(n => $"\"{n}\":0");
        var row = ParseRow(10, string.Join(",", ordered));

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Run(row));
        Assert.Contains("install-seed-access-control-row", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rounding floor: every stage and run_ms are truncated to whole milliseconds
    /// (PhaseLog.AddStageTo, PhaseLog.AddAppRun), so on a sub-millisecond run turn the
    /// truncation alone can put the sum a millisecond or two over. A 0ms run turn with 2ms of
    /// stages is that case and must not fail; 20ms of stages against it is not, and must.
    /// </summary>
    [Fact]
    public void SubMillisecondRunTurn_ToleratesTruncationButNotARealOvershoot()
    {
        Run(AppRow(runMs: 0, stages: new Dictionary<string, long>
        {
            ["run-test-methods"] = 1,
            ["codeunit-instantiate"] = 1,
        }));

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Run(AppRow(
            runMs: 0,
            stages: new Dictionary<string, long> { ["run-test-methods"] = 20 })));
    }
}
