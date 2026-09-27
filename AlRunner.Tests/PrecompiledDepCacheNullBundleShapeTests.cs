// #4783: the .app-registration twin of #4450/#4452. tests/runner-extras/precompiled-dep-cache-null-first
// asks about table 61600 before tests/runner-extras/xmlport-precompiled-dep-metadata's dependency
// .app (the only thing declaring 61600) is registered. Measured on BC 28.1.49838.53910:
//
//   ordered, -first first               -> Failed: 2, Passed: 2   (the defect)
//   ordered, xmlport-precompiled first  -> Failed: 0, Passed: 4   (positional)
//
// The combined tests/runner-extras run registers every .app before any test runs, so it cannot
// observe this. This file pins the bc-tests.yml step that runs the pair in order; shape copied
// from MetaObjectCacheNullBundleShapeTests.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class PrecompiledDepCacheNullBundleShapeTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private const string FirstDir = "tests/runner-extras/precompiled-dep-cache-null-first";
    private const string SecondDir = "tests/runner-extras/xmlport-precompiled-dep-metadata";

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
            + "gone, so #4783's proving suites run only inside the combined tests/runner-extras "
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

    [Fact]
    public void WorkflowRunsTheTwoSuites_AsTwoSeparateBundleArguments()
    {
        var args = ArgumentList(MultiBundleStep());

        Assert.Contains(FirstDir, args, StringComparison.Ordinal);
        Assert.Contains(SecondDir, args, StringComparison.Ordinal);

        // A shared parent root would build ONE bundle out of both suites, which is exactly the
        // blind configuration: green whether the defect is present or not.
        Assert.DoesNotContain("tests/runner-extras ", args, StringComparison.Ordinal);
        Assert.DoesNotContain("tests/runner-extras\n", args, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowPassesTheFirstBundleBeforeTheSecond()
    {
        var args = ArgumentList(MultiBundleStep());

        // Positional: -first must ask about table 61600 BEFORE the second bundle's dependency .app
        // is registered. Reversed, all four tests pass on the unfixed tree, so the order IS the test.
        Assert.True(
            args.IndexOf(FirstDir, StringComparison.Ordinal)
                < args.IndexOf(SecondDir, StringComparison.Ordinal),
            $"{FirstDir} must be passed BEFORE {SecondDir}: reversed, the run is green on the "
            + "unfixed tree and this coverage asserts nothing.");
    }

    // ── 2. NEITHER BUNDLE DEPENDS ON THE OTHER ───────────────────────────────────────────────
    //
    // The second bundle declares a dependency on its PRECOMPILED fixture package, which is the
    // point; it must not declare one on -first, and -first must declare none at all, or -first's
    // lookup would run with the fixture package already registered.

    [Theory]
    [InlineData(FirstDir)]
    [InlineData(SecondDir)]
    public void NeitherSuiteDeclaresADependencyOnTheOther(string suiteDir)
    {
        var manifestPath = Path.Combine(RepoRoot, suiteDir, "app.json");
        Assert.True(File.Exists(manifestPath), $"{suiteDir}/app.json is missing");

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var deps = doc.RootElement.GetProperty("dependencies");
        var names = deps.EnumerateArray().Select(d => d.GetProperty("name").GetString()).ToList();

        if (suiteDir == FirstDir)
            Assert.True(names.Count == 0,
                $"{FirstDir}/app.json declares a dependency ({string.Join(", ", names)}). The "
                + "first bundle must register nothing that could declare table 61600.");
        else
            Assert.Equal(["XPD Precompiled XmlPort Dep"], names);
    }

    // ── 3. THE GATE CANNOT SILENTLY SKIP ─────────────────────────────────────────────────────

    [Fact]
    public void TheStepsHashFilesGlob_ResolvesAgainstRealFiles()
    {
        var step = MultiBundleStep();
        const string glob = "tests/runner-extras/precompiled-dep-cache-null-first/**/*.al";

        Assert.Contains(glob, step, StringComparison.Ordinal);

        // A glob matching nothing makes hashFiles() return '', which SKIPS the step — and a
        // skipped step reports SUCCESS. So the guard on this step is only as good as the glob
        // resolving, which nothing else checks.
        var matches = Directory.GetFiles(
            Path.Combine(RepoRoot, FirstDir), "*.al", SearchOption.AllDirectories);
        Assert.True(matches.Length > 0,
            $"the step's hashFiles({glob}) resolves to no files, so the step would be SKIPPED — "
            + "and a skipped step reports success, retiring this coverage in silence.");
    }

    [Fact]
    public void TheStepAssertsEveryTestTheSuitesDeclare_ByName()
    {
        var step = MultiBundleStep();

        // Read the [Test] procedure names out of the AL rather than hardcoding them, so ADDING a
        // test without adding its PASS check reds this test instead of going unnoticed.
        foreach (var dir in new[] { FirstDir, SecondDir })
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot, dir), "*.al"))
        {
            var text = File.ReadAllText(file);
            var codeunitId = System.Text.RegularExpressions.Regex.Match(text, @"codeunit\s+(\d+)");
            if (!codeunitId.Success) continue;

            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(
                         text, @"\[Test\]\s*\r?\n\s*procedure\s+(\w+)"))
            {
                var expected = $"Codeunit{codeunitId.Groups[1].Value}.{m.Groups[1].Value}";
                Assert.True(step.Contains(expected, StringComparison.Ordinal),
                    $"the workflow step does not assert 'PASS {expected}'. Without a per-test check "
                    + "the step is green whenever the run is green, INCLUDING a run that compiled "
                    + "nothing and executed 0 tests (the #4080 shape).");
            }
        }
    }

    [Fact]
    public void TheStepFails_RatherThanOnlyReporting()
    {
        var step = MultiBundleStep();

        Assert.Contains("::error::", step, StringComparison.Ordinal);
        Assert.Contains("exit \"$missing\"", step, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error: true", step, StringComparison.Ordinal);
    }
}
