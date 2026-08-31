// CleanRunStartupVerbosityTests — issue #2239.
//
// A clean run used to print roughly fifteen lines of startup bookkeeping (which
// artifact was auto-selected and why, engine-variant selection, the re-exec notice,
// "engine artifacts already complete", package-cache directory counts, the
// `[type-index]` metadata-fallback note, patch-apply timing, per-bundle dep counts,
// and AL-output cache HIT/MISS) before the first test result — several of them twice,
// because a Cecil-fresh-rewrite or shadow-runtime re-exec re-runs the same startup
// sequence in a child process. None of that is a test result, and #2210/#2221 already
// established the failure mode this fix has to avoid: hiding a line by editing Log's
// `[Component]` allowlist either does nothing (most of these tags are not suppressed
// by that filter at all — several, like `[cache]`, `[bc]`, `[provision]`, `[reexec]`,
// are either unmatched by its regex or explicitly exempted from it) or, worse, makes
// the line invisible even under --verbose.
//
// So every line this file asserts on was moved behind an explicit `if (Log.Verbose)`
// at its own call site in Program.cs / AssemblyTypeIndex.cs — never by touching Log's
// regex or allowlist. This file proves that from the outside: a default run must not
// print any of them, and a --verbose run must print every one that this dev/CI build
// can actually reach (see below for the two that can't, and why).
//
// What deliberately still prints at default verbosity (see Program.cs's own comments,
// and issue #2077/#2210's reasoning): `[bc] selected BC <version> (<path>)` — which BC
// version actually ran is a RESULT, not a diagnostic, per #2077's measured 42-test
// swing decided silently — and `al-runner — running N bundle(s)`, the run header. Both
// are pinned here too, as the negative control: this fix must not have swept those
// away along with the bookkeeping.
//
// Two of the moved lines are NOT exercised by a live spawn here, and covered by a
// structural source check instead (ProgramCs_EngineVariantAndReexecLines_GatedOnVerbose
// below): `[bc] selecting engine variant ...` and its matching `[reexec] Re-execing
// into a shadow runtime dir with the matching BC-minor engine variant` only fire when
// this install ships per-BC-minor engine variants (a packaged release's variants/ dir —
// see EngineVariants.Discover), which a plain `dotnet build` dev/CI checkout never has.
// Forcing that condition would mean fabricating a fake variants/ directory and a second
// engine build, disproportionate to what this file needs to prove. Confirmed empirically
// (2026-08-31, this dev build): a --verbose spawn against RecordTriggerXRec prints every
// OTHER fragment below exactly once, these two zero times, matching EngineVariants.
// Discover(...).Count == 0 for this build — not evidence of a gating bug, evidence the
// branch is unreached here.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class CleanRunStartupVerbosityTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string Fixture =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec");

    /// <summary>
    /// Every line issue #2239 named as startup bookkeeping that must move behind
    /// --verbose AND that this dev/CI build can actually reach in one cold spawn
    /// against a fresh --cache dir (see the file header for the two variant-swap
    /// lines this deliberately excludes, and
    /// <see cref="DefaultRun_PrintsNoCacheHitLineOnAWarmRun"/> /
    /// <see cref="VerboseRun_PrintsCacheHitLineOnAWarmRun"/> for `[cache] HIT`, which
    /// needs a second, warm run). Matched as a plain substring against combined
    /// stdout+stderr — each entry is the fixed, non-varying prefix of a real line (the
    /// full line also carries a version number, byte count, hash, or path that varies
    /// run to run).
    /// </summary>
    private static readonly string[] ColdRunBookkeepingLineFragments =
    {
        "[bc] no --bc-version given — selecting BC ",
        "[reexec] Ncl.dll not shipped in this install",
        "[provision] BC ",
        "  package caches (requested): ",
        "  package caches (final search set): ",
        "[type-index] no raw metadata for ",
        "BC runtime patches applied (",
        " dep(s)",
        " dep assembl(ies)",
        "[cache] MISS",
        "[cache] WROTE",
    };

    private static (string Output, int Exit) Run(string alCacheDir, params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        // Deliberately NOT TestBuildConfig.BcVersionArg — the auto-selection lines this
        // class asserts on ("[bc] no --bc-version given — selecting BC ...") only print
        // when neither --bc-version nor --artifact-path is given.
        foreach (var a in extraArgs) args.Append(' ').Append(a);
        args.Append($" --cache \"{alCacheDir}\"");
        args.Append($" \"{Fixture}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        // Deliberately isolated from whatever the ambient shell/session might have set —
        // this class is specifically about the CLI --verbose flag's effect, so nothing
        // may leak in from outside either test.
        psi.Environment.Remove("AL_RUNNER_VERBOSE");

        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static string NewCacheDir([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        Path.Combine(Path.GetTempPath(), "al-runner-clean-run-verbosity", name, Guid.NewGuid().ToString("N"));

    /// <summary>
    /// RED (pre-fix): every fragment in <see cref="ColdRunBookkeepingLineFragments"/>
    /// appeared unconditionally, so this test failed against the pre-fix binary. GREEN:
    /// a default (non-verbose) run prints none of them.
    /// </summary>
    [SkippableFact]
    public void DefaultRun_PrintsNoStartupBookkeepingLines()
    {
        TestArtifacts.SkipIfMissing();
        var alCacheDir = NewCacheDir();
        try
        {
            var (output, exit) = Run(alCacheDir);
            Assert.True(exit == 0 && output.Contains("pass:        1"),
                $"fixture must compile and pass cleanly:\n{output}");

            foreach (var fragment in ColdRunBookkeepingLineFragments)
                Assert.DoesNotContain(fragment, output);
        }
        finally
        {
            try { Directory.Delete(alCacheDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The other direction: --verbose must still surface every one of these lines on a
    /// cold run against a fresh --cache dir — the fix must gate them, not delete them.
    /// </summary>
    [SkippableFact]
    public void VerboseRun_PrintsColdRunBookkeepingLines()
    {
        TestArtifacts.SkipIfMissing();
        var alCacheDir = NewCacheDir();
        try
        {
            var (output, exit) = Run(alCacheDir, "--verbose");
            Assert.True(exit == 0 && output.Contains("pass:        1"),
                $"fixture must compile and pass cleanly:\n{output}");

            foreach (var fragment in ColdRunBookkeepingLineFragments)
                Assert.Contains(fragment, output);
        }
        finally
        {
            try { Directory.Delete(alCacheDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// `[cache] HIT` only fires on a second, warm run against the same --cache dir — a
    /// single cold spawn always takes the MISS branch instead (covered above). Default
    /// verbosity: absent on both the cold (MISS) and warm (HIT) run.
    /// </summary>
    [SkippableFact]
    public void DefaultRun_PrintsNoCacheHitLineOnAWarmRun()
    {
        TestArtifacts.SkipIfMissing();
        var alCacheDir = NewCacheDir();
        try
        {
            var (coldOutput, coldExit) = Run(alCacheDir);
            Assert.True(coldExit == 0 && coldOutput.Contains("pass:        1"),
                $"cold run must compile and pass cleanly:\n{coldOutput}");

            var (warmOutput, warmExit) = Run(alCacheDir);
            Assert.True(warmExit == 0 && warmOutput.Contains("pass:        1"),
                $"warm run must compile and pass cleanly:\n{warmOutput}");
            Assert.DoesNotContain("[cache] HIT", warmOutput);
            Assert.DoesNotContain("[cache] MISS", warmOutput);
        }
        finally
        {
            try { Directory.Delete(alCacheDir, recursive: true); } catch { }
        }
    }

    /// <summary>--verbose counterpart: the warm run's HIT line must still be reachable.</summary>
    [SkippableFact]
    public void VerboseRun_PrintsCacheHitLineOnAWarmRun()
    {
        TestArtifacts.SkipIfMissing();
        var alCacheDir = NewCacheDir();
        try
        {
            var (coldOutput, coldExit) = Run(alCacheDir, "--verbose");
            Assert.True(coldExit == 0 && coldOutput.Contains("pass:        1"),
                $"cold run must compile and pass cleanly:\n{coldOutput}");
            Assert.Contains("[cache] MISS", coldOutput);

            var (warmOutput, warmExit) = Run(alCacheDir, "--verbose");
            Assert.True(warmExit == 0 && warmOutput.Contains("pass:        1"),
                $"warm run must compile and pass cleanly:\n{warmOutput}");
            Assert.Contains("[cache] HIT", warmOutput);
        }
        finally
        {
            try { Directory.Delete(alCacheDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Negative control: the two lines issue #2239 deliberately kept visible at default
    /// verbosity — see #2077/#2210's reasoning on why "which BC version ran" is a result,
    /// not a diagnostic — must not have been swept away along with the bookkeeping.
    /// </summary>
    [SkippableFact]
    public void DefaultRun_StillPrintsSelectedVersionAndRunningBanner()
    {
        TestArtifacts.SkipIfMissing();
        var alCacheDir = NewCacheDir();
        try
        {
            var (output, exit) = Run(alCacheDir);
            Assert.True(exit == 0 && output.Contains("pass:        1"),
                $"fixture must compile and pass cleanly:\n{output}");

            Assert.Contains("[bc] selected BC ", output);
            Assert.Contains("al-runner — running ", output);
        }
        finally
        {
            try { Directory.Delete(alCacheDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Structural half for the two lines a live spawn in this build can never reach
    /// (see the file header): both the engine-variant-selection reasoning line and its
    /// matching re-exec explanation in Program.cs must sit behind an
    /// `if (AlRunner.Log.Verbose)` guard within a short lookback window, the same
    /// technique ExplicitEngineMinorWarningGatingTests and
    /// ReexecExplanationVisibilityTests already use in this suite for the identical
    /// "prove the call site, not just that a correct condition exists unused" reason.
    /// </summary>
    [Fact]
    public void ProgramCs_EngineVariantAndReexecLines_GatedOnVerbose()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Program.cs"));

        AssertGuardedByLogVerbose(src, "[bc] selecting engine variant {variant.BuildVersion}");
        AssertGuardedByLogVerbose(src, "? \"[reexec] Re-execing into a shadow runtime dir");
    }

    private static void AssertGuardedByLogVerbose(string src, string marker)
    {
        var idx = src.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"marker not found in Program.cs: {marker}");
        var windowStart = Math.Max(0, idx - 300);
        var window = src[windowStart..idx];
        Assert.Contains("AlRunner.Log.Verbose", window);
    }
}
