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
    /// timestamp+duration), an optional full-rebuild-reason banner, a per-codeunit tree
    /// of test procedures, and a footer with P/F/E counts. Pure — no console side
    /// effects — so it is repaintable and testable.
    /// </summary>
    /// <param name="fullRebuildReasons">
    /// #1905 (defect 4): why the just-finished cycle recompiled the WHOLE module
    /// instead of the proportional-cost incremental path, one entry per module that
    /// fell back — e.g. ("MyApp", "app.json (identity/version/...) changed since the
    /// last cycle"). A full rebuild costs whole minutes on a large app, so a developer
    /// staring at "⟳ running…" for far longer than usual needs to know why, and the
    /// interactive dashboard is exactly the surface that deliberately swallows
    /// Console.Out/Error during the cycle body (see Program.cs's stdoutSilenced) — so
    /// logging the reason there is not enough, it has to be a frame element. Null or
    /// empty renders NOTHING (no row at all): present only on a full-rebuild cycle,
    /// absent entirely on a proportional one, so it can't be trained into background
    /// noise the way an always-present line would be.
    /// </param>
    public static IRenderable Build(
        IReadOnlyList<BucketResult> results,
        string bundleName,
        WatchStatus status,
        DateTime lastRun,
        TimeSpan lastDuration,
        IReadOnlyList<(string Module, string Reason)>? fullRebuildReasons = null)
    {
        var rows = new List<IRenderable>
        {
            Header(bundleName, status, lastRun, lastDuration),
            new Text(string.Empty),
        };
        var banner = FullRebuildBanner(fullRebuildReasons);
        if (banner != null)
        {
            rows.Add(banner);
            rows.Add(new Text(string.Empty));
        }
        rows.Add(BuildTree(results));
        rows.Add(new Text(string.Empty));
        rows.Add(Footer(results));
        return new Rows(rows);
    }

    /// <summary>
    /// One line per module that fell back to a full rebuild this cycle, naming the
    /// cause verbatim from BcCompiler.Incremental.cs — e.g. "app.json (identity/
    /// version/...) changed since the last cycle". Null when nothing fell back, so
    /// <see cref="Build"/> can omit the row entirely rather than render an empty panel.
    /// </summary>
    private static IRenderable? FullRebuildBanner(IReadOnlyList<(string Module, string Reason)>? reasons)
    {
        if (reasons == null || reasons.Count == 0) return null;
        var lines = reasons.Select(r =>
            $"[yellow]⚠ FULL REBUILD[/] [blue]{Markup.Escape(r.Module)}[/]: {Markup.Escape(r.Reason)}");
        return new Markup(string.Join("\n", lines));
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
                // #2779: same sibling gap as Reporter.PrintPerTest's EXEC FAIL branch — an
                // in-process bundle that failed at run time has no ProcessError, and its
                // diagnosis is in the suite-error list. Without this the dashboard printed the
                // placeholder "execution failed" and nothing else.
                if (b.ProcessError != null) node.AddNode($"[red]{Markup.Escape(b.ProcessError)}[/]");
                foreach (var err in b.CompileErrors)
                    node.AddNode($"[red]{Markup.Escape(err)}[/]");
                if (b.ProcessError == null && b.CompileErrors.Count == 0)
                    node.AddNode("[red]execution failed[/]");
                continue;
            }

            // #2762: a bucket that RAN but lost one or more suites. --watch has no exit code,
            // so this tree and the footer below ARE the verdict — a lost suite that appears in
            // neither reads as a clean cycle. Rendered as its own node rather than folded into
            // COMPILE FAILED, because the bucket's surviving tests are real and still listed.
            if (b.CompileErrors.Count > 0)
            {
                any = true;
                var lostLabel = Markup.Escape(Path.GetFileName(b.BucketPath));
                var lostNode = tree.AddNode(
                    $"[blue]{lostLabel}[/]  [red]SUITE ERRORS ({b.CompileErrors.Count})[/]");
                foreach (var err in b.CompileErrors)
                    lostNode.AddNode($"[red]{Markup.Escape(err)}[/]");
                lostNode.AddNode(
                    "[red]the tests these suites declare are MISSING from this cycle, not passing[/]");
                // #2880: and the results that DID run in this bucket are not trustworthy either
                // — an object the lost suite declared makes an unrelated test fail. --watch has
                // no exit code and no summary to scroll to, so a red FAIL node beside a red
                // SUITE ERRORS node otherwise reads as two independent problems.
                lostNode.AddNode(
                    "[red]FAIL/ERROR nodes in this bucket are marked suspect: they may be "
                    + "collateral, not real failures[/]");
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
                        TestOutcome.Skipped => ("SKIP", "grey"),
                        _ => ("?", "grey"),
                    };
                    long ms = (long)t.Duration.TotalMilliseconds;
                    // #2880: only fail/error, only in a bucket that lost suites. A missing
                    // object manufactures failures, not passes, and an intact bucket in the same
                    // cycle keeps reporting exactly what it measured.
                    var suspectSuffix = Reporter.IsSuspect(b, t)
                        ? $"  ·  [red]{Markup.Escape(Reporter.SuspectMarker(b))}[/]"
                        : "";
                    var methodNode = cuNode.AddNode(
                        $"[{color}]{Markup.Escape(t.Method)}[/]  ·  [{color}]{label}[/]  ·  [grey]{ms}ms[/]{suspectSuffix}");

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
    /// #2762 extends the same convention to a bucket that RAN but lost suites: one error per
    /// lost suite, matching its SUITE ERRORS tree node. Without it the footer of a cycle that
    /// dropped a whole app read a clean `1P / 0F / 0E`, and under --watch there is no exit
    /// code to disagree with it. This roll-up is dashboard-only — it never feeds the exit code
    /// or Reporter's `Tests:` totals, which keep counting only tests that actually ran.
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
            err += b.CompileErrors.Count;
            foreach (var t in b.Tests)
            {
                switch (t.Outcome)
                {
                    case TestOutcome.Pass: pass++; break;
                    case TestOutcome.Fail: fail++; break;
                    case TestOutcome.Skipped: break;   // manifest-declared skip; not an error
                    default: err++; break;
                }
            }
        }
        return (pass, fail, err, pass + fail + err);
    }
}
