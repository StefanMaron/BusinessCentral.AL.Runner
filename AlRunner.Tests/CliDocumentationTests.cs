using System.Diagnostics;
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
// See DefineFlagIntegrationTests for why this is serialized with the other
// runner-subprocess integration tests.
[Collection("server-serial")]
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

        foreach (var implemented in new[] { "--server", "--watch", "--define", "--auto-provision", "--output-json", "--output-junit" })
            Assert.False(notYet.Contains(implemented, StringComparison.Ordinal),
                $"{implemented} is implemented but listed under 'NOT YET IMPLEMENTED'.");
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

    /// <summary>Negative: an unknown documentation flag must not be silently accepted.</summary>
    [Fact]
    public void UnknownFlag_IsRejected()
    {
        var (exit, _, stderr) = RunCli("--guied");
        Assert.True(exit != 0, "A misspelled flag must not exit 0.");
        Assert.Contains("--guied", stderr, StringComparison.Ordinal);
    }
}
