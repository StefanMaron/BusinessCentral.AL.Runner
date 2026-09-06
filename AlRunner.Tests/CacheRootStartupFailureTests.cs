// CacheRootStartupFailureTests — issue #3111, the review follow-up to #3104.
//
// #3104 rooted a relative `--cache` once at parse time and added a loud guard to
// CacheRoots.Resolve. Its reviewer left three findings, all confirmed against the merged
// commit (82d7fe22) before anything here was written:
//
//   1. Program.cs's `CacheRoots.DisableForRun()` call sat outside any try, and that method
//      now calls Path.GetFullPath on the RAW AL_RUNNER_NO_CACHE_ROOT value. A value
//      GetFullPath rejects therefore aborted out of top-level statements. Measured on the
//      merged build, before the fix in this PR:
//
//        Unhandled exception. System.IO.FileNotFoundException: Unable to find the specified file.
//           at Interop.Sys.GetCwd()
//           at System.IO.Path.GetFullPathInternal(String path)
//           at AlRunner.Infrastructure.CacheRoots.DisableForRun() in .../CacheRoots.cs:line 151
//           at Program.<Main>$(String[] args) in .../Program.cs:line 1364
//        EXIT=134
//
//      while the `--cache` flag, three hundred lines up, returned the documented exit 2 for
//      the identical failure. Exit 134 instead of a documented exit is the worse half of
//      #2114, which #3104's own commit message cites.
//
//   2. Two branches with no coverage: `--cache` given a value GetFullPath rejects, and the
//      alCacheDir rooting itself. Every `--cache` caller in AlRunner.Tests passes an
//      ABSOLUTE path, so neither branch was ever entered.
//
//   3. alCacheDir does not flow through CacheRoots at all (that class's "Deliberately NOT
//      wired into alCacheDir" note), so #3104's guard covered every cache derived from a
//      --cache value and not the al-out half of the same value.
//
// HOW A REJECTED PATH IS PRODUCED ON LINUX, since this is not obvious
//   Path.GetFullPath rejects almost nothing on Unix. Measured on net8.0/Linux: an embedded
//   NUL throws ArgumentException but cannot arrive through an env var or through execve
//   argv (SetEnvironmentVariable truncates at it); a 300,001-character path succeeds; there
//   is no length check. The one reachable trigger is a RELATIVE value while the process's
//   working directory has been unlinked — getcwd(2) then fails with ENOENT and GetFullPath
//   surfaces it as FileNotFoundException. `cd d && rm -rf d && exec …` is a real process
//   state, so that is what these tests set up. It is also why the reviewer called the crash
//   practically unreachable on Linux and non-blocking: rare, but reachable and deterministic.
//
// Nothing here is a claim about Business Central. `--cache`, AL_RUNNER_NO_CACHE_ROOT and a
// cache root are runner CLI/infrastructure surfaces: AL cannot pass a --cache value and
// cannot observe a cache root, so this is structurally inexpressible in the al-language
// corpus rather than conveniently kept local. See RelativeCacheRootTests' header for the
// same reasoning about #3104 itself.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class CacheRootStartupFailureTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    // ── Finding 3: the guard the al-out root never had ────────────────────────────────

    [Fact]
    public void RequireRooted_WithARelativeDirectory_ThrowsNamingTheValueTheCacheAndBothConsumers()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CacheRoots.RequireRooted("some-relative-cache", "al-out"));

        // The value and the cache, so the reader knows WHICH root is wrong…
        Assert.Contains("some-relative-cache", ex.Message, StringComparison.Ordinal);
        Assert.Contains("al-out", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--cache", ex.Message, StringComparison.Ordinal);
        // …and both consequences, not just the one #3084 was reported through. r2r-chunks
        // feeds LoadFromAssemblyPath; ncl-shadow is handed to a CHILD process as a
        // `dotnet exec` argument (ProgramSupport/Provisioning.cs:106), which breaks for a
        // reason that has nothing to do with assembly loading.
        Assert.Contains("LoadFromAssemblyPath", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ncl-shadow", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet exec", ex.Message, StringComparison.Ordinal);
        Assert.Contains("#3084", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireRooted_WithAnAbsoluteDirectory_ReturnsItUnchanged()
    {
        // The negative half: a guard that threw unconditionally would satisfy the test
        // above. It must also hand the value straight back, because Resolve composes on
        // the return value.
        //
        // Allowlisted in ScratchDirOwnershipGuardTests rather than routed through
        // TestScratch, deliberately: RequireRooted is `Path.IsPathRooted(dir) ? dir :
        // throw` and touches no filesystem, so this path is never created and there is
        // nothing to own. Reserving it would CREATE the parent and drop a .owner sidecar
        // for a directory that will never exist — litter, to satisfy a guard about leaks.
        // The temp root is here only because it is conveniently absolute, which is the
        // same reason AlRunnerPathsTests is on that allowlist.
        var abs = Path.Combine(Path.GetTempPath(), "al-runner-required-rooted");
        Assert.Equal(abs, CacheRoots.RequireRooted(abs, "al-out"));
    }

    [Fact]
    public void BuildUnusableCacheRootMessage_NamesTheSourceTheValueAndTheUnderlyingFailure()
    {
        // One wording for all three startup writers of a cache root, so the exit-2 text a
        // user sees does not depend on which of them failed.
        Assert.Equal(
            "--cache 'relcache' is not a usable directory path: Unable to find the specified file.",
            CacheRoots.BuildUnusableCacheRootMessage("--cache", "relcache", "Unable to find the specified file."));
        Assert.Equal(
            "AL_RUNNER_NO_CACHE_ROOT 'relcache' is not a usable directory path: boom",
            CacheRoots.BuildUnusableCacheRootMessage(CacheRoots.NoCacheRootEnvVar, "relcache", "boom"));
    }

    // ── Finding 2a: --cache given a value Path.GetFullPath rejects ────────────────────

    [SkippableFact]
    public void Cache_WithAValueGetFullPathRejects_ExitsTwoNamingTheFlag()
    {
        SkipUnlessLinux();

        // No BC artifacts needed: the --cache parse branch returns before bundle
        // validation, so this is the whole run.
        var (exit, output) = RunFromUnlinkedWorkingDirectory(
            new[] { "--cache", "relcache" }, env: null);

        Assert.Equal(2, exit);
        Assert.Contains(
            "--cache 'relcache' is not a usable directory path:", output, StringComparison.Ordinal);
        AssertNotAnUnhandledCrash(exit, output);
    }

    // ── Finding 1: AL_RUNNER_NO_CACHE_ROOT must not abort the process ─────────────────

    [SkippableFact]
    public void NoCache_WithAnAdoptedEnvironmentRootGetFullPathRejects_ExitsTwoInsteadOfCrashing()
    {
        SkipUnlessLinux();
        TestArtifacts.SkipIfMissing();

        // --expectations is passed explicitly, at an empty directory, only to get PAST
        // Program.cs's expectations auto-probe: that probe reads Environment.CurrentDirectory,
        // whose getter throws for the same unlinked-cwd reason, and would abort earlier than
        // the line under test (measured: Program.cs:843, exit 134 — filed as its own defect,
        // issue #3120, since deciding what the runner should DO with an unreadable working
        // directory is a different question from this one). Scaffolding for reaching the
        // cache-root block, not part of the claim.
        var emptyExpectations = TestScratch.Dir("al-runner-no-cache-root-expectations");
        Directory.CreateDirectory(emptyExpectations);

        var (exit, output) = RunFromUnlinkedWorkingDirectory(
            new[]
            {
                Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec"),
                "--no-cache",
                "--expectations", emptyExpectations,
            },
            env: new Dictionary<string, string?> { [CacheRoots.NoCacheRootEnvVar] = "relcache" });

        Assert.Equal(2, exit);
        Assert.Contains(
            "AL_RUNNER_NO_CACHE_ROOT 'relcache' is not a usable directory path:",
            output, StringComparison.Ordinal);
        // The part that regressed: exit 134 with a stack trace, not a documented exit.
        AssertNotAnUnhandledCrash(exit, output);
    }

    /// <summary>
    /// The sibling of the test above, and the review follow-up to this PR's own first cut.
    ///
    /// <para>DisableForRun has TWO failure sites, not one. It ADOPTS
    /// AL_RUNNER_NO_CACHE_ROOT when that variable is set (the test above), and otherwise
    /// MINTS a root through ScratchDirs.Reserve — publishing it to the variable only after
    /// the reserve succeeds. So a throw from the mint branch leaves the variable unset, and
    /// the catch in Program.cs, which reads the variable back to name the offending value,
    /// got null. Reusing the one wording for both sites then printed:</para>
    ///
    /// <code>AL_RUNNER_NO_CACHE_ROOT '' is not a usable directory path: Unable to find the specified file.</code>
    ///
    /// <para>naming a variable the user never set and quoting an empty value that was
    /// nobody's input — an error whose stated cause did not happen, which sends the reader
    /// to unset something already unset. Both directions are asserted: the honest message
    /// is present, AND the misattributed one is absent. Without the second assertion this
    /// would still pass if the code printed both.</para>
    ///
    /// <para>REACHING the mint branch's throw needs Path.GetFullPath to reject the minted
    /// path. Reserve swallows IOException and UnauthorizedAccessException, so an unwritable
    /// temp root is not enough — the GetFullPath on its first line is outside that catch and
    /// is the only part that can throw. A RELATIVE TMPDIR makes the minted path relative
    /// (measured on net8.0/Linux: TMPDIR=reltmp gives Path.GetTempPath() == "reltmp/"), and
    /// the unlinked working directory this file's helper sets up makes getcwd(2) fail, so
    /// GetFullPath of that relative path throws. Both halves are real process states.</para>
    /// </summary>
    [SkippableFact]
    public void NoCache_WhenMintingItsOwnRootFails_BlamesTheMintAndNotAnUnsetEnvironmentVariable()
    {
        SkipUnlessLinux();
        TestArtifacts.SkipIfMissing();

        // Same scaffolding, and the same reason, as the test above (issue #3120).
        var emptyExpectations = TestScratch.Dir("al-runner-mint-failure-expectations");
        Directory.CreateDirectory(emptyExpectations);

        var (exit, output) = RunFromUnlinkedWorkingDirectory(
            new[]
            {
                Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec"),
                "--no-cache",
                "--expectations", emptyExpectations,
            },
            env: new Dictionary<string, string?>
            {
                // Unset, so DisableForRun takes the MINT branch rather than the adopt one.
                [CacheRoots.NoCacheRootEnvVar] = null,
                // Relative, so the path it mints is relative and GetFullPath rejects it
                // against the unlinked cwd.
                ["TMPDIR"] = "reltmp",
            });

        Assert.Equal(2, exit);

        // Positive: the message names what actually failed — the runner's own mint, under
        // the directory it tried to mint in — and offers the remedy that applies.
        Assert.Contains(
            "al-runner could not reserve a throwaway --no-cache root under 'reltmp/':",
            output, StringComparison.Ordinal);
        Assert.Contains(
            "Set AL_RUNNER_NO_CACHE_ROOT to a writable absolute directory",
            output, StringComparison.Ordinal);

        // Negative, and the whole point: it must NOT blame the variable that is unset.
        Assert.DoesNotContain(
            "AL_RUNNER_NO_CACHE_ROOT '' is not a usable directory path",
            output, StringComparison.Ordinal);

        AssertNotAnUnhandledCrash(exit, output);
    }

    // ── Finding 2b: the alCacheDir rooting path ──────────────────────────────────────

    /// <summary>
    /// A RELATIVE `--cache` on the command line must produce a working run, and the
    /// AL-output cache must carry the ABSOLUTE equivalent of that directory.
    ///
    /// Two assertions, because they fail for different reasons:
    ///
    ///   * The `[cache] WROTE … path=` line (behind --verbose since #2239) names an
    ///     absolute path under the relative directory's absolute equivalent. This is the
    ///     decisive half, and it isolates alCacheDir specifically — the one root that never
    ///     flows through CacheRoots.Resolve. Measured with ONLY the parse-time rooting
    ///     reverted (so #3104's CacheRoots.SetOverride rooting is still in place, and every
    ///     derived cache is still absolute):
    ///         [cache] WROTE key=7c5f1cd7… path=rel-out/7c5f1cd7….dll
    ///     — relative, silently, with the run otherwise green. That is the value the process
    ///     CARRIES, not merely where the bytes happened to land: for an unchanged working
    ///     directory the rooted and unrooted forms name the same physical directory, so a
    ///     file-exists check alone could not tell the fix from its absence.
    ///   * The run PASSES, `pass: 1`, exit 0. A regression assertion rather than a
    ///     discriminator — it also held in the reverted state above, because with the
    ///     derived caches rooted the AL-output path alone was survivable in that
    ///     configuration. It is here so a future change that makes a relative --cache
    ///     unusable end to end (the #3084 symptom: exit 1, `pass: 0 / fail: 1`) fails a test
    ///     instead of being reported as ordinary AL failures.
    ///
    /// Revert the parse-time rooting with the al-out guard #3111 added still in place and
    /// this instead exits 2 naming the al-out root — loud either way, never silent.
    /// </summary>
    [SkippableFact]
    public void RelativeCache_RunsCleanlyAndCarriesTheAbsoluteAlOutputRoot()
    {
        TestArtifacts.SkipIfMissing();

        var workDir = TestScratch.Dir("al-runner-relative-cache-cli");
        Directory.CreateDirectory(workDir);
        var emptyExpectations = Path.Combine(workDir, "expectations");
        Directory.CreateDirectory(emptyExpectations);

        // Relative, and never before seen — so the AL-output cache is a guaranteed MISS and
        // the run must WRITE, which is the line this test reads.
        const string RelativeCache = "rel-out";
        var expectedAbsolute = Path.Combine(workDir, RelativeCache);

        var (exit, output) = RunRunner(
            workDir,
            new[]
            {
                Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec"),
                "--cache", RelativeCache,
                "--expectations", emptyExpectations,
                "--verbose",
            });

        Assert.True(exit == 0 && output.Contains("pass:        1", StringComparison.Ordinal),
            $"a relative --cache must produce the same passing run an absolute one does:\n{output}");

        var wrote = Regex.Match(output, @"\[cache\] WROTE key=\S+ path=(?<path>\S+)");
        Assert.True(wrote.Success, $"expected a '[cache] WROTE … path=' line:\n{output}");

        var writtenPath = wrote.Groups["path"].Value;
        Assert.True(Path.IsPathRooted(writtenPath),
            $"the AL-output cache path must be absolute, was '{writtenPath}':\n{output}");
        Assert.StartsWith(expectedAbsolute + Path.DirectorySeparatorChar, writtenPath, StringComparison.Ordinal);
        Assert.True(File.Exists(writtenPath), $"cached assembly must exist at '{writtenPath}'");

        // And the guard on this root — the one alCacheDir never had — stayed quiet, which
        // is the same fact stated from the other side.
        Assert.DoesNotContain("is not a usable directory path", output, StringComparison.Ordinal);
        Assert.DoesNotContain("RELATIVE root", output, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private static void SkipUnlessLinux()
        => Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
            "unlinking a live working directory is a Unix behaviour; Windows refuses to remove it");

    private static void AssertNotAnUnhandledCrash(int exit, string output)
    {
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.Ordinal);
        // 128 + SIGABRT(6): what CoreCLR's default unhandled-exception handler produces.
        Assert.NotEqual(134, exit);
    }

    /// <summary>
    /// Runs the built runner with its working directory UNLINKED — `cd d && rm -rf d &&
    /// exec dotnet …`, which leaves the process with a valid but nameless cwd, so getcwd(2)
    /// fails and every Path.GetFullPath of a relative path throws. The only Linux state
    /// that makes Path.GetFullPath reject a value that can actually reach the runner.
    /// </summary>
    private static (int Exit, string Output) RunFromUnlinkedWorkingDirectory(
        string[] runnerArgs, IDictionary<string, string?>? env)
    {
        var doomed = Path.Combine(TestScratch.Dir("al-runner-unlinked-cwd"), "doomed");
        Directory.CreateDirectory(doomed);

        var script = new StringBuilder();
        script.Append("rm -rf ").Append(ShellQuote(doomed)).Append(" && exec dotnet ");
        script.Append(ShellQuote(RunnerDll()));
        foreach (var a in RunnerArgsWithBcVersion(runnerArgs)) script.Append(' ').Append(ShellQuote(a));

        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = doomed,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script.ToString());
        // A null VALUE removes the variable, so a test can assert on the branch taken when
        // something is UNSET without depending on the ambient environment not having it.
        if (env != null)
            foreach (var kv in env)
            {
                if (kv.Value == null) psi.Environment.Remove(kv.Key);
                else psi.Environment[kv.Key] = kv.Value;
            }
        return Capture(psi);
    }

    private static (int Exit, string Output) RunRunner(string workingDirectory, string[] runnerArgs)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        psi.ArgumentList.Add(RunnerDll());
        foreach (var a in RunnerArgsWithBcVersion(runnerArgs)) psi.ArgumentList.Add(a);
        return Capture(psi);
    }

    private static string[] RunnerArgsWithBcVersion(string[] runnerArgs)
    {
        // TestBuildConfig.BcVersionArg is " --bc-version <v>" (or empty) — split so it can
        // be passed as ArgumentList entries rather than a command line.
        var extra = TestBuildConfig.BcVersionArg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return runnerArgs.Concat(extra).ToArray();
    }

    /// <summary>The built al-runner.dll, unquoted — TestBuildConfig.RunArgs returns it quoted
    /// for command-line use, and both call sites here quote for themselves.</summary>
    private static string RunnerDll() => TestBuildConfig.RunArgs(ProjectPath).Trim('"');

    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static (int Exit, string Output) Capture(ProcessStartInfo psi)
    {
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (p.ExitCode, sb.ToString());
    }
}
