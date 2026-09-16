// #4085: the `dep-tableext-platform-base` pair is the one dep/main pair in tests/runner-extras
// that must run in BOTH bundle shapes, because each shape is blind to one half of #1686.
//
// Measured on BC 28.1, one mutation per half, fresh cache root per run (PR body has the table):
//
//   cross-bundle half  -- RecordPatches `_sourceDirs` de-dup + the field-id de-dup in
//     MergeExtensionFields, off for table `item`:
//       two ordered bundles -> exit 2, 0 tests, "table 27 metadata could not be built: NRE"
//       one combined bundle -> exit 0, 474/474, all four DTB tests PASS   <-- blind
//
//   in-bundle half  -- EmitSiblingSymbols dropping the bundle-wide platform closure from the
//     sibling's deps sidecar (SiblingCompile.cs, the `bundleResolvedDeps` argument):
//       two ordered bundles -> exit 0, 4/4 PASS                            <-- blind
//       one combined bundle -> exit 3, AL0132 'Record Item' does not contain a definition
//                              for 'DTB Repro Flag', the suite's 4 tests dropped
//
// So neither shape subsumes the other, and moving the pair out of tests/runner-extras the way
// #4080 moved the tableext-eviction pairs would silently drop the in-bundle half. That is the
// tidy-up this file exists to stop: the "move it to its own root like #4079 did" edit reads as
// finishing an unfinished job, and nothing else would fail if someone made it.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class DepTableExtPlatformBaseBothShapesTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private const string DepDir = "tests/runner-extras/dep-tableext-platform-base-dep";
    private const string MainDir = "tests/runner-extras/dep-tableext-platform-base-main";

    /// <summary>The marker proving the dep loaded as a separate dependency PACKAGE.</summary>
    private const string DepLoadMarker = "[dep] AL Runner/DTB Platform Base Dep";

    private static string Workflow() =>
        File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "bc-tests.yml"));

    /// <summary>
    /// The one step that passes the two suite directories as ordered arguments. Located by the
    /// dep directory appearing as a quoted argument, which no other step does.
    /// </summary>
    private static string OrderedBundleStep()
    {
        var text = Workflow();
        var start = text.IndexOf(DepDir, StringComparison.Ordinal);
        Assert.True(start >= 0,
            $"no step in bc-tests.yml passes {DepDir} — the ordered-bundle invocation is gone, so the "
            + "cross-bundle half of #1686 is unguarded (#4085).");
        // Back up to the enclosing `- name:` and forward to the next one.
        var stepStart = text.LastIndexOf("      - name:", start, StringComparison.Ordinal);
        Assert.True(stepStart >= 0, "could not find the enclosing step for the ordered-bundle invocation");
        var next = text.IndexOf("      - name:", start, StringComparison.Ordinal);
        return next < 0 ? text[stepStart..] : text[stepStart..next];
    }

    /// <summary>
    /// The positional arguments of the step's <c>dotnet run</c>: everything between the <c>--</c>
    /// that ends dotnet's own options and the first runner flag. Bundle order lives here and
    /// nowhere else.
    /// </summary>
    private static string ArgumentList(string step)
    {
        var start = step.IndexOf("--framework net8.0 --", StringComparison.Ordinal);
        Assert.True(start >= 0, "could not find the dotnet run invocation in the ordered-bundle step");
        var end = step.IndexOf("--package-cache", start, StringComparison.Ordinal);
        Assert.True(end > start, "the ordered-bundle step passes no --package-cache, so it would "
            + "abort on the platform dependency before running a test");
        return step[start..end];
    }

    [Fact]
    public void BothSuiteDirectories_StayInsideTheCombinedRunnerExtrasRoot()
    {
        // The in-bundle half. tests/runner-extras is passed to the combined step as ONE root, so
        // these directories being under it is exactly what makes the dep an in-bundle app group
        // and puts EmitSiblingSymbols on the path. Moving them anywhere else removes that.
        foreach (var dir in new[] { DepDir, MainDir })
            Assert.True(Directory.Exists(Path.Combine(RepoRoot, dir)),
                $"{dir} is missing. If it moved to its own root the way #4080 moved the tableext-eviction "
                + "pairs, the in-bundle half of #1686 (EmitSiblingSymbols' bundle-wide platform closure) is "
                + "no longer covered anywhere — measured #4085: that mutation is invisible to the "
                + "two-bundle run and reds the combined one.");

        // And the combined step must still take the parent as a single root, or being under it
        // buys nothing.
        Assert.Contains("tests/runner-extras \\", Workflow(), StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowAlsoRunsThePair_AsTwoOrderedBundles_DepFirst()
    {
        // The cross-bundle half. Order is the whole mechanism: the dep must be its own earlier
        // bundle so it loads as a dependency package rather than a sibling app group.
        //
        // Scoped to the ARGUMENT LIST, not to the step text. The step's `if:` and its comments
        // both name the directories, in an order that has nothing to do with bundle order — the
        // first draft of this assertion read the whole step and failed on a correct workflow.
        var step = OrderedBundleStep();
        var args = ArgumentList(step);
        var dep = args.IndexOf(DepDir, StringComparison.Ordinal);
        var main = args.IndexOf(MainDir, StringComparison.Ordinal);

        Assert.True(dep >= 0, $"the ordered-bundle step does not pass {DepDir} as an argument");

        Assert.True(main >= 0, $"the ordered-bundle step does not pass {MainDir} as an argument");
        Assert.True(dep < main,
            "the dep directory must be passed BEFORE the main directory: bundle order comes from the "
            + "argument list, and a main-first list makes the dep an ordinary later bundle.");

        // --verbose, because the [dep] marker below is a [Component] log line.
        Assert.Contains("--verbose", step, StringComparison.Ordinal);
        Assert.Contains("--strict", step, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderedBundleStep_ChecksTheDependencyPackageLoadMarker()
    {
        // Without this, the step passes whenever the tests pass — including in a future where the
        // two directories collapse back into one bundle and the cross-bundle path stops running,
        // which is #4079's defect exactly.
        //
        // Matches the grep INVOCATION, not the marker text. The first draft asserted the text and
        // survived a mutation that replaced the whole check with `if false`, because the marker is
        // also quoted in the ::error:: message the check prints — a test that names the thing
        // rather than driving it (.claude/rules/tdd.md). Anchored `^ *` because the runner indents
        // the line under its bundle.
        Assert.Matches(
            new Regex(@"grep -qE ""\^ \*\\\[dep\\\] " + Regex.Escape(DepLoadMarker[6..])),
            OrderedBundleStep());
    }

    [Fact]
    public void OrderedBundleStep_AlsoChecksAPassLineForEveryTestTheSuiteDeclares()
    {
        // #4080's finding: the marker prints at PARSE time, so a suite whose [Test] attributes all
        // vanished still printed it, ran 0P/0F/0E and exited 0. The marker check alone is a guard
        // that cannot fail. This step carries no --count-baseline either, so the PASS lines are the
        // only thing asserting the tests ran.
        //
        // Reading the names out of the AL rather than hardcoding them is what makes a renamed test
        // fail HERE, in a one-second unit test, instead of on a BC leg twenty minutes later.
        var al = File.ReadAllText(Path.Combine(RepoRoot, MainDir, "DtbTests.Codeunit.al"));
        var codeunitId = Regex.Match(al, @"codeunit\s+(\d+)\s").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(codeunitId), "could not read the test codeunit's id");

        var declared = Regex.Matches(al, @"\[Test\]\s*\r?\n\s*procedure\s+(\w+)")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.True(declared.Count >= 4,
            $"expected at least 4 [Test] procedures in {MainDir}/DtbTests.Codeunit.al, found {declared.Count}");

        var step = OrderedBundleStep();
        foreach (var name in declared)
            Assert.Contains($"Codeunit{codeunitId}.{name}", step, StringComparison.Ordinal);

        // The check must be an anchored grep, so a FAIL line mentioning the name cannot satisfy
        // it — and, as above, matching the invocation rather than a loose substring.
        Assert.Matches(new Regex(@"grep -qE ""\^PASS \+"), step);
    }

    /// <summary>
    /// The step's own `if:` must name a glob that MATCHES SOMETHING, or the step is skipped --
    /// and a skipped step reports success.
    ///
    /// Found in review of #4249: narrowing the guard to `**/*.NOPE` leaves every other test in
    /// this class green, because they all reason about the step's CONTENT and none about whether
    /// it runs. That is the same defect this file exists to fix, one level up -- the other tests
    /// prove the block works; this one proves it is reached.
    ///
    /// Note which red you are looking at. This test refuses shapes it cannot evaluate -- an `if:`
    /// it cannot parse, a glob whose directory component is a pattern, a final segment with no
    /// extension -- and those reds are about the PARSE, not about reachability. A benign reformat
    /// of the `if:` (a folded scalar, say) reds here with "calls no hashFiles()", and the fix is to
    /// teach this test the new shape, not to go looking for a skipped step.
    /// </summary>
    [Fact]
    public void OrderedBundleStep_IsReachable_ItsHashFilesGlobMatchesRealFiles()
    {
        var step = OrderedBundleStep();
        var condition = Regex.Match(step, @"^\s*if:\s*(?<expr>.+)$", RegexOptions.Multiline);
        Assert.True(condition.Success,
            "the ordered-bundle step has no `if:` -- if that is deliberate the step always runs and "
            + "this test should be deleted, but silently losing the condition is not the same thing.");

        var globs = Regex.Matches(condition.Groups["expr"].Value, @"hashFiles\('(?<glob>[^']+)'\)")
            .Select(m => m.Groups["glob"].Value).ToList();
        Assert.True(globs.Count > 0,
            $"the step's `if:` calls no hashFiles(): {condition.Groups["expr"].Value.Trim()} -- this "
            + "test reads that call to decide whether the step can run at all.");

        foreach (var glob in globs)
        {
            // hashFiles() is repo-root-relative and '' when nothing matches, which makes the step
            // skip -- and a skipped step reports SUCCESS.
            var star = glob.IndexOf('*');
            Assert.True(star > 0, $"unexpected hashFiles glob with no wildcard: {glob}");

            var lastSlash = glob.LastIndexOf('/', star);
            var dirPart = glob[..lastSlash];
            var tailPart = glob[(lastSlash + 1)..];

            // REFUSED, not approximated: a wildcard left of the last literal '/' means the
            // directory itself is a pattern, and checking a literal ANCESTOR then answers a weaker
            // question than the glob asks. Found in review -- `...-GONE*/**/*.al` left every test
            // green while hashFiles() matched nothing and the step was skipped, because the check
            // collapsed to "are there .al files under tests/runner-extras", which there are.
            // Matching properly needs a glob engine this assembly does not reference, so this
            // refuses instead: an unmeasurable shape must not report the success state
            // (guards-need-a-third-state.md).
            // The tail is everything after the last '/' BEFORE the first '*', so any further '/'
            // in it means a directory level is itself a pattern -- `.../base-GONE*/**/*.al` has
            // tail `base-GONE*/**/*.al`. Checking the literal ancestor then answers a weaker
            // question than the glob asks, which is how that shape stayed green.
            var wildcardDirs = tailPart.Split('/');
            Assert.True(
                wildcardDirs.Length <= 2 && !wildcardDirs[0].Contains('*', StringComparison.Ordinal)
                    || wildcardDirs.SkipLast(1).All(seg => seg == "**"),
                $"the step's `if:` tests hashFiles('{glob}'), where a DIRECTORY level is itself a "
                + $"pattern ('{string.Join("/", wildcardDirs.SkipLast(1))}'). This test can only "
                + "check a literal directory plus '**', so it cannot tell whether that glob matches "
                + "anything -- and a glob matching nothing skips the step, which reports success. "
                + "Use a literal directory here, or teach this test a real glob matcher (#4249).");

            var dot = tailPart.LastIndexOf('.');
            Assert.True(dot >= 0,
                $"the step's `if:` tests hashFiles('{glob}'), whose final segment has no extension. "
                + "This test matches on extension, so it cannot evaluate that glob -- same refusal "
                + "as above rather than a guess (#4249).");
            var ext = tailPart[(dot + 1)..];

            var dir = Path.Combine(RepoRoot, dirPart.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(Directory.Exists(dir),
                $"the step's `if:` tests hashFiles('{glob}'), but '{dir}' does not exist, so it "
                + "evaluates to '' and the step is SKIPPED -- which reports success (#4249).");
            Assert.True(
                Directory.EnumerateFiles(dir, "*." + ext, SearchOption.AllDirectories).Any(),
                $"the step's `if:` tests hashFiles('{glob}'), which matches no file under '{dir}', "
                + "so it evaluates to '' and the step is SKIPPED -- reporting success while running "
                + "nothing (#4249).");
        }
    }

    /// <summary>
    /// The step's verification block, from `missing=0` to its final `exit`, with the log filename
    /// replaced by $LOG so it can be run against a fixture.
    ///
    /// Scoped through <see cref="OrderedBundleStep"/> deliberately: `exit "$missing"` occurs TWICE
    /// in bc-tests.yml (#4249), and a file-wide search would extract the wrong one.
    /// </summary>
    private static string VerificationBlock()
    {
        var step = OrderedBundleStep();
        var start = step.IndexOf("missing=0", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "the ordered-bundle step has no `missing=0`, so the block this test executes is gone "
            + "(#4249). If the guard was rewritten, re-point this extraction at the new shape.");
        var end = step.IndexOf("exit \"$missing\"", start, StringComparison.Ordinal);
        Assert.True(end > start,
            "the ordered-bundle step's verification block does not end in `exit \"$missing\"`. "
            + "Either it no longer propagates its own verdict -- which is the defect #4249 is "
            + "about -- or it was restructured and this extraction needs re-pointing.");

        var block = step[start..end] + "exit \"$missing\"";
        // Dedent: the YAML block is indented under `run: |`.
        var lines = block.Split('\n').Select(l => l.Length > 10 ? l[10..] : l.TrimStart());
        return string.Join("\n", lines)
            .Replace("dep-tableext-platform-base.log", "\"$LOG\"", StringComparison.Ordinal);
    }

    /// <summary>Runs the extracted block against a log fixture and returns its exit code.</summary>
    private static (int Exit, string Output) RunGuard(string log)
    {
        // TestScratch, not Path.GetTempPath(): ScratchDirs records an owner, so a killed test
        // host cannot leak this directory permanently (ScratchDirOwnershipGuardTests, #2743).
        var dir = TestScratch.Dir(nameof(DepTableExtPlatformBaseBothShapesTests));
        Directory.CreateDirectory(dir);   // Reserve() records an owner; it does not create.
        try
        {
            var logPath = Path.Combine(dir, "run.log");
            File.WriteAllText(logPath, log);
            var script = Path.Combine(dir, "guard.sh");
            File.WriteAllText(script, "#!/usr/bin/env bash\nLOG=\"$1\"\n" + VerificationBlock() + "\n");

            var psi = new ProcessStartInfo("bash", $"\"{script}\" \"{logPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = dir,
            };
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            Assert.True(proc.WaitForExit(60_000), "the extracted guard did not finish in 60s");
            return (proc.ExitCode, stdout + stderr);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* owned by ScratchDirs either way */ }
        }
    }

    /// <summary>A log with the dep marker and a PASS line for every declared test.</summary>
    private static string GoodLog()
    {
        var al = File.ReadAllText(Path.Combine(RepoRoot, MainDir, "DtbTests.Codeunit.al"));
        var id = Regex.Match(al, @"codeunit\s+(\d+)\s").Groups[1].Value;
        var names = Regex.Matches(al, @"\[Test\]\s*\r?\n\s*procedure\s+(\w+)")
            .Select(m => m.Groups[1].Value);
        var sb = new StringBuilder();
        sb.AppendLine("  [dep] AL Runner/DTB Platform Base Dep 1.0.0.0");
        foreach (var n in names) sb.AppendLine($"PASS Codeunit{id}.{n} 3ms");
        sb.AppendLine("-> 4P/0F/0E");
        return sb.ToString();
    }

    /// <summary>
    /// The step's guard must actually REFUSE, not merely contain the right grep text.
    ///
    /// #4249: three mutations that each disarm the step completely -- `exit "$missing"` to
    /// `exit 0`, `missing=1` to `missing=0`, and dropping `( |$)` from the PASS pattern -- left all
    /// four of this class's other tests GREEN, because they assert over the step's TEXT. Two of
    /// those make the step report success unconditionally.
    ///
    /// This test executes the block instead, so a disarmed guard fails here rather than passing
    /// CI forever while asserting nothing (.claude/rules/tdd.md, "a test that names the thing is
    /// not a test that drives it").
    /// </summary>
    [Fact]
    public void OrderedBundleStep_VerificationBlock_ActuallyRefuses_NotJustContainsTheGrep()
    {
        var good = GoodLog();

        // A real log passes. Without this the rest proves only that the block fails on everything.
        var (okExit, okOut) = RunGuard(good);
        Assert.True(okExit == 0,
            $"the guard rejects a log carrying the marker and every PASS line (exit {okExit}):\n{okOut}");

        // The dep did not load as a separate package -- #4079's collapse-to-one-bundle shape.
        var noMarker = string.Join("\n",
            good.Split('\n').Where(l => !l.Contains(DepLoadMarker, StringComparison.Ordinal)));
        Assert.True(RunGuard(noMarker).Exit != 0,
            "the guard accepts a log with no '[dep] ...' line, so the step would pass when the two "
            + "directories collapse into one bundle -- the defect it exists to catch (#4085).");

        // Markers print at parse time, so a suite that ran nothing still prints them (#4080).
        var noPass = string.Join("\n",
            good.Split('\n').Where(l => !l.StartsWith("PASS ", StringComparison.Ordinal)));
        Assert.True(RunGuard(noPass).Exit != 0,
            "the guard accepts a log with no PASS lines, so a suite that ran zero tests would pass.");

        // A FAIL line naming the test must not satisfy the PASS check.
        Assert.True(RunGuard(good.Replace("PASS Codeunit", "FAIL Codeunit", StringComparison.Ordinal)).Exit != 0,
            "the guard accepts FAIL lines where it requires PASS.");

        // tdd.md's prefix trap: a longer name must not satisfy the check for a shorter one.
        // This is what `( |$)` buys, and dropping it is one of #4249's three silent mutations.
        var suffixed = good.Replace("_ReturnsInsertedValue 3ms", "_ReturnsInsertedValueExtra 3ms",
            StringComparison.Ordinal);
        Assert.True(RunGuard(suffixed).Exit != 0,
            "the guard accepts a PASS line whose test name merely STARTS WITH the required one, so "
            + "renaming a test to a longer name would silently stop asserting it.");
    }
}
