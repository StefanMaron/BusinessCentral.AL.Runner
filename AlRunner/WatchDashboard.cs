// WatchDashboard — the pure view-model for `--watch`'s live, in-place dashboard.
//
// The interactive watch loop (Program.cs) drives the terminal with the IRenderable
// that Build() returns, repainting it on every cycle (and on scroll keypresses).
// Keeping the rendering pure (results + status → renderable) is what makes it
// unit-testable: WatchDashboardTests renders Build() to a Spectre.Console TestConsole
// string and asserts on the rows/counts, with no BC artifacts and no live terminal.
//
// On a non-interactive stdout (CI, a pipe, VS Code, the WatchTests harness) the
// loop does NOT use this — it falls back to Reporter.PrintPerTest/PrintSummary so
// the existing line markers ("PASS"/"FAIL", "[watch] waiting for AL source") keep
// working. See Program.cs for that branch.
//
// Layout: a header panel, then a Tree of test codeunits → their test procedures
// (and, under a failing procedure, its full message + AL call stack), then a footer.
// The tree replaces the old flat table so the codeunit→procedure hierarchy is visible
// and full call stacks can be shown without blowing up a single row.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AlRunner;

/// <summary>Whether a watch cycle is in flight (compiling + running) or idle, waiting for edits.</summary>
public enum WatchStatus { Running, Idle }

public static class WatchDashboard
{
    /// <summary>
    /// Builds the full dashboard renderable: header (bundle · status · last-run
    /// timestamp+duration), a per-codeunit tree of test procedures, and a footer with
    /// P/F/E counts. Pure — no console side effects — so it is repaintable and testable.
    /// </summary>
    public static IRenderable Build(
        IReadOnlyList<BucketResult> results,
        string bundleName,
        WatchStatus status,
        DateTime lastRun,
        TimeSpan lastDuration)
    {
        var rows = new List<IRenderable>
        {
            Header(bundleName, status, lastRun, lastDuration),
            new Text(string.Empty),
            BuildTree(results),
            new Text(string.Empty),
            Footer(results),
        };
        return new Rows(rows);
    }

