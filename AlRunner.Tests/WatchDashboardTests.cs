using System;
using System.Collections.Generic;
using Spectre.Console.Testing;
using Xunit;

namespace AlRunnerV2.Tests;

/// <summary>
/// Pure render-model tests for the --watch live dashboard. The interactive loop
/// itself can't be unit-tested, so the view (BucketResult[] + status → renderable)
/// is factored out into <see cref="WatchDashboard"/> and exercised here against
/// Spectre.Console's <see cref="TestConsole"/>. No BC artifacts, runs fast.
/// </summary>
public class WatchDashboardTests
{
    private static string Render(IReadOnlyList<BucketResult> results, WatchStatus status,
        DateTime ts, TimeSpan dur)
    {
        var console = new TestConsole();
        // Wide enough that the table columns aren't truncated away in the test.
        console.Profile.Width = 120;
        console.Write(WatchDashboard.Build(results, "my-bundle", status, ts, dur));
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
                new TestResult("Cust", "Pays", TestOutcome.Pass, null, null, TimeSpan.FromMilliseconds(12)),
                new TestResult("Cust", "Validates", TestOutcome.Fail, "Assert.AreEqual failed: expected 1 got 9", null, TimeSpan.FromMilliseconds(34)),
                new TestResult("Vend", "Posts", TestOutcome.Error, "NullReferenceException", null, TimeSpan.FromMilliseconds(56)))
        };

        var output = Render(results, WatchStatus.Idle,
            new DateTime(2026, 5, 29, 13, 5, 7, DateTimeKind.Local), TimeSpan.FromSeconds(4.2));

        // Header: bundle name + idle/watching status.
        Assert.Contains("my-bundle", output);
        Assert.Contains("watching", output);

        // One row per test, fully-qualified name.
        Assert.Contains("Cust.Pays", output);
        Assert.Contains("Cust.Validates", output);
        Assert.Contains("Vend.Posts", output);

        // Status labels.
        Assert.Contains("PASS", output);
        Assert.Contains("FAIL", output);
        Assert.Contains("ERROR", output);

        // Durations rendered as ms.
        Assert.Contains("12", output);
        Assert.Contains("34", output);

        // The failing test surfaces its message.
        Assert.Contains("expected 1 got 9", output);

        // Footer counts: 1 pass / 1 fail / 1 error, 3 total.
        Assert.Contains("1P", output);
        Assert.Contains("1F", output);
        Assert.Contains("1E", output);
        Assert.Contains("Ctrl+C", output);
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
