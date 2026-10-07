// EngineVariantRuntimeTests — a variant is only runnable where its .NET runtime is installed (#5382).
//
// BC 29's engine targets net10.0; the tool itself and BC 27/28 run on net8.0. A variant is entered by
// re-exec under the runtime its own runtimeconfig.json names, so a machine with only the tool's runtime
// cannot run BC 29. Two things follow and both are pinned here:
//   * the DEFAULT version is never one the machine cannot run — otherwise installing the release that
//     first ships a BC 29 variant breaks every .NET-8-only user who passes no --bc-version;
//   * an EXPLICIT request for such a version says what is missing, instead of leaving the user the
//     host's "framework not found" from a child process.
// Each test is written so the obvious wrong implementation passes the others and fails it: ignoring the
// runtime entirely passes the "available" cases, treating "unknown" as "missing" fails the unknown ones.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class EngineVariantRuntimeTests
{
    private static readonly Version Bc285 = new(28, 5, 54151, 55132);
    private static readonly Version Bc290 = new(29, 0, 54011, 55816);

    private static EngineVariants.Variant V(Version v, int? runtime) => new(v, $"/x/{v}", runtime);

    // ---- RuntimeAvailable ---------------------------------------------------------------

    [Fact]
    public void Variant_NeedingARuntime_IsAvailableOnlyWhereItIsInstalled()
    {
        var v = V(Bc290, 10);

        Assert.True(v.RuntimeAvailable(new[] { 8, 10 }));
        Assert.False(v.RuntimeAvailable(new[] { 8 }));
        Assert.False(v.RuntimeAvailable(Array.Empty<int>()));
    }

    [Fact]
    public void UnknownOnEitherSide_IsAvailable_NotMissing()
    {
        // Refusing on a fact nobody measured would stop a runnable variant.
        Assert.True(V(Bc290, null).RuntimeAvailable(new[] { 8 }));
        Assert.True(V(Bc290, 10).RuntimeAvailable(null));
    }

    // ---- reading the runtime out of the variant's own runtimeconfig ---------------------

    private static string VariantDir(string? runtimeConfig, string version)
    {
        var dir = Path.Combine(TestScratch.Dir("al-runner-variant-runtime"), "variants", version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "al-runner.dll"), "x");
        if (runtimeConfig != null) File.WriteAllText(Path.Combine(dir, "al-runner.runtimeconfig.json"), runtimeConfig);
        return dir;
    }

    [Theory]
    [InlineData("""{"runtimeOptions":{"tfm":"net10.0"}}""", 10)]
    [InlineData("""{"runtimeOptions":{"tfm":"net8.0"}}""", 8)]
    [InlineData("""{"runtimeOptions":{"tfm":"net9.0","framework":{"name":"Microsoft.NETCore.App"}}}""", 9)]
    [InlineData("""{"runtimeOptions":{"tfm":"netcoreapp3.1"}}""", null)]
    [InlineData("""{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App"}}}""", null)]
    [InlineData("""{ not json""", null)]
    public void ReadRuntimeMajor_TakesTheTfmsMajor_AndNothingElseIsAGuess(string config, int? expected)
    {
        var dir = VariantDir(config, "29.0.54011.55816");

        Assert.Equal(expected, EngineVariants.ReadRuntimeMajor(dir));
    }

    [Fact]
    public void ReadRuntimeMajor_IsNullWhenThereIsNoRuntimeconfig()
    {
        Assert.Null(EngineVariants.ReadRuntimeMajor(VariantDir(null, "29.0.54011.55816")));
    }

    [Fact]
    public void Discover_CarriesEachVariantsOwnRuntimeMajor()
    {
        var root = TestScratch.Dir("al-runner-variant-discover");
        foreach (var (ver, tfm) in new[] { ("28.5.54151.55132", "net8.0"), ("29.0.54011.55816", "net10.0") })
        {
            var d = Path.Combine(root, "variants", ver);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "al-runner.dll"), "x");
            File.WriteAllText(Path.Combine(d, "al-runner.runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"" + tfm + "\"}}");
        }

        var found = EngineVariants.Discover(root).ToDictionary(v => v.BuildVersion.Major, v => v.RuntimeMajor);

        Assert.Equal(8, found[28]);
        Assert.Equal(10, found[29]);
    }

    // ---- the default ---------------------------------------------------------------------

    [Fact]
    public void Default_SkipsAVariantWhoseRuntimeIsNotInstalled_AndSaysWhy()
    {
        var variants = new[] { V(Bc285, 8), V(Bc290, 10) };

        var choice = EngineVariants.ChooseDefault(variants, Array.Empty<string>(), installedRuntimeMajors: new[] { 8 });

        Assert.Equal("28.5", choice.Version);
        Assert.Equal(new[] { "29.0" }, choice.SkippedNoRuntime);
        Assert.Contains("29.0", choice.NoRuntimeLine(variants), StringComparison.Ordinal);
    }

    [Fact]
    public void Default_IsTheNewestWhenItsRuntimeIsInstalled_AndWhenInstalledRuntimesAreUnknown()
    {
        var variants = new[] { V(Bc285, 8), V(Bc290, 10) };

        Assert.Equal("29.0", EngineVariants.ChooseDefault(variants, Array.Empty<string>(),
            installedRuntimeMajors: new[] { 8, 10 }).Version);
        Assert.Equal("29.0", EngineVariants.ChooseDefault(variants, Array.Empty<string>(),
            installedRuntimeMajors: null).Version);
        Assert.Null(EngineVariants.ChooseDefault(variants, Array.Empty<string>(),
            installedRuntimeMajors: new[] { 8, 10 }).NoRuntimeLine(variants));
    }

    [Fact]
    public void Default_SkipsACachedBuildItsRuntimeCannotRun_ForTheNextOneDown()
    {
        var variants = new[] { V(Bc285, 8), V(Bc290, 10) };

        var choice = EngineVariants.ChooseDefault(variants,
            new[] { "29.0.54011.55816", "28.5.54151.55132" }, installedRuntimeMajors: new[] { 8 });

        Assert.Equal("28.5.54151.55132", choice.Version);
    }

    // ---- an explicit request -------------------------------------------------------------

    [Fact]
    public void Resolve_RefusesAVariantItWouldHaveToEnterWhenItsRuntimeIsMissing()
    {
        var variants = new[] { V(Bc285, 8), V(Bc290, 10) };

        var r = EngineVariants.Resolve(variants, Bc290, runningBuild: Bc285, installedRuntimeMajors: new[] { 8 });

        Assert.Equal(EngineVariants.ResolutionKind.NoneSupported, r.Kind);
        Assert.Contains(".NET 10", r.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("installed: .NET 8", r.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("--bc-version", r.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_EntersTheVariantWhenItsRuntimeIsThere_AndNeverChecksTheOneAlreadyRunning()
    {
        var variants = new[] { V(Bc285, 8), V(Bc290, 10) };

        Assert.Equal(EngineVariants.ResolutionKind.SwapRequired,
            EngineVariants.Resolve(variants, Bc290, Bc285, new[] { 8, 10 }).Kind);
        // The running variant is on its runtime by definition, whatever the installed listing says.
        Assert.Equal(EngineVariants.ResolutionKind.RunningEngineMatches,
            EngineVariants.Resolve(variants, Bc290, Bc290, new[] { 8 }).Kind);
        // Unknown installed runtimes do not refuse.
        Assert.Equal(EngineVariants.ResolutionKind.SwapRequired,
            EngineVariants.Resolve(variants, Bc290, Bc285, null).Kind);
    }

    // ---- listing the installed runtimes --------------------------------------------------

    [Fact]
    public void MajorsUnder_ListsTheSharedFrameworkDirectoriesByMajor()
    {
        var root = TestScratch.Dir("al-runner-dotnet-root");
        foreach (var v in new[] { "8.0.30", "10.0.11", "10.0.2", "9.0.15" })
            Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", v));
        Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "not-a-version"));

        Assert.Equal(new[] { 8, 9, 10 }, InstalledDotNetRuntimes.MajorsUnder(root));
    }

    [Fact]
    public void MajorsUnder_IsNullNotEmpty_WhenThereIsNoSharedFramework()
    {
        // Unknown, so nothing is refused on it.
        Assert.Null(InstalledDotNetRuntimes.MajorsUnder(TestScratch.Dir("al-runner-dotnet-root-empty")));
    }
}
