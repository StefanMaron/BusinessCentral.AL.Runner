// NestedBundleManifestDiscoveryTests — issue #2996.
//
// RUNNER-MECHANISM test. Two functions decide what a multi-app bundle is, and they used to
// disagree about how deep an app may sit:
//
//   • ProgramSupport.EnumerateSuites recurses to ARBITRARY depth, stopping on each branch at
//     the first directory that LooksLikeSuite. It decides which apps become AppGroups and get
//     compiled.
//   • ProgramSupport.CollectBundleManifests scanned only the bundle root's DIRECT children.
//     It decides whose app.json contributes to the bundle's dependency closure — the
//     `application` / `platform` / Microsoft roots that give every module in the bundle the
//     platform symbol set.
//
// For a tree one level deeper than the scan — the al-language corpus root, whose three apps
// live at <root>/tests/<app>/app.json — the first function found three apps to compile and
// the second found no manifests at all. Program.cs then printed
// "WARN: no app.json under <root> — skipping dep loading" and compiled all three apps with
// NO Microsoft closure, so every reference to a platform object failed AL0185:
// `Table 'Object Metadata' is missing`, `Table 'AllObj' is missing`, `Codeunit 'Temp Blob'
// is missing`, `Table 'Customer' is missing`. 60 objects were dropped from the Cloud app and
// the OnPrem app emitted zero. Measured on BC 28.1 against corpus master 861a566:
//
//     al-runner <corpus>          -> compile-fail, 0 tests   (apps at depth 2)
//     al-runner <corpus>/tests    -> 2698 tests, 2696 pass    (apps at depth 1)
//
// Same apps, same runner, same caches; only the nesting differs. The reported symptom looked
// like a Target=OnPrem / Scope=OnPrem resolution problem, and is not — nothing in the failure
// depends on the manifest's `target`.
//
// The fix makes CollectBundleManifests derive its set FROM EnumerateSuites, so the two can no
// longer drift: the manifests are exactly the app.json files of the suites that will be
// compiled. That invariant is what NestedManifests_AreExactlyTheSuiteManifests pins — a test
// asserting only "depth 2 works" would pass again if someone hard-coded a second level.
using Xunit;

namespace AlRunner.Tests;

public sealed class NestedBundleManifestDiscoveryTests : IDisposable
{
    private readonly string _root;

