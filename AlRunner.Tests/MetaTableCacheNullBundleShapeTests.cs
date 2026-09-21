// #4450: `_metaTableCache` is a ConcurrentDictionary whose GetOrAdd caches the NULL
// BuildNCLMetaTable returns for a table not yet in `_parsedTables`, and GetOrAdd never replaces
// an existing entry. In a run with SEVERAL bundles the first bundle can ask about a later
// bundle's table before that bundle's source dir is registered, and the later bundle's OWN table
// then answers null for the rest of the process -- Field.Get on it returns false, silently.
//
// The AL proof lives in tests/runner-extras/metatable-cache-null-{first,second}, and the whole
// value of those suites is the SHAPE they are run in. Measured on BC 28.1, fresh cache root:
//
//   two independent bundles, -first first  -> Failed: 2, Passed: 4   (the defect)
//   two independent bundles, -second first -> Failed: 0, Passed: 6   (positional)
//   one combined bundle (tests/runner-extras) -> 500/500, 0 failed   <-- blind
//
// So the combined run cannot observe this, and the suites are worth nothing without the
// workflow step that runs them as two bundles. This file pins that step: nothing else would go
// red if the step were deleted, the suites folded into the combined root, or the argument order
// reversed -- and each of those edits reads as ordinary tidying.
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class MetaTableCacheNullBundleShapeTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private const string FirstDir = "tests/runner-extras/metatable-cache-null-first";
    private const string SecondDir = "tests/runner-extras/metatable-cache-null-second";

    private static string Workflow() =>
        File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "bc-tests.yml"));

    /// <summary>
    /// The one step that passes the two suite directories as arguments, located by the -first
    /// directory appearing as an argument, which no other step does.
    /// </summary>
    private static string MultiBundleStep()
    {
        var text = Workflow();
        var start = text.IndexOf(FirstDir, StringComparison.Ordinal);
        Assert.True(start >= 0,
            $"no step in bc-tests.yml passes {FirstDir} -- the two-independent-bundles invocation is "
            + "gone, so #4450's proving suites run only inside the combined tests/runner-extras "
            + "bundle, where they pass whether the defect is present or not.");
        var stepStart = text.LastIndexOf("      - name:", start, StringComparison.Ordinal);
        Assert.True(stepStart >= 0, "could not find the enclosing step for the multi-bundle invocation");
        var next = text.IndexOf("      - name:", start, StringComparison.Ordinal);
        return next < 0 ? text[stepStart..] : text[stepStart..next];
    }

    /// <summary>
    /// The positional arguments of the step's <c>dotnet run</c>: everything between the <c>--</c>
    /// ending dotnet's own options and the first runner flag. Bundle ORDER lives here and nowhere
    /// else, and the order is what makes the defect reproducible.
    /// </summary>
    private static string ArgumentList(string step)
    {
        var start = step.IndexOf("--framework net8.0 --", StringComparison.Ordinal);
        Assert.True(start >= 0, "could not find the dotnet run invocation in the multi-bundle step");
        var end = step.IndexOf("--package-cache", start, StringComparison.Ordinal);
        Assert.True(end > start, "the multi-bundle step passes no --package-cache, so it would abort "
            + "on the platform dependency before running a test");
        return step[start..end];
    }

    // ── 1. THE SHAPE ─────────────────────────────────────────────────────────────────────────
    //
    // Two SEPARATE path arguments is what builds two bundles. One argument -- either a single
    // suite or a shared parent root -- builds one, and one bundle cannot express the defect.

    [Fact]
    public void WorkflowRunsTheTwoSuites_AsTwoSeparateBundleArguments()
    {
        var args = ArgumentList(MultiBundleStep());

        Assert.Contains(FirstDir, args, StringComparison.Ordinal);
        Assert.Contains(SecondDir, args, StringComparison.Ordinal);

        // -first BEFORE -second. Reversed, the poisoning lookup happens after -second is
        // registered and all six tests pass on the unfixed runner too -- measured, and it is the
        // positional property the issue reports. A reordering would leave this suite green while
        // asserting nothing.
        Assert.True(
            args.IndexOf(FirstDir, StringComparison.Ordinal)
            < args.IndexOf(SecondDir, StringComparison.Ordinal),
            "the multi-bundle step must pass metatable-cache-null-first BEFORE "
            + "metatable-cache-null-second: the defect is positional, and in the other order all "
            + "six tests pass on an unfixed runner (#4450).");
    }

    [Fact]
    public void TheTwoSuites_DeclareNoDependencyOnEachOther()
    {
        // A dependency between them would make the second load the first as a PACKAGE rather than
        // as an unrelated sibling app group -- which is the dep-tableext-platform-base shape, and
        // is NOT the shape that reproduces this. The distinction is invisible from the argument
        // list alone, which is why it is asserted here rather than left to the manifests.
        foreach (var dir in new[] { FirstDir, SecondDir })
        {
            var manifest = File.ReadAllText(Path.Combine(RepoRoot, dir, "app.json"));
            using var doc = System.Text.Json.JsonDocument.Parse(manifest);
            var deps = doc.RootElement.GetProperty("dependencies");
            Assert.True(deps.GetArrayLength() == 0,
                $"{dir}/app.json declares a dependency. The two suites must be UNRELATED sibling "
                + "app groups: a declared dependency makes one load as a package, which is a "
                + "different code path and does not reproduce #4450.");
        }
    }

    // ── 2. THE VERDICT ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void MultiBundleStep_AssertsAPassLineForEveryTestTheSuitesDeclare()
    {
        var step = MultiBundleStep();

        // Every [Test] procedure across both suites. Read from the .al files rather than listed
        // here, so adding a test to either suite without adding it to the step's check list reds
        // this rather than silently going unasserted.
        var expected = new List<string>();
        foreach (var (dir, codeunit) in new[] { (FirstDir, 66102), (SecondDir, 66112) })
        {
            foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot, dir), "*Tests.Codeunit.al"))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(text,
                             @"\[Test\]\s*\r?\n\s*procedure\s+(?<name>\w+)\s*\("))
                    expected.Add($"Codeunit{codeunit}.{m.Groups["name"].Value}");
            }
        }

        Assert.True(expected.Count >= 6,
            $"expected at least the six tests #4450's suites declare, found {expected.Count}: "
            + string.Join(", ", expected));

        foreach (var name in expected)
            Assert.True(step.Contains(name, StringComparison.Ordinal),
                $"the multi-bundle step does not check for a PASS line naming {name}. Without a "
                + "per-test check the step is green whenever the run is green, INCLUDING a run "
                + "that compiled nothing and executed 0 tests (#4080's shape).");
    }

    [Fact]
    public void MultiBundleStep_FailsTheJob_RatherThanOnlyReporting()
    {
        var step = MultiBundleStep();

        Assert.Contains("::error::", step, StringComparison.Ordinal);
        Assert.Contains("exit \"$missing\"", step, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error: true", step);
    }

    // ── 3. REACHABILITY ──────────────────────────────────────────────────────────────────────
    //
    // Every assertion above reasons about the step's CONTENT, and all of them stay green while
    // the step is SKIPPED -- which reports success. This one asks whether it can run at all.

    [Fact]
    public void MultiBundleStep_IsReachable_ItsHashFilesGlobMatchesRealFiles()
    {
        var step = MultiBundleStep();
        var condition = Regex.Match(step, @"^\s*if:\s*(?<expr>.+)$", RegexOptions.Multiline);
        Assert.True(condition.Success, "the multi-bundle step has no `if:` this test can read");

        var globs = Regex.Matches(condition.Groups["expr"].Value, @"hashFiles\('(?<glob>[^']+)'\)")
            .Select(m => m.Groups["glob"].Value).ToList();
        Assert.True(globs.Count > 0,
            $"the step's `if:` calls no hashFiles(): {condition.Groups["expr"].Value.Trim()} -- if the "
            + "gating expression changed shape, re-derive what now decides whether this step runs.");

        foreach (var glob in globs)
        {
            // hashFiles() is repo-root-relative and answers '' when nothing matches, which SKIPS
            // the step -- reported as success. So a glob naming a directory that was renamed or a
            // file extension that no longer exists retires this coverage in silence.
            var star = glob.IndexOf('*');
            Assert.True(star > 0, $"unexpected hashFiles glob with no wildcard: {glob}");
            var dir = Path.Combine(RepoRoot, glob[..star].TrimEnd('/'));
            Assert.True(Directory.Exists(dir),
                $"the step's `if:` tests hashFiles('{glob}'), but '{dir}' does not exist, so the step "
                + "is skipped -- and a skipped step reports SUCCESS (#4450).");
            var ext = Path.GetExtension(glob);
            Assert.True(Directory.EnumerateFiles(dir, "*" + ext, SearchOption.AllDirectories).Any(),
                $"the step's `if:` tests hashFiles('{glob}'), which matches no file under '{dir}', so "
                + "the step is skipped -- and a skipped step reports SUCCESS (#4450).");
        }
    }

    // ── 4. THE SUITES STAY IN THE COMBINED ROOT TOO ──────────────────────────────────────────

    [Fact]
    public void BothSuiteDirectories_StayInsideTheCombinedRunnerExtrasRoot()
    {
        // They are cheap, and the combined run is where the count baseline accounts for them.
        // Moving them out to a private root -- the #4079 tidy-up shape -- would drop them from
        // that accounting while leaving every assertion above green.
        foreach (var dir in new[] { FirstDir, SecondDir })
        {
            Assert.StartsWith("tests/runner-extras/", dir, StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(RepoRoot, dir)), $"{dir} does not exist");
        }
    }
}
