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
        Assert.Contains(DepLoadMarker, OrderedBundleStep(), StringComparison.Ordinal);
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

        // The check must be ANCHORED, so a FAIL line mentioning the name cannot satisfy it.
        Assert.Contains("^PASS +", step, StringComparison.Ordinal);
    }
}
