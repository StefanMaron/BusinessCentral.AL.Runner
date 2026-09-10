// MetadataEquivalenceBundleGateTests — every class that reads a ground-truth bundle must get
// its "there is no bundle" verdict from MetadataEquivalenceBundleGate, and that verdict must
// FAIL on CI rather than skip.
//
// Issue #3789, folded into #3782 steps 3/4 because it lands in the same files.
//
// WHY THIS IS A TEST AND NOT A CONVENTION
//   The classes that read a bundle exist to stop the harness reporting green over an unrun
//   measurement — MetadataEquivalencePageOracleTests pins that BC's MetaPageDefinition accepts
//   the emitter's document and reads NOTHING from it, which is how step 1 got 235 pages
//   compared and 0 differences with every test green. A skip reads green in the summary line.
//   So a bundle-less CI leg silences exactly the discrimination that stops the harness
//   measuring nothing, and the class without the guard was the anti-green-over-nothing class.
//
//   That is not a one-off. #3782 has five more object kinds to go, each adding a class that
//   reads a bundle, and the cheap thing to write is a bare Skip.If. This test is what makes
//   the next one fail instead.
//
// NOT CURRENTLY EXPLOITABLE, and worth recording so the fix is not overstated: the real gate
// runs through RunAll() and does fail loudly, so the oracle classes skipping would not by
// itself produce a false green today. It is still the wrong shape, for the reason
// .claude/rules/guards-need-a-third-state.md gives — a guard safe only by accident of a
// neighbour is still on the list.

using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class MetadataEquivalenceBundleGateTests
{
    private static string TestsDir()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Path.Combine(dir, "AlRunner.Tests");
    }

    /// <summary>
    /// Every .cs in AlRunner.Tests that mentions a ground-truth bundle at all, except the two
    /// that legitimately name these symbols: the harness, which DEFINES both, and this file,
    /// which names them in its own assertion messages. Excluded by filename rather than by a
    /// cleverer match, so the exclusion is visible and cannot silently widen.
    /// </summary>
    private static readonly string[] NotReaders =
    {
        "MetadataEquivalenceHarness.cs",
        "MetadataEquivalenceBundleGateTests.cs",
    };

    private static IEnumerable<(string Path, string Text)> BundleReaders()
    {
        foreach (var f in Directory.EnumerateFiles(TestsDir(), "*.cs", SearchOption.TopDirectoryOnly)
                     .Where(p => !NotReaders.Contains(Path.GetFileName(p), StringComparer.Ordinal))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(f);
            if (text.Contains("MetadataEquivalencePaths.GroundTruthDirForThisBuild", StringComparison.Ordinal)
                || text.Contains("MetadataEquivalenceHarness.LoadBundles", StringComparison.Ordinal)
                || text.Contains("MetadataEquivalenceBundleGate.RequireBundles", StringComparison.Ordinal))
                yield return (f, text);
        }
    }

    [Fact]
    public void Only_the_shared_gate_loads_bundles_directly()
    {
        // LoadBundles has no CI branch, so a caller that reaches it directly has written, or is
        // one edit away from writing, its own skip. RequireBundles is the only legitimate
        // caller — and it lives in MetadataEquivalenceHarness.cs beside it.
        var offenders = BundleReaders()
            .Where(f => f.Text.Contains("MetadataEquivalenceHarness.LoadBundles", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f.Path))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "these classes call MetadataEquivalenceHarness.LoadBundles directly instead of " +
            "MetadataEquivalenceBundleGate.RequireBundles(), so their 'no bundle' verdict is " +
            "their own — and a bundle-less CI leg would SKIP, which reads green in the summary " +
            "while measuring nothing (#3789): " + string.Join(", ", offenders));

        // Non-vacuity: "zero offenders among zero files scanned" is how this passes having
        // checked nothing, which is the same shape as the defect.
        Assert.True(BundleReaders().Count() >= 4,
            $"only {BundleReaders().Count()} file(s) matched as bundle readers, so this test is " +
            "not scanning what it claims to. The detection strings are probably stale.");
    }

    // SkippableFact, not Fact: BundleReaders() can raise SkipException, and a SkipException out
    // of a plain [Fact] is reported Failed rather than Skipped (TestArtifactsGateTests).
    [SkippableFact]
    public void No_bundle_reader_writes_its_own_empty_bundle_skip()
    {
        // The second half, because a class could hold a bundles list from RequireBundles and
        // still add a defensive Skip.If on its count — which would be dead code that reads like
        // the guard being present.
        var bare = new Regex(@"Skip\.If\w*\(\s*bundles\.Count\s*==\s*0", RegexOptions.Compiled);
        var offenders = BundleReaders()
            .Where(f => bare.IsMatch(f.Text))
            .Select(f => Path.GetFileName(f.Path))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "these classes skip on an empty bundle list of their own accord. " +
            "MetadataEquivalenceBundleGate.RequireBundles() never returns an empty list — it " +
            "skips locally and THROWS on CI — so such a guard is either dead code or a second, " +
            "weaker verdict (#3789): " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_gate_fails_rather_than_skips_when_CI_has_no_bundle()
    {
        // The behaviour itself, not just its call sites. Asserted by pointing the gate at an
        // empty directory through AL_RUNNER_METADATA_GROUND_TRUTH, which is the same override
        // the skip message tells a developer about — so this exercises the real code path
        // rather than a reimplementation of it.
        // Owned: this directory IS created, so a killed test host would leak it (#2743).
        // TestScratch.FlatDir creates it and records an owner, which the sweep deletes.
        var empty = TestScratch.FlatDir("al-runner-bundle-gate");

        var previousRoot = Environment.GetEnvironmentVariable("AL_RUNNER_METADATA_GROUND_TRUTH");
        var previousCi = Environment.GetEnvironmentVariable("CI");
        var previousActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        try
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_METADATA_GROUND_TRUTH", empty);

            Environment.SetEnvironmentVariable("CI", "true");
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");
            var onCi = Assert.ThrowsAny<Exception>(() => MetadataEquivalenceBundleGate.RequireBundles());
            Assert.IsType<MetadataGroundTruthMissingOnCiException>(onCi);
            Assert.Contains("workflow regression", onCi.Message, StringComparison.Ordinal);
            // The message must still name the remedy, or a real CI failure is unactionable.
            Assert.Contains("gen-metadata-ground-truth.sh", onCi.Message, StringComparison.Ordinal);

            // ...and the OTHER direction, which is what keeps this from being a demand that a
            // developer box provision a bundle before any of these tests can run.
            Environment.SetEnvironmentVariable("CI", null);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);
            var offCi = Assert.ThrowsAny<Exception>(() => MetadataEquivalenceBundleGate.RequireBundles());
            Assert.IsType<SkipException>(offCi);
            Assert.Contains("gen-metadata-ground-truth.sh", offCi.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_METADATA_GROUND_TRUTH", previousRoot);
            Environment.SetEnvironmentVariable("CI", previousCi);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", previousActions);
            try { Directory.Delete(empty, recursive: true); } catch { /* best effort */ }
        }
    }
}
