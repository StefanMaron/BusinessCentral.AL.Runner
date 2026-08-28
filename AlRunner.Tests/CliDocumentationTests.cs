using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// The CLI's self-documentation is the ONLY thing an agent handed a bare
/// al-runner binary can read. If a flag is implemented but undocumented, or the
/// help contradicts itself, the agent invents an explanation for the resulting
/// failure — which is how "al-runner cannot run the suite (a rewriter
/// limitation)" got asserted about a runner that runs ~1000 Pageworks tests.
///
/// These tests pin the help/guide surface against drift. They exercise the real
/// CLI (both flags are handled before any BC type loads, so this is fast).
/// </summary>
// See DefineFlagIntegrationTests for why this used to be [Collection("server-serial")]
// and no longer is — #1809.
public sealed class CliDocumentationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string CurrentFramework()
    {
        var v = Environment.Version;
        return $"net{v.Major}.{v.Minor}";
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var argLine = TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")) + $" {string.Join(' ', args)}";
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = argLine,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        // Drain BOTH pipes concurrently. Reading stdout to the end first and stderr only
        // afterwards deadlocks: the runner writes heavily to stderr ([Cecil], [cache],
        // [deps], [emit-timing]), so once that 64K pipe buffer fills the child blocks on
        // its stderr write, never exits, and never closes stdout — so ReadToEnd() on
        // stdout never returns and the WaitForExit timeout below is never even reached.
        //
        // The window is widest right after AlRunner is rebuilt: a new runner assembly
        // changes the Ncl Cecil cache key, so the next invocation does a fresh rewrite and
        // re-execs itself, and that re-exec child inherits these same pipes. That is the
        // multi-hour "suite hangs after Starting test execution" with al-runner children
        // parked in Process.WaitForExit().
        //
        // The other runner-subprocess tests already use this async pattern; this one was
        // the last holdout.
        using var proc = Process.Start(psi)!;
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(240_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"al-runner did not exit within 240s for args: {string.Join(' ', args)}");
        }
        proc.WaitForExit(); // flush the async readers
        lock (outSb) lock (errSb) return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    /// <summary>
    /// Every long flag the argument parser recognises must appear in --help.
    ///
    /// Scraping Program.cs rather than reflecting is deliberate: the parser lives
    /// in top-level statements, so there is no symbol to reflect over. The scrape
    /// is what caught --auto-provision and --emit-app being implemented-but-
    /// undocumented, and it is what will catch the next one.
    /// </summary>
    [Fact]
    public void Help_DocumentsEveryRecognizedFlag()
    {
        var programSource = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Program.cs"));

        // Flags the parser compares against, minus the bare "--" argument separator.
        var recognized = Regex.Matches(programSource, "\"(--[a-z][a-z-]*)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Where(f => f != "--")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(recognized);

        var (exit, help, stderr) = RunCli("--help");
        Assert.True(exit == 0, $"--help must exit 0. exit={exit}\n{stderr}");

        var undocumented = recognized.Where(f => !help.Contains(f, StringComparison.Ordinal)).ToList();

        Assert.True(undocumented.Count == 0,
            "These flags are recognized by the parser but absent from --help. An agent "
            + "reading --help cannot discover them:\n  " + string.Join("\n  ", undocumented));
    }

    /// <summary>
    /// The `provision` subcommand is the only supported way to obtain BC artifacts
    /// (the runner never auto-downloads). If --help omits it, an agent on a machine
    /// without artifacts has no path forward.
    /// </summary>
    [Fact]
    public void Help_DocumentsProvisionSubcommand()
    {
        var (_, help, _) = RunCli("--help");
        Assert.Contains("provision", help, StringComparison.Ordinal);
    }

    /// <summary>
    /// --server is fully implemented (docs/server-mode.md, ServerTests.cs) but was
    /// also listed under "NOT YET IMPLEMENTED". A reader who hits the second list
    /// concludes the runner is crippled. Self-contradiction is worse than silence.
    /// </summary>
    [Fact]
    public void Help_DoesNotListImplementedFlagsAsUnimplemented()
    {
        var (_, help, _) = RunCli("--help");

        var idx = help.IndexOf("NOT YET IMPLEMENTED", StringComparison.Ordinal);
        Assert.True(idx >= 0, "--help should keep a 'NOT YET IMPLEMENTED' section.");
        var notYet = help[idx..];

        foreach (var implemented in new[] { "--server", "--watch", "--define", "--auto-provision", "--output-json", "--output-junit", "--tdd" })
            Assert.False(notYet.Contains(implemented, StringComparison.Ordinal),
                $"{implemented} is implemented but listed under 'NOT YET IMPLEMENTED'.");
    }

    /// <summary>
    /// --help is user-facing documentation, not a source-comment scratchpad. A flag
    /// whose behavior --help calls "TODO" is either not shipped (and must not claim
    /// otherwise) or is shipped and the text just never got updated — issue #2118:
    /// `--server`'s `execute` command shipped and returns real results (tests,
    /// `capturedValues`, `coverage`, and — as of #2117/#2120 — `messages`), but
    /// --help still read "Commands: runTests, shutdown (execute: TODO)" for multiple
    /// releases of the thing it describes. Neither the flag-name scrape
    /// (<see cref="Help_DocumentsEveryRecognizedFlag"/>) nor the "not listed as
    /// unimplemented" check (<see cref="Help_DoesNotListImplementedFlagsAsUnimplemented"/>)
    /// would have caught this: `execute` is not a `--flag`, and the stale text lived
    /// under EXECUTION, not under "NOT YET IMPLEMENTED". A blanket "no literal TODO"
    /// guard catches this whole class without needing to know which command it names.
    /// </summary>
    [Fact]
    public void Help_NeverMarksAShippedCommandAsTodo()
    {
        var (_, help, _) = RunCli("--help");
        Assert.DoesNotContain("TODO", help, StringComparison.Ordinal);

        var (_, guide, _) = RunCli("--guide");
        Assert.DoesNotContain("TODO", guide, StringComparison.Ordinal);
    }

    /// <summary>
    /// --dap is documented in full later in the flag list (its own EXECUTION entries,
    /// with docs/dap-mode.md), but the USAGE synopsis at the very top of --help never
    /// mentioned it (issue #2118) even though every other mode flag with its own
    /// invocation shape (--server, --precompile, --emit-app) got a synopsis line. An
    /// agent that only skims USAGE before scrolling to the flag it already knows the
    /// name of would not learn --dap exists at all.
    /// </summary>
    [Fact]
    public void Usage_SynopsisListsDap()
    {
        var (_, help, _) = RunCli("--help");
        var usageIdx = help.IndexOf("USAGE", StringComparison.Ordinal);
        var selectionIdx = help.IndexOf("SELECTION", StringComparison.Ordinal);
        Assert.True(usageIdx >= 0 && selectionIdx > usageIdx, "--help should keep USAGE/SELECTION sections.");
        var usage = help[usageIdx..selectionIdx];
        Assert.Contains("--dap", usage, StringComparison.Ordinal);
    }

    /// <summary>
    /// --dap (issue #1642, stdio transport in #2058) is fully implemented and already
    /// documented as such earlier in --help, but "NOT YET IMPLEMENTED" carried a stale
    /// "(debug adapter)" bullet dating from before --dap existed (issue #2118), naming
    /// a DapServer.cs that has since been deleted from the repository entirely. The
    /// curated-flag-name check in <see cref="Help_DoesNotListImplementedFlagsAsUnimplemented"/>
    /// does not catch this shape of staleness: the stale bullet never spells out the
    /// literal flag ("--dap") it is actually describing, so a flag-name-based
    /// allowlist has nothing to match against.
    /// </summary>
    [Fact]
    public void Help_DoesNotDescribeDapAsUnimplemented()
    {
        var (_, help, _) = RunCli("--help");
        var idx = help.IndexOf("NOT YET IMPLEMENTED", StringComparison.Ordinal);
        Assert.True(idx >= 0, "--help should keep a 'NOT YET IMPLEMENTED' section.");
        var notYet = help[idx..];

        Assert.DoesNotContain("debug adapter", notYet, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DapServer", notYet, StringComparison.Ordinal);
    }

    /// <summary>
    /// --guide is advertised in CLAUDE.md and the al-runner-workflow skill. It must
    /// actually exist, and it must answer the questions an agent gets wrong when it
    /// only has the binary: how to invoke against a real app + test app, where deps
    /// come from, and what the common failure signatures mean.
    /// </summary>
    [Fact]
    public void Guide_ExistsAndCoversInvocationEssentials()
    {
        var (exit, guide, stderr) = RunCli("--guide");
        Assert.True(exit == 0, $"--guide must exit 0. exit={exit}\n{stderr}");

        foreach (var required in new[]
        {
            "INVOCATION",          // the minimal correct command line
            "DEPENDENCIES",        // where .app deps are resolved from
            "TROUBLESHOOTING",     // failure signature -> meaning
            "does not have a member with that ID",  // the ID-0 signature specifically
            "symbols-only",        // the distinction that makes "it compiled" not mean "it can run"
            "HIGHEST VERSION",     // how the winning package is actually chosen
            "[dep]",               // the mechanical check that replaces hand-auditing .app files
            "Do NOT infer it from the app under test",  // --bc-version is not the app's version
        })
            Assert.True(guide.Contains(required, StringComparison.Ordinal),
                $"--guide must cover '{required}'. Got:\n{guide}");
    }

    /// <summary>
    /// The guide must state plainly that the runner executes full suites. Absent a
    /// positive claim, an agent hitting any failure is free to conclude the runner
    /// "can't run tests" — which is the exact misdiagnosis this suite exists to stop.
    /// </summary>
    [Fact]
    public void Guide_StatesThatFullSuitesExecute()
    {
        var (_, guide, _) = RunCli("--guide");
        Assert.Contains("CAPABILITY", guide, StringComparison.Ordinal);
    }

    /// <summary>
    /// Flags that materially change WHAT A RUN DOES — not merely how output is formatted —
    /// must be covered in --guide, because CLAUDE.md and the al-runner-workflow skill both
    /// tell an agent to start with --guide. --help alone is not sufficient: --tdd was fully
    /// documented in --help (<see cref="Help_DocumentsEveryRecognizedFlag"/> already passed)
    /// and STILL went undiscovered by an agent, because nothing ever pointed it at --guide's
    /// existence for that flag (issue #2001).
    /// <para>
    /// Deliberately NOT every recognized flag — <see cref="Help_DocumentsEveryRecognizedFlag"/>
    /// already owns that broader surface, and most flags there are output-shape switches
    /// (--output-json, --quiet, --no-strict-exit, …) that don't need a guide section.
    /// <see cref="BehaviorChangingFlags"/> is the curated set that DOES, and the check below
    /// is a loop over it rather than one hardcoded <c>Assert.Contains</c> per flag — the
    /// previous shape (<see cref="Guide_ExistsAndCoversInvocationEssentials"/>'s fixed list
    /// of unrelated prose fragments) could never notice a new flag went undocumented, because
    /// nothing connected that list to the flags the parser actually recognizes. Adding a flag
    /// to <see cref="BehaviorChangingFlags"/> is now the ONLY step a future PR needs for this
    /// gate to catch it — an omission there is a decision made in one auditable place, not a
    /// gap nobody wrote a test for.
    /// </para>
    /// </summary>
    [Fact]
    public void Guide_CoversEveryBehaviorChangingFlag()
    {
        var (exit, guide, stderr) = RunCli("--guide");
        Assert.True(exit == 0, $"--guide must exit 0. exit={exit}\n{stderr}");

        var missing = BehaviorChangingFlags
            .Where(f => !guide.Contains(f, StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "These behavior-changing flags are missing from --guide. An agent that starts "
            + "with --guide (per CLAUDE.md / the al-runner-workflow skill) has no way to "
            + "discover them exist, even if --help documents them:\n  "
            + string.Join("\n  ", missing));
    }

    /// <summary>
    /// The curated set <see cref="Guide_CoversEveryBehaviorChangingFlag"/> checks against.
    /// A flag belongs here when passing it changes WHICH CODE PATH a run takes or WHAT GETS
    /// EXECUTED (compiles against different source, runs a daemon instead of one-shot,
    /// changes the compile symbol set, generates code, watches for changes, …) — not when it
    /// only reshapes how an unchanged run's result is reported.
    /// </summary>
    private static readonly string[] BehaviorChangingFlags =
    {
        "--tdd", "--watch", "--server", "--define", "--auto-provision", "--no-auto-provision", "--no-cache",
    };

    /// <summary>Negative: an unknown documentation flag must not be silently accepted.</summary>
    [Fact]
    public void UnknownFlag_IsRejected()
    {
        var (exit, _, stderr) = RunCli("--guied");
        Assert.True(exit != 0, "A misspelled flag must not exit 0.");
        Assert.Contains("--guied", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// --help accepts three spellings (--help, -h, help). --version accepted only
    /// one, so `al-runner -v` fell through to the bundle-path parser and produced
    /// an unrelated "directory not found" error instead of pointing at --version
    /// (issue #2072). -v, -V and bare "version" must all print the identical
    /// version string and exit 0, same as --version itself.
    /// </summary>
    [Fact]
    public void Version_AcceptsAllDocumentedSpellings()
    {
        var (baseExit, baseOut, baseErr) = RunCli("--version");
        Assert.True(baseExit == 0, $"--version must exit 0. exit={baseExit}\n{baseErr}");
        Assert.Matches(new Regex(@"^al-runner v\S+"), baseOut.Trim());

        foreach (var spelling in new[] { "-v", "-V", "version" })
        {
            var (exit, stdout, stderr) = RunCli(spelling);
            Assert.True(exit == 0, $"'{spelling}' must exit 0 like --version. exit={exit}\n{stderr}");
            Assert.Equal(baseOut.Trim(), stdout.Trim());
        }
    }

    /// <summary>
    /// Negative: accepting -v/-V/version must not turn into accepting arbitrary
    /// short flags. An unrecognized single-dash flag still falls through to the
    /// existing bundle-path handling and fails loud, not silently as version 0.
    /// </summary>
    [Fact]
    public void Version_UnrecognizedShortFlag_StillFailsAsBefore()
    {
        var (exit, stdout, _) = RunCli("-q");
        Assert.True(exit != 0, "An unrecognized short flag must not exit 0.");
        Assert.DoesNotContain("al-runner v", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// --help never printed which build is running (issue #2072). Someone pasting
    /// --help output into a gap report should carry their runner version with it
    /// without being asked separately for --version's output too.
    /// </summary>
    [Fact]
    public void Help_PrintsVersionAsFirstLine()
    {
        var (_, version, _) = RunCli("--version");
        var (exit, help, stderr) = RunCli("--help");
        Assert.True(exit == 0, $"--help must exit 0. exit={exit}\n{stderr}");

        var firstLine = help.Split('\n', 2)[0].Trim();
        Assert.Equal(version.Trim(), firstLine);
    }

    /// <summary>
    /// The REPORTING A RUNNER GAP section is the replacement for telemetry (#1643,
    /// closed as not-planned). It must actually be able to produce a report: a
    /// caller with only the binary needs the repository URL, and the section must
    /// tell the agent to ask its human for permission before posting anything —
    /// without that instruction the only two behaviors left are "post without
    /// asking" and "say nothing and work around it silently", both wrong per
    /// .claude/rules/file-issues-for-gaps.md.
    /// </summary>
    [Fact]
    public void Guide_ReportingSectionCoversWhereAndPermission()
    {
        var (exit, guide, stderr) = RunCli("--guide");
        Assert.True(exit == 0, $"--guide must exit 0. exit={exit}\n{stderr}");

        var idx = guide.IndexOf("REPORTING A RUNNER GAP", StringComparison.Ordinal);
        Assert.True(idx >= 0, "--guide should keep a 'REPORTING A RUNNER GAP' section.");
        var section = guide[idx..];

        Assert.Contains("github.com/StefanMaron/BusinessCentral.AL.Runner", section, StringComparison.Ordinal);
        Assert.Contains("ask", section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission", section, StringComparison.OrdinalIgnoreCase);
        // The existing constraint against naming an unsupported cause must survive
        // alongside the new instructions, not be displaced by them.
        Assert.Contains("worse than none", section, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #2085: `dotnet run --project tools/DownloadArtifacts` requires a source
    /// checkout of this repository. A `dotnet tool install -g msdyn365bc.al.runner` user
    /// has none — `tools/DownloadArtifacts` ships only as source, never as part of the
    /// packaged tool — so a provisioning-gap message naming it as a fix is a dead end for
    /// exactly the audience it's printed for (measured on the published 2.7.0: two of the
    /// three "Resolve it" routes were unusable). `al-runner provision
    /// --platform-apps/--test-apps/--service-tier [--force]` is the tool-install-valid
    /// replacement every remediation message must use instead.
    ///
    /// Scans the actual source that BUILDS these messages, the same style as
    /// <see cref="Help_DocumentsEveryRecognizedFlag"/>'s Program.cs scrape, rather than
    /// driving every message site through a real provisioning-gap scenario. Comment-only
    /// lines are excluded — they legitimately document the history/rationale (e.g. this
    /// very test's own doc comment) — only live code lines that could actually reach a
    /// console/exception message count.
    /// </summary>
    [Fact]
    public void NoRemediationText_NamesTheCheckoutOnlyDownloadArtifactsInvocation()
    {
        var root = Path.Combine(RepoRoot, "AlRunner");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//")) continue; // comment: rationale, never emitted
                if (lines[i].Contains("dotnet run --project", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "These lines under AlRunner/ (the shipped binary's own source) build "
            + "remediation text containing 'dotnet run --project', which requires a source "
            + "checkout a `dotnet tool install` user never has:\n  "
            + string.Join("\n  ", offenders));
    }
}
