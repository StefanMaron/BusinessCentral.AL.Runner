// #4457: the app-group registration inside ProgramSupport.BuildSiblingSourceDeps was
// unexercised -- mutating it out left all 74 runner-extras tests green. This file pins the
// CI step that makes it load-bearing, and executes that step's verification block so a
// disarmed guard fails here rather than reporting success forever.
//
// Measured on BC 28.1.49838.53910, fresh cache root per run, `app-group-visibility-c` then
// `app-group-visibility-b` as two ordered bundles:
//
//   registration removed at BuildSiblingSourceDeps (SiblingCompile.cs)
//       -> exit 1, 42P/8F. All eight failures are Codeunit62612 `GroupA...IsNotListed`,
//          e.g. "Assert.IsFalse failed. AllObj in app group B lists table 62600 of
//          unrelated app group A". C's 24 stay GREEN.
//   registration removed at the [layered] impl site instead (the OTHER call in the same file)
//       -> exit 0, 50P/0F                                              <-- does not discriminate
//   unmutated
//       -> exit 0, 50P/0F
//
// So this step pins the sibling-source site specifically, and C's green half is the control
// showing it discriminates rather than reddening on everything.
//
// Why no other invocation observes it: the combined `tests/runner-extras` run passes ONE root,
// so A, B and C are suites of a single bundle and their dirs are registered by the suite walk in
// Program.cs, never by BuildSiblingSourceDeps. The dep-tableext pair passes its dep AS A BUNDLE,
// which excludes that dir from the sibling-source candidate set (`bundleRoots.Contains`) --
// measured: that invocation reaches BuildSiblingSourceDeps with 82 candidate sibling apps and
// matches none. A bundle whose dependency is a sibling dir NOT passed as a bundle is the shape,
// and this is the only place in CI that runs one.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class AppGroupVisibilitySiblingSourceDepStepTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private const string CDir = "tests/runner-extras/app-group-visibility-c";
    private const string BDir = "tests/runner-extras/app-group-visibility-b";
    private const string ADir = "tests/runner-extras/app-group-visibility-a";

    private const string LogName = "app-group-visibility-cb.log";

    /// <summary>
    /// The eight assertions the #4457 mutation reds. Read as a literal list because the property
    /// is "exactly this measured set is asserted", not "some tests are asserted" -- a later
    /// addition to group B must not silently enlarge what this step claims to have measured.
    /// </summary>
    private static readonly string[] MutationSensitiveTests =
    {
        "Codeunit62612.AllObj_GroupATable_IsNotListed",
        "Codeunit62612.AllObjWithCaption_GroupATable_IsNotListed",
        "Codeunit62612.TableMetadata_GroupATable_IsNotListed",
        "Codeunit62612.FieldTable_GroupATable_IsNotListed",
        "Codeunit62612.CodeunitMetadata_GroupACodeunit_IsNotListed",
        "Codeunit62612.PageMetadata_GroupAPage_IsNotListed",
        "Codeunit62612.ReportMetadata_GroupAReport_IsNotListed",
        "Codeunit62612.ReportDataItems_GroupAReport_IsNotListed",
    };

    /// <summary>The control: green under the mutation, so it proves the step discriminates.</summary>
    private const string ControlTest = "Codeunit62622.AllObj_DependencyATable_IsListed";

    private static string Workflow() =>
        File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "bc-tests.yml"));

    /// <summary>
    /// The one step that passes app-group-visibility-c as a quoted argument. Located by the log
    /// filename, which is unique to this step -- the directory name also appears in the combined
    /// step's comments, and matching on that took the wrong block.
    /// </summary>
    private static string Step()
    {
        var text = Workflow();
        var start = text.IndexOf(LogName, StringComparison.Ordinal);
        Assert.True(start >= 0,
            $"no step in bc-tests.yml writes {LogName} -- the ordered-bundle invocation that makes "
            + "the sibling-source app-group registration load-bearing is gone, so removing that "
            + "registration would again leave every test green (#4457).");
        var stepStart = text.LastIndexOf("      - name:", start, StringComparison.Ordinal);
        Assert.True(stepStart >= 0, "could not find the enclosing step for the app-group-visibility invocation");
        var next = text.IndexOf("      - name:", start, StringComparison.Ordinal);
        return next < 0 ? text[stepStart..] : text[stepStart..next];
    }

    /// <summary>
    /// The positional arguments of the step's <c>dotnet run</c>: everything between the <c>--</c>
    /// ending dotnet's own options and the first runner flag. Bundle order lives here and nowhere
    /// else -- the step's comments name the directories in an order that has nothing to do with it.
    /// </summary>
    private static string ArgumentList(string step)
    {
        var start = step.IndexOf("--framework net8.0 --", StringComparison.Ordinal);
        Assert.True(start >= 0, "could not find the dotnet run invocation in the app-group-visibility step");
        var end = step.IndexOf("--package-cache", start, StringComparison.Ordinal);
        Assert.True(end > start, "the app-group-visibility step passes no --package-cache, so it would "
            + "abort on the platform dependency before running a test");
        return step[start..end];
    }

    [Fact]
    public void GroupA_IsNotPassedAsABundle_SoItStaysASiblingSourceDep()
    {
        // The whole mechanism. BuildSiblingSourceDeps builds its candidate set from the
        // sub-directories of each bundle root's PARENT and skips any that is itself a bundle
        // (`if (bundleRoots.Contains(subAbs)) continue;`). Passing A here would exclude it, C's
        // dependency would resolve as an ordinary bundle, and the registration under test would
        // never run -- which is exactly why the dep-tableext pair cannot observe this (#4457).
        var args = ArgumentList(Step());
        Assert.False(args.Contains(ADir, StringComparison.Ordinal),
            $"the step passes {ADir} as a bundle argument. That excludes it from the sibling-source "
            + "candidate set, so C's dependency on A no longer routes through BuildSiblingSourceDeps "
            + "and the registration this step exists to pin is never reached -- the step would then "
            + "be green with that registration deleted (#4457).");

        // ...and A must still exist to be found as a sibling at all.
        Assert.True(Directory.Exists(Path.Combine(RepoRoot, ADir)),
            $"{ADir} is missing, so there is no sibling source app for C's dependency to match.");
    }

    [Fact]
    public void Step_PassesCAndB_AsTwoOrderedBundles_CFirst()
    {
        var step = Step();
        var args = ArgumentList(step);
        var c = args.IndexOf(CDir, StringComparison.Ordinal);
        var b = args.IndexOf(BDir, StringComparison.Ordinal);

        Assert.True(c >= 0, $"the step does not pass {CDir} as an argument: C is what declares the "
            + "dependency on A, so without it nothing reaches BuildSiblingSourceDeps' match loop.");
        Assert.True(b >= 0, $"the step does not pass {BDir} as an argument: B is the OBSERVER. C "
            + "depends on A, so A is inside C's visible closure whether or not it has a recorded "
            + "owner -- C alone cannot tell the two apart, and all 24 of its tests pass with the "
            + "registration removed (#4457).");
        Assert.True(c < b,
            "app-group-visibility-c must be passed BEFORE app-group-visibility-b. Both orders red 8 "
            + "under the mutation, but b-first swaps one assertion for an unrelated "
            + "PageMetadata_OwnPage_IsListed; c-first gives the eight GroupA...IsNotListed failures "
            + "that name the mechanism.");

        Assert.Contains("--strict", step, StringComparison.Ordinal);
    }

    [Fact]
    public void Step_AssertsAPassLineForEveryMutationSensitiveTest_AndForTheControl()
    {
        // This step carries no --count-baseline (its two groups are already counted by the
        // combined runner-extras step, and it adds no tests), so these PASS lines are the ONLY
        // thing asserting the tests ran. #4080's shape: markers and summaries print even when a
        // suite's [Test] attributes have all vanished and the run exits 0 over zero tests.
        var step = Step();
        foreach (var name in MutationSensitiveTests)
            Assert.True(step.Contains(name, StringComparison.Ordinal),
                $"the step does not assert a PASS line for {name}, which is one of the eight tests "
                + "the #4457 mutation reds. Dropping it shrinks what this step measures without "
                + "anything going red.");

        Assert.True(step.Contains(ControlTest, StringComparison.Ordinal),
            $"the step does not assert a PASS line for {ControlTest}. That is the CONTROL: it stays "
            + "green under the mutation, so asserting it is what shows this step discriminates "
            + "rather than reddening on everything (.claude/rules/tdd.md).");

        // Anchored grep, so a FAIL line mentioning the name cannot satisfy the check -- and
        // matching the INVOCATION rather than a loose substring, because the names are also
        // quoted in the ::error:: message the check prints.
        Assert.Matches(new Regex(@"grep -qE ""\^PASS \+"), step);
    }

    /// <summary>
    /// The step must be able to RUN. A step whose condition is false is skipped, and a skipped
    /// step reports SUCCESS -- so every assertion above can be satisfied by a block that never
    /// executes (#4249, #4255, measured on the dep-tableext step).
    /// </summary>
    [Fact]
    public void Step_IsReachable_ConditionAndNeighbours()
    {
        var step = Step();
        var condition = Regex.Match(step, @"^\s*if:\s*(?<expr>.+)$", RegexOptions.Multiline);
        Assert.True(condition.Success,
            "the app-group-visibility step has no `if:` -- if that is deliberate the step always "
            + "runs and this assertion should be deleted, but silently losing the condition is not "
            + "the same thing.");

        var globs = Regex.Matches(condition.Groups["expr"].Value, @"hashFiles\('(?<glob>[^']+)'\)")
            .Select(m => m.Groups["glob"].Value).ToList();
        Assert.True(globs.Count > 0,
            $"the step's `if:` calls no hashFiles(): {condition.Groups["expr"].Value.Trim()}");

        foreach (var glob in globs)
        {
            var star = glob.IndexOf('*');
            Assert.True(star > 0, $"unexpected hashFiles glob with no wildcard: {glob}");
            var lastSlash = glob.LastIndexOf('/', star);
            var dirPart = glob[..lastSlash];
            var tailPart = glob[(lastSlash + 1)..];

            // REFUSED rather than approximated, same as the dep-tableext step's guard: a wildcard
            // in a DIRECTORY level means checking a literal ancestor answers a weaker question
            // than the glob asks, and a glob matching nothing skips the step silently
            // (guards-need-a-third-state.md).
            var wildcardDirs = tailPart.Split('/');
            Assert.True(
                wildcardDirs.Length <= 2 && !wildcardDirs[0].Contains('*', StringComparison.Ordinal)
                    || wildcardDirs.SkipLast(1).All(seg => seg == "**"),
                $"the step's `if:` tests hashFiles('{glob}'), where a DIRECTORY level is itself a "
                + "pattern. This test can only check a literal directory plus '**', so it cannot "
                + "tell whether that glob matches anything (#4249).");

            var dot = tailPart.LastIndexOf('.');
            Assert.True(dot >= 0,
                $"the step's `if:` tests hashFiles('{glob}'), whose final segment has no extension, "
                + "so this test cannot evaluate it (#4249).");
            var ext = tailPart[(dot + 1)..];

            var dir = Path.Combine(RepoRoot, dirPart.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(Directory.Exists(dir),
                $"the step's `if:` tests hashFiles('{glob}'), but '{dir}' does not exist, so it "
                + "evaluates to '' and the step is SKIPPED -- which reports success (#4249).");
            Assert.True(
                Directory.EnumerateFiles(dir, "*." + ext, SearchOption.AllDirectories).Any(),
                $"the step's `if:` tests hashFiles('{glob}'), which matches no file under '{dir}', "
                + "so the step is SKIPPED while reporting success (#4249).");
        }

        // The globs say the hashFiles() call can return something and nothing about the rest of
        // the condition: inverting `!= ''` to `== ''`, or appending `&& false`, skips the step
        // with every other assertion here green (#4255). So pin the whole expression,
        // whitespace-normalised because YAML line-wrapping is not a semantic change.
        var actual = Regex.Replace(condition.Groups["expr"].Value.Trim(), @"\s+", " ");
        const string Expected =
            "${{ always() && hashFiles('tests/runner-extras/app-group-visibility-c/**/*.al') != '' }}";
        Assert.True(actual == Expected,
            "the app-group-visibility step's `if:` is no longer the condition this test was written "
            + $"against.\n  expected: {Expected}\n  actual:   {actual}\n"
            + "A skipped step reports SUCCESS, so an edit here disables the whole step with nothing "
            + "going red (#4255). If the change is deliberate, update this expectation in the same "
            + "commit and say why the step should still run on a pull request.");

        // Two shapes the expression pin cannot see, because they are properties of the step's
        // NEIGHBOURS rather than of its condition (#4255).
        Assert.False(step.Contains("continue-on-error", StringComparison.Ordinal),
            "the step declares `continue-on-error`, so its failure no longer fails the leg: the "
            + "guard runs, prints ::error::, and CI stays green (#4255).");

        var wf = Workflow();
        var stepAt = wf.IndexOf(LogName, StringComparison.Ordinal);
        // Locate the enclosing job by SHAPE, not by name -- a rename must not read as "no
        // job-level if:", which is the silent direction. A line at column 2 terminates an
        // enclosing block scalar, so anything matched here under `jobs:` IS a job (#4255).
        var jobs = Regex.Matches(wf, @"(?m)^  (?<name>[^\s#][^\n]*?):[ \t]*(?:#[^\n]*)?$")
            .Where(m => m.Index < stepAt).ToList();
        Assert.True(jobs.Count > 0,
            "could not locate the job declaring the app-group-visibility step (#4255).");
        var jobStart = jobs[^1].Index;
        var stepsAt = wf.IndexOf("\n    steps:", jobStart, StringComparison.Ordinal);
        Assert.True(stepsAt > jobStart, "the enclosing job has no `steps:` -- shape changed (#4255).");
        Assert.False(wf[jobStart..stepsAt].Contains("\n    if:", StringComparison.Ordinal),
            $"the job declaring the app-group-visibility step ({jobs[^1].Groups["name"].Value}) "
            + "carries a job-level `if:`, which skips every step in it while the step's own "
            + "condition stays byte-identical (#4255).");
    }

    /// <summary>
    /// The step's verification block, from `missing=0` to its final `exit`, with the log filename
    /// replaced by $LOG so it can be executed against a fixture.
    ///
    /// Scoped through <see cref="Step"/> deliberately: `exit "$missing"` occurs several times in
    /// bc-tests.yml, and a file-wide search would extract the wrong one (#4249).
    /// </summary>
    private static string VerificationBlock()
    {
        var step = Step();
        var start = step.IndexOf("missing=0", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "the step has no `missing=0`, so the block this test executes is gone (#4249).");
        var end = step.IndexOf("exit \"$missing\"", start, StringComparison.Ordinal);
        Assert.True(end > start,
            "the step's verification block does not end in `exit \"$missing\"`, so it no longer "
            + "propagates its own verdict -- the defect #4249 is about.");

        var block = step[start..end] + "exit \"$missing\"";
        var lines = block.Split('\n').Select(l => l.Length > 10 ? l[10..] : l.TrimStart());
        return string.Join("\n", lines).Replace(LogName, "\"$LOG\"", StringComparison.Ordinal);
    }

    private static (int Exit, string Output) RunGuard(string log)
    {
        // TestScratch, not Path.GetTempPath(): ScratchDirs records an owner, so a killed test host
        // cannot leak this directory permanently (ScratchDirOwnershipGuardTests, #2743).
        var dir = TestScratch.Dir(nameof(AppGroupVisibilitySiblingSourceDepStepTests));
        Directory.CreateDirectory(dir);
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

    /// <summary>A log carrying a PASS line for every name the step asserts.</summary>
    private static string GoodLog()
    {
        var sb = new StringBuilder();
        sb.AppendLine("  -> 24P/0F/0E across 24 tests, 0 suite errors (1.1s)");
        sb.AppendLine("  -> 26P/0F/0E across 26 tests, 0 suite errors (0.4s)");
        foreach (var n in MutationSensitiveTests) sb.AppendLine($"PASS  {n} (2ms)");
        sb.AppendLine($"PASS  {ControlTest} (1ms)");
        return sb.ToString();
    }

    /// <summary>
    /// The guard must actually REFUSE, not merely contain the right grep text. #4249 measured
    /// three mutations that each disarm such a block -- `exit "$missing"` to `exit 0`, `missing=1`
    /// to `missing=0`, and dropping `( |$)` from the PASS pattern -- while every text-reading
    /// assertion stayed green. This executes the block instead
    /// (.claude/rules/tdd.md, "a test that names the thing is not a test that drives it").
    /// </summary>
    [Fact]
    public void VerificationBlock_ActuallyRefuses_NotJustContainsTheGrep()
    {
        var good = GoodLog();

        // A real log passes. Without this the rest proves only that the block fails on everything.
        var (okExit, okOut) = RunGuard(good);
        Assert.True(okExit == 0,
            $"the guard rejects a log carrying every PASS line it asks for (exit {okExit}):\n{okOut}");

        // A suite that ran zero tests still prints its summary lines (#4080).
        var noPass = string.Join("\n",
            good.Split('\n').Where(l => !l.StartsWith("PASS ", StringComparison.Ordinal)));
        Assert.True(RunGuard(noPass).Exit != 0,
            "the guard accepts a log with no PASS lines, so a run over zero tests would pass.");

        // Each mutation-sensitive test individually: dropping any ONE of the eight must refuse,
        // or the step silently measures less than it claims. A whole-set check passes when seven
        // of eight are present, which is the shape that rots.
        foreach (var name in MutationSensitiveTests)
        {
            var missingOne = string.Join("\n",
                good.Split('\n').Where(l => !l.Contains(name, StringComparison.Ordinal)));
            Assert.True(RunGuard(missingOne).Exit != 0,
                $"the guard accepts a log with no PASS line for {name}, one of the eight tests the "
                + "#4457 mutation reds -- so that assertion could vanish with CI staying green.");
        }

        // The control too: losing it would leave the step unable to show it discriminates.
        var noControl = string.Join("\n",
            good.Split('\n').Where(l => !l.Contains(ControlTest, StringComparison.Ordinal)));
        Assert.True(RunGuard(noControl).Exit != 0,
            $"the guard accepts a log with no PASS line for the control {ControlTest}.");

        // A FAIL line naming the test must not satisfy the PASS check.
        Assert.True(RunGuard(good.Replace("PASS  Codeunit", "FAIL  Codeunit", StringComparison.Ordinal)).Exit != 0,
            "the guard accepts FAIL lines where it requires PASS.");

        // tdd.md's prefix trap: a longer name must not satisfy the check for a shorter one. This
        // is what `( |$)` buys, and dropping it is one of #4249's three silent mutations.
        var suffixed = good.Replace("_IsNotListed (2ms)", "_IsNotListedExtra (2ms)", StringComparison.Ordinal);
        Assert.True(RunGuard(suffixed).Exit != 0,
            "the guard accepts a PASS line whose test name merely STARTS WITH the required one, so "
            + "renaming a test to a longer name would silently stop asserting it.");
    }
}
