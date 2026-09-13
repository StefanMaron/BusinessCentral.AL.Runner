// EngineVariantsTests — pins the per-BC-minor engine variant discovery/selection
// mechanism for #2024 item 3 / #2027: a packed tool now ships one thin engine binary
// per .github/bc-versions.txt entry under variants/<full-build-version>/, and the runner
// picks the right one at startup instead of running a single, fixed-minor engine against
// whatever BC artifact happens to be selected (the root cause of #2020).
//
// Every assertion here is a concrete value, not a bare non-throw — a stub implementation
// that always returned the first variant, or null, or an empty list, would fail every
// negative case and most of the positives (see .claude/rules/tdd.md).
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class EngineVariantsTests
{
    private static string NewTempDir(string label)
    {
        var dir = TestScratch.FlatDir($"engine-variants-tests-{label}-");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void MakeVariant(string baseDir, string version)
    {
        var dir = Path.Combine(baseDir, EngineVariants.VariantsDirName, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, EngineVariants.EntryAssemblyFileName), "fake");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Discover
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Negative: no variants/ directory at all (a plain dev/test `dotnet build`
    /// checkout) — discovery must come back empty, not throw, so every downstream caller
    /// degrades to today's single-build behaviour.</summary>
    [Fact]
    public void Discover_NoVariantsDirectory_ReturnsEmpty()
    {
        var baseDir = NewTempDir("no-dir");
        try
        {
            Assert.Empty(EngineVariants.Discover(baseDir));
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    /// <summary>Positive: well-formed variant directories are all discovered, with their
    /// build version parsed from the directory name and their own directory path.</summary>
    [Fact]
    public void Discover_WellFormedVariants_ReturnsAllWithParsedVersionsAndPaths()
    {
        var baseDir = NewTempDir("well-formed");
        try
        {
            MakeVariant(baseDir, "28.1.49838.53910");
            MakeVariant(baseDir, "28.3.52162.53954");

            var found = EngineVariants.Discover(baseDir);

            Assert.Equal(2, found.Count);
            Assert.Contains(found, v => v.BuildVersion == new Version(28, 1, 49838, 53910)
                && v.Dir == Path.Combine(baseDir, "variants", "28.1.49838.53910"));
            Assert.Contains(found, v => v.BuildVersion == new Version(28, 3, 52162, 53954));
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    /// <summary>Negative: a directory whose name isn't a parseable version, and a
    /// version-named directory missing its own al-runner.dll, are both skipped rather
    /// than corrupting discovery for the well-formed siblings.</summary>
    [Fact]
    public void Discover_MalformedEntries_AreSkippedNotThrown()
    {
        var baseDir = NewTempDir("malformed");
        try
        {
            MakeVariant(baseDir, "28.1.49838.53910"); // well-formed
            Directory.CreateDirectory(Path.Combine(baseDir, "variants", "not-a-version"));
            Directory.CreateDirectory(Path.Combine(baseDir, "variants", "28.9.0.0")); // no al-runner.dll inside

            var found = EngineVariants.Discover(baseDir);

            Assert.Single(found);
            Assert.Equal(new Version(28, 1, 49838, 53910), found[0].BuildVersion);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SelectBestMatch
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Positive: an EXACT 4-part build match wins over a same-minor,
    /// different-build variant that's also present — proves the tiering order, not just
    /// "a match was found".</summary>
    [Fact]
    public void SelectBestMatch_ExactBuildPresent_PrefersExactOverSameMinorDifferentBuild()
    {
        var variants = new[]
        {
            new EngineVariants.Variant(new Version(28, 1, 11111, 1), "/v/28.1.11111.1"),
            new EngineVariants.Variant(new Version(28, 1, 22222, 2), "/v/28.1.22222.2"),
        };

        var match = EngineVariants.SelectBestMatch(variants, new Version(28, 1, 22222, 2));

        Assert.NotNull(match);
        Assert.Equal(new Version(28, 1, 22222, 2), match!.Value.Variant.BuildVersion);
        Assert.False(match.Value.Degraded);
    }

    /// <summary>Positive: no exact build, but a same-major.minor variant exists — matched,
    /// flagged Degraded (the known CodeAnalysis per-build strong-name skew risk).</summary>
    [Fact]
    public void SelectBestMatch_NoExactBuild_FallsBackToSameMinorAsDegraded()
    {
        var variants = new[]
        {
            new EngineVariants.Variant(new Version(28, 1, 11111, 1), "/v/28.1.11111.1"),
        };

        var match = EngineVariants.SelectBestMatch(variants, new Version(28, 1, 99999, 9));

        Assert.NotNull(match);
        Assert.Equal(new Version(28, 1, 11111, 1), match!.Value.Variant.BuildVersion);
        Assert.True(match.Value.Degraded);
    }

    /// <summary>Negative: no variant shares even the MAJOR — must return null, not a
    /// nearby-major variant. #2020's root cause was exactly this silent fallback; the
    /// caller (Program.cs) turns a null here into a loud, version-naming failure.</summary>
    [Fact]
    public void SelectBestMatch_NoMajorMatch_ReturnsNull()
    {
        var variants = new[]
        {
            new EngineVariants.Variant(new Version(28, 1, 11111, 1), "/v/28.1.11111.1"),
        };

        var match = EngineVariants.SelectBestMatch(variants, new Version(27, 5, 1, 1));

        Assert.Null(match);
    }

    /// <summary>Negative: an empty variant list never matches anything.</summary>
    [Fact]
    public void SelectBestMatch_NoVariants_ReturnsNull()
    {
        var match = EngineVariants.SelectBestMatch(Array.Empty<EngineVariants.Variant>(), new Version(28, 1, 1, 1));
        Assert.Null(match);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DescribeAvailable
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DescribeAvailable_ListsEachVariantVersion()
    {
        var variants = new[]
        {
            new EngineVariants.Variant(new Version(27, 5, 1, 1), "/v/27.5"),
            new EngineVariants.Variant(new Version(28, 1, 1, 1), "/v/28.1"),
        };

        var desc = EngineVariants.DescribeAvailable(variants);

        Assert.Equal("27.5.1.1, 28.1.1.1", desc);
    }

    [Fact]
    public void DescribeAvailable_NoVariants_SaysNone()
    {
        Assert.Equal("(none)", EngineVariants.DescribeAvailable(Array.Empty<EngineVariants.Variant>()));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Resolve (#2190: the one decision shared by the bundle run and --precompile)
    // ─────────────────────────────────────────────────────────────────────────

    private static EngineVariants.Variant V(string version) => new(Version.Parse(version), "/variants/" + version);

    [Fact]
    public void Resolve_NoVariants_ProceedsInPlaceWithNoSwapAndNoWarning()
    {
        var r = EngineVariants.Resolve(Array.Empty<EngineVariants.Variant>(), new Version(28, 4, 1, 1), new Version(28, 1, 0, 0));
        Assert.Equal(EngineVariants.ResolutionKind.NoVariantsShipped, r.Kind);
        Assert.Null(r.SwapDir);
        Assert.Null(r.FailureMessage);
        Assert.Null(r.DegradedWarning);
    }

    [Fact]
    public void Resolve_NoVariantServesSelected_RefusesNamingSelectedAndAvailable()
    {
        var r = EngineVariants.Resolve(new[] { V("27.5.1.1") }, new Version(28, 4, 2, 2), new Version(27, 5, 1, 1));
        Assert.Equal(EngineVariants.ResolutionKind.NoneSupported, r.Kind);
        Assert.Null(r.SwapDir);
        Assert.Equal(
            "BC version selection failed: no shipped engine variant supports BC 28.4.2.2 (major 28). " +
            "Available variants: 27.5.1.1. Select a cached BC version this install ships an engine for " +
            "(--bc-version), or update al-runner.",
            r.FailureMessage);
    }

    [Fact]
    public void Resolve_ExactVariantIsRunningEngine_NoSwapNoWarning()
    {
        var r = EngineVariants.Resolve(new[] { V("28.4.2.2"), V("27.5.1.1") }, new Version(28, 4, 2, 2), new Version(28, 4, 2, 2));
        Assert.Equal(EngineVariants.ResolutionKind.RunningEngineMatches, r.Kind);
        Assert.Null(r.SwapDir);
        Assert.False(r.Degraded);
        Assert.Null(r.DegradedWarning);
    }

    [Fact]
    public void Resolve_ExactVariantIsNotRunningEngine_SwapsWithoutWarning()
    {
        var r = EngineVariants.Resolve(new[] { V("28.4.2.2"), V("27.5.1.1") }, new Version(27, 5, 1, 1), new Version(28, 4, 2, 2));
        Assert.Equal(EngineVariants.ResolutionKind.SwapRequired, r.Kind);
        Assert.Equal("/variants/27.5.1.1", r.SwapDir);
        Assert.Null(r.DegradedWarning);
    }

    [Fact]
    public void Resolve_SameMinorDifferentBuild_SwapsAndWarnsNamingBothBuilds()
    {
        var r = EngineVariants.Resolve(new[] { V("28.4.0.0") }, new Version(28, 4, 2, 2), new Version(28, 4, 2, 2));
        Assert.Equal(EngineVariants.ResolutionKind.SwapRequired, r.Kind);
        Assert.Equal("/variants/28.4.0.0", r.SwapDir);
        Assert.True(r.Degraded);
        Assert.StartsWith(
            "[bc] warning: the shipped 28.4 engine variant was built against 28.4.0.0, not the selected 28.4.2.2",
            r.DegradedWarning);
    }
}
