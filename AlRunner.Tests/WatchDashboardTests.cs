using System;
using System.Collections.Generic;
using Spectre.Console.Testing;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Pure render-model tests for the --watch live dashboard. The interactive loop
/// itself can't be unit-tested, so the view (BucketResult[] + status → renderable)
/// is factored out into <see cref="WatchDashboard"/> and exercised here against
/// Spectre.Console's <see cref="TestConsole"/>. No BC artifacts, runs fast.
/// </summary>
public class WatchDashboardTests
{
    private static string Render(IReadOnlyList<BucketResult> results, WatchStatus status,
        DateTime ts, TimeSpan dur, IReadOnlyList<(string Module, string Reason)>? fullRebuildReasons = null,
        int width = 120)
    {
        var console = new TestConsole();
        // Wide enough that the table columns aren't truncated away in the test.
        console.Profile.Width = width;
        console.Write(WatchDashboard.Build(results, "my-bundle", status, ts, dur, fullRebuildReasons));
        return console.Output;
    }

    private static BucketResult Bucket(params TestResult[] tests) =>
        new BucketResult("/tmp/my-bundle", BucketStage.Ran,
            Array.Empty<string>(), null, tests,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

    [Fact]
    public void Render_ShowsPassFailErrorRows_WithNamesAndCounts()
    {
        var results = new List<BucketResult>
        {
            Bucket(
                new TestResult("Codeunit1", "Pays", TestOutcome.Pass, null, null, TimeSpan.FromMilliseconds(12)),
                new TestResult("Codeunit1", "Validates", TestOutcome.Fail, "Assert.AreEqual failed: expected 1 got 9", null, TimeSpan.FromMilliseconds(34)),
                new TestResult("Codeunit2", "Posts", TestOutcome.Error, "NullReferenceException", null, TimeSpan.FromMilliseconds(56)))
        };

        var output = Render(results, WatchStatus.Idle,
            new DateTime(2026, 5, 29, 13, 5, 7, DateTimeKind.Local), TimeSpan.FromSeconds(4.2));

        // Header: bundle name + idle/watching status.
        Assert.Contains("my-bundle", output);
        Assert.Contains("watching", output);

        // Tree: a codeunit parent node, then a child node per test method.
        Assert.Contains("Codeunit1", output);
        Assert.Contains("Codeunit2", output);
        Assert.Contains("Pays", output);
        Assert.Contains("Validates", output);
        Assert.Contains("Posts", output);

        // Status labels.
        Assert.Contains("PASS", output);
        Assert.Contains("FAIL", output);
        Assert.Contains("ERROR", output);

        // Durations rendered as ms.
        Assert.Contains("12ms", output);
        Assert.Contains("34ms", output);

        // The failing test surfaces its full message under the method node.
        Assert.Contains("expected 1 got 9", output);

        // Footer counts: 1 pass / 1 fail / 1 error, 3 total.
        Assert.Contains("1P", output);
        Assert.Contains("1F", output);
        Assert.Contains("1E", output);
        // Footer hints the scroll/quit keys.
        Assert.Contains("quit", output);
    }

    [Fact]
    public void Render_UsesCodeunitDisplayName_NotTypeName()
    {
        var results = new List<BucketResult>
        {
            Bucket(
                new TestResult("Codeunit60208", "DispatchesEvent", TestOutcome.Pass, null, null,
                    TimeSpan.FromMilliseconds(8), AlCallStack: null,
                    CodeunitDisplayName: "Test Table Event Dispatch"))
        };

        var output = Render(results, WatchStatus.Idle, DateTime.Now, TimeSpan.FromSeconds(1));

        // The AL object NAME is shown as the codeunit node label …
        Assert.Contains("Test Table Event Dispatch", output);
        // … not the raw .NET type id.
        Assert.DoesNotContain("Codeunit60208", output);
        // The method still shows under it.
        Assert.Contains("DispatchesEvent", output);
    }

    [Fact]
    public void Render_FailingTest_ShowsFullStack()
    {
        const string stack =
            "\"Cust\"(CodeUnit 1).Validates line 5 - App by Pub version 1.0\n" +
            "\"Helper\"(CodeUnit 2).Check line 9 - App by Pub version 1.0";
        var results = new List<BucketResult>
        {
            Bucket(
                new TestResult("Codeunit1", "Validates", TestOutcome.Fail,
                    "NavTestFieldException: TestField failed for Amount",
                    "System.Exception full .net trace here", TimeSpan.FromMilliseconds(20),
                    AlCallStack: stack))
        };

        var output = Render(results, WatchStatus.Idle, DateTime.Now, TimeSpan.FromSeconds(1));

        // Full (untruncated) message.
        Assert.Contains("TestField failed for Amount", output);
        // Both AL call-stack frames appear (no truncation).
        Assert.Contains("Validates line 5", output);
        Assert.Contains("Check line 9", output);
    }

    [Fact]
    public void Render_FailingTest_FallsBackToFullException_WhenNoAlStack()
    {
        var results = new List<BucketResult>
        {
            Bucket(
                new TestResult("Codeunit1", "Boom", TestOutcome.Error,
                    "NullReferenceException", "NRE at SomeMethod\n  at OtherMethod",
                    TimeSpan.FromMilliseconds(3), AlCallStack: null))
        };

        var output = Render(results, WatchStatus.Idle, DateTime.Now, TimeSpan.FromSeconds(1));
        Assert.Contains("at SomeMethod", output);
        Assert.Contains("at OtherMethod", output);
    }

    [Fact]
    public void Render_RunningStatus_ShowsBusyMarker()
    {
        var results = new List<BucketResult>(); // first cold cycle: nothing yet
        var output = Render(results, WatchStatus.Running, DateTime.Now, TimeSpan.Zero);
        Assert.Contains("running", output);
        // The cold first run must not look frozen — busy state is explicit.
        Assert.DoesNotContain("watching", output);
    }

    /// <summary>
    /// #1905 (defect 4): when a cycle fell back to a full rebuild, the interactive
    /// dashboard must show WHY as a distinct frame element — not just a log line,
    /// because Program.cs's watch loop deliberately redirects Console.Out/Error to
    /// TextWriter.Null for the duration of the cycle body while the dashboard is
    /// active (see its "Clean loading (#5)" comment), so a log-line-only fix is
    /// invisible in exactly the mode it exists for. Assert the module name AND the
    /// specific cause both reach the rendered frame, not just a generic "rebuilt"
    /// marker — a generic marker would pass even if the actual reason never made it
    /// through.
    /// </summary>
    [Fact]
    public void Render_FullRebuildReason_ShowsBannerNamingModuleAndCause()
    {
        var results = new List<BucketResult> { Bucket() };
        var reasons = new List<(string Module, string Reason)>
        {
            ("MyTestApp", "app.json (identity/version/preprocessor symbols/features/help url) changed since the last cycle"),
        };

        // Wide enough that the reason line doesn't word-wrap mid-sentence — this test
        // asserts on the reason text as a contiguous substring, and the real dashboard
        // wraps at whatever the actual terminal width is, which this is standing in for.
        var output = Render(results, WatchStatus.Idle, DateTime.Now, TimeSpan.FromSeconds(842.5), reasons, width: 400);

        Assert.Contains("MyTestApp", output);
        Assert.Contains("app.json", output);
        Assert.Contains("changed since the last cycle", output);
        // Distinct from an ordinary compile/test failure row — a developer scanning the
        // frame must be able to tell "this cost minutes for a structural reason" apart
        // from "a test failed".
        Assert.Contains("FULL REBUILD", output);
    }

    /// <summary>
    /// The converse of the above: a proportional (incremental) cycle must show NO
    /// full-rebuild banner at all — not an empty one, not a "no reason" placeholder.
    /// "Present on a full-rebuild cycle, absent entirely on a proportional one" is the
    /// #1905 acceptance wording verbatim; a banner that's merely empty-but-rendered
    /// would still occupy dashboard real estate every single cycle and get trained
    /// into background noise exactly like the defect this fixes.
    /// </summary>
    [Fact]
    public void Render_NoFullRebuildReason_OmitsBannerEntirely()
    {
        var results = new List<BucketResult> { Bucket() };

        var withNull = Render(results, WatchStatus.Idle, DateTime.Now, TimeSpan.FromSeconds(2.1), fullRebuildReasons: null);
        var withEmpty = Render(results, WatchStatus.Idle, DateTime.Now, TimeSpan.FromSeconds(2.1),
            fullRebuildReasons: new List<(string Module, string Reason)>());

        Assert.DoesNotContain("FULL REBUILD", withNull);
        Assert.DoesNotContain("FULL REBUILD", withEmpty);
    }

    [Fact]
    public void Render_CompileFailure_ShowsErrorRow()
    {
        var results = new List<BucketResult>
        {
            new BucketResult("/tmp/my-bundle", BucketStage.CompileFailed,
                new[] { "AL0185: 'Foo' does not contain a definition for 'Bar'" }, null,
                Array.Empty<TestResult>(),
                TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.Zero)
        };

        var output = Render(results, WatchStatus.Idle, DateTime.Now, TimeSpan.FromSeconds(1));
        Assert.Contains("COMPILE", output);
        Assert.Contains("AL0185", output);
        // No tests ran, so a compile failure counts as one E in the footer roll-up.
        Assert.Contains("1E", output);
    }

    [Fact]
    public void Render_AllGreen_ShowsZeroFailures()
    {
        var results = new List<BucketResult>
        {
            Bucket(
                new TestResult("A", "One", TestOutcome.Pass, null, null, TimeSpan.FromMilliseconds(5)),
                new TestResult("A", "Two", TestOutcome.Pass, null, null, TimeSpan.FromMilliseconds(7)))
        };
        var output = Render(results, WatchStatus.Idle, DateTime.Now, TimeSpan.FromSeconds(2));
        Assert.Contains("2P", output);
        Assert.Contains("0F", output);
        Assert.Contains("0E", output);
    }
}
