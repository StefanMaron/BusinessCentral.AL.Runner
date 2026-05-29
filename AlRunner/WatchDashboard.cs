// WatchDashboard — the pure view-model for `--watch`'s live, in-place dashboard.
//
// The interactive watch loop (Program.cs) drives AnsiConsole.Live with the
// IRenderable that Build() returns, repainting it on every cycle. Keeping the
// rendering pure (results + status → renderable) is what makes it unit-testable:
// WatchDashboardTests renders Build() to a Spectre.Console TestConsole string and
// asserts on the rows/counts, with no BC artifacts and no live terminal.
//
// On a non-interactive stdout (CI, a pipe, VS Code, the WatchTests harness) the
// loop does NOT use this — it falls back to Reporter.PrintPerTest/PrintSummary so
// the existing line markers ("PASS"/"FAIL", "[watch] waiting for AL source") keep
// working. See Program.cs for that branch.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AlRunnerV2;

/// <summary>Whether a watch cycle is in flight (compiling + running) or idle, waiting for edits.</summary>
public enum WatchStatus { Running, Idle }

public static class WatchDashboard
{
    // Truncate long messages so a single failure can't blow up the row height.
    private const int MaxMessageLen = 90;

    /// <summary>
    /// Builds the full dashboard renderable: header (bundle · status · last-run
    /// timestamp+duration), a per-test table, and a footer with P/F/E counts.
    /// Pure — no console side effects — so it is repaintable and testable.
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
            BuildTable(results),
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

    private static IRenderable BuildTable(IReadOnlyList<BucketResult> results)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Expand();
        table.AddColumn("[bold]Test[/]");
        table.AddColumn("[bold]Status[/]");
        table.AddColumn(new TableColumn("[bold]ms[/]").RightAligned());
        table.AddColumn("[bold]Message[/]");

        bool any = false;
        foreach (var b in results)
        {
            if (b.Stage == BucketStage.CompileFailed)
            {
                any = true;
                var first = b.CompileErrors.FirstOrDefault() ?? "compile failed";
                table.AddRow(
                    new Markup($"[blue]{Markup.Escape(Path.GetFileName(b.BucketPath))}[/]"),
                    new Markup("[red]COMPILE[/]"),
                    new Markup("[grey]—[/]"),
                    new Markup($"[red]{Markup.Escape(Truncate(first))}[/]"));
                continue;
            }
            if (b.Stage == BucketStage.ExecuteFailed)
            {
                any = true;
                table.AddRow(
                    new Markup($"[blue]{Markup.Escape(Path.GetFileName(b.BucketPath))}[/]"),
                    new Markup("[red]EXEC[/]"),
                    new Markup("[grey]—[/]"),
                    new Markup($"[red]{Markup.Escape(Truncate(b.ProcessError ?? "execution failed"))}[/]"));
                continue;
            }

            foreach (var t in b.Tests)
            {
                any = true;
                var (label, color) = t.Outcome switch
                {
                    TestOutcome.Pass => ("PASS", "green"),
                    TestOutcome.Fail => ("FAIL", "red"),
                    TestOutcome.Error => ("ERROR", "yellow"),
                    _ => ("?", "grey"),
                };
                long ms = (long)t.Duration.TotalMilliseconds;
                var msg = t.Outcome == TestOutcome.Pass ? "" : Truncate(t.Message ?? "");
                table.AddRow(
                    new Markup(Markup.Escape($"{t.Codeunit}.{t.Method}")),
                    new Markup($"[{color}]{label}[/]"),
                    new Markup(ms.ToString()),
                    new Markup($"[{color}]{Markup.Escape(msg)}[/]"));
            }
        }

        if (!any)
            table.AddRow(new Markup("[grey]no results yet…[/]"), new Markup(""), new Markup(""), new Markup(""));

        return table;
    }

    private static IRenderable Footer(IReadOnlyList<BucketResult> results)
    {
        var (pass, fail, err, total) = Tally(results);
        var line =
            $"[green]{pass}P[/] / [red]{fail}F[/] / [yellow]{err}E[/]  ·  {total} total" +
            "    [grey]Ctrl+C to quit[/]";
        return new Markup(line);
    }

    /// <summary>
    /// Roll-up counts. A compile- or exec-failed bucket has no per-test rows, so it
    /// counts as one error in the footer (consistent with the COMPILE/EXEC table row).
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

    private static string Truncate(string s)
    {
        s = (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= MaxMessageLen ? s : s[..(MaxMessageLen - 1)] + "…";
    }
}
