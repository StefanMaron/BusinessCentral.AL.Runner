// EventSubscriptionMultiBundleScopeTests — issue #4222.
//
// The end-to-end proof that Event Subscription (2000000140) is scoped to the bundle being run.
// BundleEpochScopeTests pins the MARKER the fix rests on; this file pins the AL-observable
// CLAIM, by driving the runner over two bundles in one invocation and reading its output.
//
// WHY THIS FILE HAS TO EXIST, AND WHAT ITS ABSENCE COST
//   The fixture next door (Fixtures/EventSubscriptionMultiBundle) is only reachable through an
//   explicitly-named test: there is no generic fixture discovery in this repository. For one
//   review cycle this driver did not exist, so the fixture's six arms — including BOTH AllObj
//   control arms — ran only when someone invoked the runner by hand. The PR body's
//   `Failed: 1, Passed: 5` -> `Failed: 0, Passed: 6` figures were honest and reproducible, and
//   CI performed neither run. A committed fixture with no driver reads exactly like coverage.
//
// WHY TWO BUNDLES, AND WHY THEY CANNOT BE SPLIT
//   Run alone, each bundle passes every arm even on the unfixed runner — measured. The defect
//   needs a second bundle in the same process, because EventSubscriberPatches' scan registries
//   are process-wide and a one-shot multi-bundle run reaches neither the watch-mode reset
//   (Program.cs gates it on `if (watchMode)`) nor --server's per-request one. So the two
//   directories are passed to ONE invocation, in order, and the accumulation shows up in
//   whichever bundle runs second.
//
// WHAT MAKES A DISABLED FIX RED HERE
//   `ForeignSubscriptionIsAbsentB` filters on bundle A's subscriber codeunit id, a number
//   bundle B never declares. Against the unfixed runner it reads one row and the runner exits
//   non-zero. Measured both ways, and with two independent mutations of the fix.
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class EventSubscriptionMultiBundleScopeTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "EventSubscriptionMultiBundle");

    /// <summary>
    /// One runner invocation over <paramref name="bundles"/>, in the order given.
    ///
    /// <para>The order is load-bearing: the accumulation this issue reports appears in the
    /// SECOND bundle, so a helper that sorted or deduplicated its arguments would silently
    /// destroy the property under test.</para>
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) Run(string cacheDir, params string[] bundles)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        foreach (var b in bundles)
            sb.Append(' ').Append($"\"{Path.Combine(FixtureDir, b)}\"");
        sb.Append(' ').Append($"--cache \"{cacheDir}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = sb.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };

        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(300_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 300s.");
        }
        // WaitForExit(int) does not drain the async output callbacks; only the parameterless
        // overload does. Without this the last stdout lines can still be in flight.
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    /// <summary>
    /// The whole two-bundle fixture, in one runner invocation — the proof of the #4222 fix.
    ///
    /// <para>Named arms rather than a bare exit-code check: the runner exits 0 on a bundle
    /// whose tests all pass, and the specific claims below are what say WHICH tests passed.</para>
    /// </summary>
    [Fact]
    public void EventSubscription_IsScopedToTheBundleBeingRun()
    {
        var cacheDir = TestScratch.Dir("al-runner-esv-multibundle");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir, "AppA", "AppB");

            Assert.True(exit == 0,
                $"expected a clean run (every fixture test must pass). exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // Each bundle lists its OWN subscription. Without these the foreign arms below
            // would pass against a table that is simply empty — the pre-#4198 behaviour,
            // which is not a scoping fix.
            Assert.Contains("PASS  Codeunit70784.OwnSubscriptionIsPresentA", stdout);
            Assert.Contains("PASS  Codeunit70804.OwnSubscriptionIsPresentB", stdout);

            // The arm this issue is about. B runs SECOND, so it is the one that read bundle
            // A's row on the unfixed runner: `Expected 0 but got 1`.
            Assert.Contains("PASS  Codeunit70804.ForeignSubscriptionIsAbsentB", stdout);
            // Its partner in the other direction — A runs first and must not see B either,
            // which is what says the fix scopes rather than merely clearing at some point.
            Assert.Contains("PASS  Codeunit70784.ForeignSubscriptionIsAbsentA", stdout);

            // The control arms. AllObj is asked the same cross-bundle question and is scoped
            // correctly both before and after the fix, so these passing is what makes a red
            // foreign arm attributable to THIS table rather than to the process model. They
            // are asserted here, not merely present in the fixture, because a control nobody
            // reads proves nothing.
            Assert.Contains("PASS  Codeunit70784.AllObjDoesNotListTheForeignCodeunitA", stdout);
            Assert.Contains("PASS  Codeunit70804.AllObjDoesNotListTheForeignCodeunitB", stdout);

            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The discriminator: bundle B ALONE passes every arm, on the fixed runner and on the
    /// unfixed one alike.
    ///
    /// <para>This is the control for the test above, in the sense
    /// <c>.claude/rules/tdd.md</c> means: it removes the input that makes the property apply —
    /// a previous bundle — and requires GREEN. Measured against a mutation that disables the
    /// row-dropping half of the fix: the two-bundle test goes <c>Failed: 1, Passed: 5</c> while
    /// this one stays <c>Failed: 0, Passed: 3</c>. A fixture that reddened in both would be
    /// measuring something other than bundle scoping.</para>
    /// </summary>
    [Fact]
    public void ASingleBundleRun_IsUnaffected()
    {
        var cacheDir = TestScratch.Dir("al-runner-esv-singlebundle");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir, "AppB");

            Assert.True(exit == 0,
                $"a single-bundle run must pass every arm — this is every CI leg and every "
                + $"ordinary local invocation. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            Assert.Contains("PASS  Codeunit70804.OwnSubscriptionIsPresentB", stdout);
            Assert.Contains("PASS  Codeunit70804.ForeignSubscriptionIsAbsentB", stdout);
            Assert.Contains("PASS  Codeunit70804.AllObjDoesNotListTheForeignCodeunitB", stdout);
            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