    public NestedBundleManifestDiscoveryTests()
    {
        _root = TestScratch.Dir("al-runner-nested-bundle-manifests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// Writes a minimal app manifest at <paramref name="relativeDir"/> and one .al file next to
    /// it, so the directory also satisfies EnumerateSuites' flat-bundle shape. No "application"
    /// property — see .claude/rules/no-base-app-in-csharp-tests.md; nothing here compiles AL.
    /// </summary>
    private string WriteApp(string relativeDir, string name, string? platform = null)
    {
        var dir = Path.Combine(_root, relativeDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        var platformProp = platform == null ? "" : $@", ""platform"": ""{platform}""";
        File.WriteAllText(
            Path.Combine(dir, "app.json"),
            $@"{{ ""id"": ""{DeterministicId(name)}"", ""name"": ""{name}"", ""publisher"": ""P"", ""version"": ""1.0.0.0""{platformProp} }}");
        File.WriteAllText(Path.Combine(dir, "Dummy.al"), "codeunit 50000 Dummy { }");
        return Path.Combine(Path.GetFullPath(dir), "app.json");
    }

    private static string DeterministicId(string name)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return new Guid(bytes).ToString();
    }

    // ---------------------------------------------------------------------------------
    // The defect: a manifest two levels below the bundle root.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ManifestsTwoLevelsDown_AreCollected()
    {
        // The al-language corpus shape: <root>/tests/<app>/app.json. <root>/tests holds no
        // app.json, no src/ and no test/, so it is a plain container the walk must pass through.
        var cloud = WriteApp("tests/al-language", "AL Language Coverage Tests");
        var onprem = WriteApp("tests/al-language-onprem", "AL Language Coverage Tests (OnPrem)");

        var manifests = ProgramSupport.CollectBundleManifests(
            bucketRoot: null, bundleAbs: _root);

        // RED before the fix: Directory.EnumerateDirectories(_root) sees only "tests", which has
        // no app.json of its own, so this list was EMPTY and the bundle resolved no dependencies.
        Assert.Equal(2, manifests.Count);
        Assert.Contains(cloud, manifests);
        Assert.Contains(onprem, manifests);
    }

    [Fact]
    public void ManifestsThreeLevelsDown_AreCollected()
    {
        // Nothing about the fix may be specific to "one more level" — the walk is unbounded,
        // exactly as EnumerateSuites' is.
        var deep = WriteApp("a/b/c/app-deep", "Deep App");

        var manifests = ProgramSupport.CollectBundleManifests(
            bucketRoot: null, bundleAbs: _root);

        Assert.Equal(new[] { deep }, manifests);
    }

    // ---------------------------------------------------------------------------------
    // The invariant the fix installs: manifests == the suites' own manifests.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void NestedManifests_AreExactlyTheSuiteManifests()
    {
        // Mixed depths in one tree, which is the case a hard-coded depth limit gets wrong.
        WriteApp("shallow-app", "Shallow App");
        WriteApp("tests/nested-app", "Nested App");
        WriteApp("a/b/deep-app", "Deep App");

        var expected = ProgramSupport.EnumerateSuites(_root)
            .Select(s => Path.Combine(s, "app.json"))
            .Where(File.Exists)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var manifests = ProgramSupport.CollectBundleManifests(
            bucketRoot: null, bundleAbs: _root);

        Assert.Equal(3, expected.Count);           // the fixture really does have three apps
        Assert.Equal(expected, manifests);          // and the closure covers exactly them
    }

    // ---------------------------------------------------------------------------------
    // Negative directions — the fix must not turn into "every app.json in the tree".
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ManifestInsideASuite_IsNotCollected()
    {
        // A vendored/extracted package under an app that IS a suite. EnumerateSuites stops
        // descending at the first suite on a branch, so the inner manifest is part of that
        // suite's own directory, never a bundle member. Recursing over every app.json in the
        // tree instead of over the suites would wrongly pull this one into the closure.
        var outer = WriteApp("app-a", "App A");
        WriteApp("app-a/.alpackages/Vendored", "Vendored Package");

        var manifests = ProgramSupport.CollectBundleManifests(
            bucketRoot: null, bundleAbs: _root);

        Assert.Equal(new[] { outer }, manifests);
    }

    [Fact]
    public void BucketRootWithItsOwnManifest_StillWinsAlone()
    {
        // A bundle root that IS one app speaks for the whole bucket: exactly its own manifest,
        // and the children below it are its sub-directories, not sibling apps.
        var rootManifest = WriteApp(".", "Root App");
        WriteApp("tests/child", "Child App");

        var manifests = ProgramSupport.CollectBundleManifests(
            bucketRoot: _root, bundleAbs: _root);

        Assert.Equal(new[] { rootManifest }, manifests);
    }

    [Fact]
    public void MissingBundleDirectory_YieldsNoManifests()
    {
        var absent = Path.Combine(_root, "does-not-exist");

        Assert.Empty(ProgramSupport.CollectBundleManifests(bucketRoot: null, bundleAbs: absent));
    }

    [Fact]
    public void NestedSuiteDeclaringANewerBc_IsDroppedFromTheClosure()
    {
        // BcFloorGate has to keep working through the deeper walk: one suite's unreachable
        // Microsoft floor must not join a bundle-wide union and abort every sibling. 999.0 is
        // above any BC that could be selected, so this gates deterministically.
        var ok = WriteApp("tests/app-ok", "App Ok");
        WriteApp("tests/app-future", "App Future", platform: "999.0.0.0");

        AlRunner.BcFloorGate.ResetForTests();
        var manifests = ProgramSupport.CollectBundleManifests(
            bucketRoot: null, bundleAbs: _root);

        Assert.Equal(new[] { ok }, manifests);
    }
}
