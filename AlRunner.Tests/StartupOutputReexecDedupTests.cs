// StartupOutputReexecDedupTests — issue #2041.
//
// Split out of #2037/#2038: this process's startup reporting (the `[provision] BC ...
// already complete` line, `[bc] selected BC ...`, and the `al-runner — running N
// bundle(s)` banner) is printed BEFORE the shadow-re-exec decision hands off to a child
// process. The tool package no longer ships Microsoft.Dynamics.Nav.Ncl.dll (#2023/#2026),
// so NclShadowRuntime.NeedsShadow is true on essentially every real invocation, and the
// child re-runs the exact same startup sequence from scratch — reprinting all three
// lines. Confirmed live against the published 2.5.0 package with `strace -f -e
// trace=execve`: exactly two execve calls on a warm run, and all three lines appear
// twice, once per process generation.
//
// The fix (see Program.cs) computes whether THIS generation will need to shadow-re-exec
// — a cheap, deterministic filesystem check (does Ncl.dll already exist beside this
// assembly?) that is known before any of the three lines would print — and suppresses
// them in that generation only. The generation that actually goes on to run tests (no
// re-exec pending) always prints them, exactly once. The `[reexec]` explanation itself
// (#2034/#2038) is untouched: it still prints from the parent, at default verbosity.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class StartupOutputReexecDedupTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string Fixture =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec");

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    /// <summary>
    /// Acceptance #1 + #2: a WARM run (ncl-cecil cache already populated — the exact
    /// shape of the issue's own repro, "two execve calls") against an explicit
    /// --bc-version prints the provisioning line, the `[bc] selected BC` line and the
    /// banner exactly once each, and the `[reexec]` explanation still fires from the
    /// parent — all at DEFAULT verbosity (no AL_RUNNER_VERBOSE), since #2038 already
    /// made `[provision]`/`[bc]`/`[reexec]` survive the default-verbosity filter and
    /// this is what a real user actually sees.
    /// </summary>
    [SkippableFact]
    public void WarmRun_PrintsStartupTrioExactlyOnce_ReexecExplanationStillFromParent()
    {
        TestArtifacts.SkipIfMissing();

        // Warm-up spawn: primes the ncl-cecil / ncl-shadow caches so the run asserted
        // on below is genuinely warm (a cold cache adds a THIRD generation via the
        // fresh-Cecil-rewrite re-exec, which this issue's acceptance criteria does not
        // cover — see Program.cs's comment on the "fresh rewrite" re-exec).
        Spawn();

        var (output, exit) = Spawn();
        Assert.Equal(0, exit);
        Assert.Contains("pass:        1", output);
        Assert.Contains("fail:        0", output);

        Assert.Equal(1, CountOccurrences(output, "[provision] BC "));
        Assert.Equal(1, CountOccurrences(output, "[bc] selected BC "));
        Assert.Equal(1, CountOccurrences(output, "al-runner — running "));

        // The re-exec explanation must not have been collapsed away along with the
        // duplicated trio above — it is specifically about the parent and stays there.
        Assert.Equal(1, CountOccurrences(output, "[reexec] Ncl.dll not shipped in this install"));
    }

    /// <summary>
    /// Acceptance #3: a run that does NOT need to re-exec at all — spawned directly out
    /// of the shadow runtime dir, which genuinely has Ncl.dll on disk, so
    /// NclShadowRuntime.NeedsShadow is false for it — is unaffected by the fix: the trio
    /// still prints, exactly once, same as it always did, and no `[reexec]` marker
    /// appears since none fired.
    /// </summary>
    [SkippableFact]
    public void NoReexecRun_StartupTrioUnchanged()
    {
        TestArtifacts.SkipIfMissing();

        // Discover the shadow dir path from a REAL subprocess's own [Cecil] "Building/
        // Reusing Ncl shadow runtime dir at ..." line (verbose only for this discovery
        // spawn) rather than calling NclShadowRuntime.EnsureShadowDir in-process: this
        // test host process has almost certainly already loaded
        // Microsoft.Dynamics.Nav.Ncl.dll for some OTHER test in the same run, and
        // NclCecilRewrite.RewriteInPlace silently no-ops ("Ncl already loaded before
        // in-place rewrite — no effect") whenever that is true for the CURRENT
        // AppDomain — it would build a shadow dir with every dependency mirrored except
        // the one file this test is actually about. A fresh child process never has
        // that problem, and it is exactly the path Program.cs itself takes.
        var (warmupOutput, warmupExit) = SpawnVerbose();
        Assert.Equal(0, warmupExit);
        var shadowDir = ExtractShadowDir(warmupOutput);
        var shadowDll = Path.Combine(shadowDir, "al-runner.dll");
        Assert.True(File.Exists(shadowDll), $"shadow al-runner.dll not found at {shadowDll}");
        Assert.True(
            File.Exists(Path.Combine(shadowDir, "Microsoft.Dynamics.Nav.Ncl.dll")),
            "shadow dir is missing its own Ncl.dll copy — NeedsShadow would be true there too");

        var (output, exit) = SpawnAssembly(shadowDll);
        Assert.Equal(0, exit);
        Assert.Contains("pass:        1", output);
        Assert.Contains("fail:        0", output);

        Assert.Equal(1, CountOccurrences(output, "[provision] BC "));
        Assert.Equal(1, CountOccurrences(output, "[bc] selected BC "));
        Assert.Equal(1, CountOccurrences(output, "al-runner — running "));
        Assert.DoesNotContain("[reexec]", output);
    }

    /// <summary>
    /// Regression: `reexecPending` must track the ACTUAL re-exec gate
    /// (`NeedsShadow(...) && AL_RUNNER_NCL_SHADOW_DONE != "1"`), not `NeedsShadow` alone.
    ///
    /// Setup: Ncl.dll is genuinely absent from the original (non-shadow) bin/ — so
    /// NeedsShadow is true — but AL_RUNNER_NCL_SHADOW_DONE=1 is forced by hand (a
    /// plausible way to skip the shadow hop while debugging), and the ncl-cecil cache is
    /// already warm for this exact build (primed by the earlier warm-up spawns in this
    /// class). Under that combination: the shadow-re-exec block is skipped (env guard),
    /// NclCecilRewrite.RewriteInPlace hits the warm cache and just copies the cached
    /// bytes into the original bin/'s Ncl.dll (no further re-exec — it only re-execs on
    /// a genuine cache MISS), so this single process runs the whole bundle itself. ZERO
    /// re-execs happen at all.
    ///
    /// If `reexecPending` were computed from `NeedsShadow` alone (ignoring the env
    /// guard), it would read true here even though no re-exec follows — suppressing the
    /// provisioning line, `[bc] selected BC`, and the banner in the ONLY generation that
    /// ever runs, with no later generation to reprint them. Confirmed by reverting the
    /// env-guard clause locally: the trio does not appear ANYWHERE in the output in that
    /// configuration, even though the run itself passes cleanly (same silent-output
    /// class of bug #2034 was about, one file over).
    /// </summary>
    [SkippableFact]
    public void ShadowDoneEnvVarForced_NoFurtherReexecFollows_StartupTrioStillPrintsOnce()
    {
        TestArtifacts.SkipIfMissing();

        var originalDll = Path.Combine(
            ProjectPath, "bin", TestBuildConfig.Configuration, TestBuildConfig.Framework, "al-runner.dll");
        var originalNcl = Path.Combine(Path.GetDirectoryName(originalDll)!, "Microsoft.Dynamics.Nav.Ncl.dll");

        // Warm the ncl-cecil cache for this exact build (normal spawn, via the shadow
        // dir — never touches the original bin/) so the forced run below hits a cache
        // HIT rather than a genuine MISS (a MISS would trigger the UNRELATED
        // fresh-rewrite re-exec — see Program.cs — which would mask exactly the
        // single-generation scenario this test is pinning).
        Spawn();
        Assert.False(File.Exists(originalNcl),
            $"precondition violated: {originalNcl} already exists — NeedsShadow would be false " +
            "for the original bin/ regardless of the env guard, and this test would not be " +
            "exercising the scenario it claims to.");

        try
        {
            var psi = BuildPsi(originalDll);
            psi.Environment["AL_RUNNER_NCL_SHADOW_DONE"] = "1";
            var (output, exit) = Run(psi);

            Assert.Equal(0, exit);
            Assert.Contains("pass:        1", output);
            Assert.Contains("fail:        0", output);

            // Zero re-execs of ANY kind fired — this really is the single-generation
            // case, not the fresh-rewrite one masking it.
            Assert.DoesNotContain("[reexec]", output);

            Assert.Equal(1, CountOccurrences(output, "[provision] BC "));
            Assert.Equal(1, CountOccurrences(output, "[bc] selected BC "));
            Assert.Equal(1, CountOccurrences(output, "al-runner — running "));
        }
        finally
        {
            // RewriteInPlace writes Ncl.dll directly into the original bin/ as a side
            // effect of this scenario (see the doc comment above) — clean it up so it
            // cannot contaminate any other test in this assembly that inspects
            // NclShadowRuntime.NeedsShadow against that same directory.
            try { File.Delete(originalNcl); } catch { /* best effort */ }
        }
    }

    /// <summary>Extracts the path from Program's own `[Cecil] Building/Reusing Ncl
    /// shadow runtime dir at &lt;path&gt;` line.</summary>
    private static string ExtractShadowDir(string output)
    {
        var m = Regex.Match(output, @"\[Cecil\] (?:Building|Reusing) Ncl shadow runtime dir at (.+)$",
            RegexOptions.Multiline);
        Assert.True(m.Success, $"could not find the shadow-dir marker line in runner output:\n{output}");
        return m.Groups[1].Value.TrimEnd('\r');
    }

    private (string Output, int Exit) Spawn() =>
        SpawnAssembly(Path.Combine(
            ProjectPath, "bin", TestBuildConfig.Configuration, TestBuildConfig.Framework, "al-runner.dll"));

    /// <summary>Same spawn as <see cref="Spawn"/>, but AL_RUNNER_VERBOSE=1 so the
    /// `[Cecil]`-tagged shadow-dir marker line (suppressed by default — see Log.cs) is
    /// observable, purely for path discovery. Not used for any of the count assertions,
    /// which must stay at default verbosity to prove what a real user actually sees.
    /// </summary>
    private (string Output, int Exit) SpawnVerbose()
    {
        var psi = BuildPsi(Path.Combine(
            ProjectPath, "bin", TestBuildConfig.Configuration, TestBuildConfig.Framework, "al-runner.dll"));
        psi.Environment["AL_RUNNER_VERBOSE"] = "1";
        return Run(psi);
    }

    private (string Output, int Exit) SpawnAssembly(string dllPath) => Run(BuildPsi(dllPath));

    private ProcessStartInfo BuildPsi(string dllPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\"{TestBuildConfig.BcVersionArg} \"{Fixture}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        // Deliberately default verbosity — no AL_RUNNER_VERBOSE — this test is about
        // what a real user sees by default, and #2038 already made every line asserted
        // on here (`[provision]`, `[bc]`, `[reexec]`) survive that filter.
        psi.Environment.Remove("AL_RUNNER_VERBOSE");
        psi.Environment.Remove("AL_RUNNER_NCL_SHADOW_DONE");
        psi.Environment.Remove("AL_RUNNER_REEXECED");
        return psi;
    }

    private static (string Output, int Exit) Run(ProcessStartInfo psi)
    {
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Assert.True(p.WaitForExit(300_000), "runner did not exit within 300s");
        p.WaitForExit();
        return (sb.ToString(), p.ExitCode);
    }
}