    private static IRenderable Header(string bundleName, WatchStatus status,
        DateTime lastRun, TimeSpan lastDuration)
    {
        // ● watching (green) when idle; ⟳ running… (yellow) while a cycle is in flight.
        // The busy marker is essential so the cold first run (~70-90s) doesn't look frozen.
        var statusMarkup = status == WatchStatus.Running
            ? "[yellow]⟳ running…[/]"
            : "[green]● watching[/]";

        var lastRunPart = status == WatchStatus.Running
            ? "[grey]—[/]"
            : $"[grey]last run {Markup.Escape(lastRun.ToString("HH:mm:ss"))} · {lastDuration.TotalSeconds:F1}s[/]";

        var line = $"[bold]al-runner[/] [blue]{Markup.Escape(bundleName)}[/]  ·  {statusMarkup}  ·  {lastRunPart}";
        return new Panel(new Markup(line))
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static IRenderable BuildTree(IReadOnlyList<BucketResult> results)
    {
        var tree = new Tree("[bold]Tests[/]");

        bool any = false;
        foreach (var b in results)
        {
            if (b.Stage == BucketStage.CompileFailed)
            {
                any = true;
                var bucketLabel = Markup.Escape(Path.GetFileName(b.BucketPath));
                var node = tree.AddNode($"[blue]{bucketLabel}[/]  [red]COMPILE FAILED[/]");
                foreach (var err in (b.CompileErrors.Count > 0 ? b.CompileErrors : new[] { "compile failed" }))
                    node.AddNode($"[red]{Markup.Escape(err)}[/]");
                continue;
            }
            if (b.Stage == BucketStage.ExecuteFailed)
            {
                any = true;
                var bucketLabel = Markup.Escape(Path.GetFileName(b.BucketPath));
                var node = tree.AddNode($"[blue]{bucketLabel}[/]  [red]EXEC FAILED[/]");
                node.AddNode($"[red]{Markup.Escape(b.ProcessError ?? "execution failed")}[/]");
                continue;
            }

            // Group this bucket's tests by codeunit so each codeunit is one parent node.
            // (A bucket normally maps to one bundle but may contain several codeunits.)
            var byCodeunit = b.Tests
                .GroupBy(t => t.Codeunit, StringComparer.Ordinal);

            foreach (var group in byCodeunit)
            {
                any = true;
                var tests = group.ToList();
                int p = tests.Count(t => t.Outcome == TestOutcome.Pass);
                int f = tests.Count(t => t.Outcome == TestOutcome.Fail);
                int e = tests.Count(t => t.Outcome == TestOutcome.Error);

                var display = DisplayName(tests[0]);
                var rollup = $"[green]{p}P[/] / [red]{f}F[/] / [yellow]{e}E[/]";
                var cuNode = tree.AddNode($"[blue]{Markup.Escape(display)}[/]  {rollup}");

                foreach (var t in tests)
                {
                    var (label, color) = t.Outcome switch
                    {
                        TestOutcome.Pass => ("PASS", "green"),
                        TestOutcome.Fail => ("FAIL", "red"),
                        TestOutcome.Error => ("ERROR", "yellow"),
                        _ => ("?", "grey"),
                    };
                    long ms = (long)t.Duration.TotalMilliseconds;
                    var methodNode = cuNode.AddNode(
                        $"[{color}]{Markup.Escape(t.Method)}[/]  ·  [{color}]{label}[/]  ·  [grey]{ms}ms[/]");

                    if (t.Outcome != TestOutcome.Pass)
                    {
                        // Full message (no truncation), then the full AL call stack
                        // (preferred) or the .NET exception as fallback, one child line each.
                        var msg = (t.Message ?? "").Trim();
                        if (msg.Length > 0)
                            methodNode.AddNode($"[{color}]{Markup.Escape(msg)}[/]");

                        var stack = !string.IsNullOrWhiteSpace(t.AlCallStack)
                            ? t.AlCallStack
                            : t.FullException;
                        if (!string.IsNullOrWhiteSpace(stack))
                        {
                            var stackNode = methodNode.AddNode("[grey]stack[/]");
                            foreach (var frame in SplitStack(stack!))
                                stackNode.AddNode($"[grey]{Markup.Escape(frame)}[/]");
                        }
                    }
                }
            }
        }

        if (!any)
            tree.AddNode("[grey]no results yet…[/]");

        return tree;
    }

    /// <summary>AL object name when resolved, else the .NET codeunit type name.</summary>
    private static string DisplayName(TestResult t) =>
        !string.IsNullOrWhiteSpace(t.CodeunitDisplayName) ? t.CodeunitDisplayName! : t.Codeunit;

    private static IEnumerable<string> SplitStack(string stack) =>
        stack.Replace("\r\n", "\n").Replace("\r", "\n")
             .Split('\n')
             .Select(l => l.TrimEnd())
             .Where(l => l.Length > 0);

    private static IRenderable Footer(IReadOnlyList<BucketResult> results)
    {
        var (pass, fail, err, total) = Tally(results);
        var line =
            $"[green]{pass}P[/] / [red]{fail}F[/] / [yellow]{err}E[/]  ·  {total} total" +
            "    [grey]↑↓ scroll · q quit[/]";
        return new Markup(line);
    }

    /// <summary>
    /// Roll-up counts. A compile- or exec-failed bucket has no per-test rows, so it
    /// counts as one error in the footer (consistent with the COMPILE/EXEC tree node).
    /// </summary>
    internal static (int Pass, int Fail, int Err, int Total) Tally(IReadOnlyList<BucketResult> results)
    {
        int pass = 0, fail = 0, err = 0;
        foreach (var b in results)
        {
            if (b.Stage == BucketStage.CompileFailed || b.Stage == BucketStage.ExecuteFailed)
            {
                err++;
                continue;
            }
            foreach (var t in b.Tests)
            {
                switch (t.Outcome)
                {
                    case TestOutcome.Pass: pass++; break;
                    case TestOutcome.Fail: fail++; break;
                    default: err++; break;
                }
            }
        }
        return (pass, fail, err, pass + fail + err);
    }
}
