// EnumExtensionSidecarRoundTripTests — issue #2709.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it proves that
// AlEnumMetadataRegistry's compiled-deps/AL-output sidecar round-trip (SnapshotRaw /
// SaveSidecar / LoadSidecar) keeps a base enum registration and an enumextension's own
// values SEPARATE across a save+reload cycle, regardless of which one registers first
// in the process. The BC-side claim ("an enumextension of a Base App enum still casts
// to its interface and keeps the base's own values") is Base App behaviour, already
// covered by the corpus per docs/rules/bc-behavior-tests-go-upstream.md — see the issue
// body's acceptance-criteria note. This test exists so a regression in OUR OWN sidecar
// mechanism fails loudly here, in milliseconds, without needing the BC engine or Base
// App loaded at all.
//
// Root cause (see AlEnumMetadataRegistry.SnapshotRaw's doc comment): the OLD sidecar
// persisted TryGet's MERGED base+extension view, flattened under the base id, with no
// way to say "this came from an extension". Replaying that through plain Register()
// then went wrong in one of two directions depending on registration order:
//   - extension-sidecar replayed AFTER the real base registers -> clobbers the base
//     (multi-bundle: Base App enum lost, "Unable to cast enum '' value '...'").
//   - extension-sidecar replayed BEFORE the real base registers -> the later base
//     registration overwrites the replayed (extension-only) entry, so the extension's
//     value is gone (single-bundle: "Unable to cast enum '<base options only>' value
//     '<extension ordinal>'").
//
// RED (pre-fix, i.e. AlEnumMetadataRegistry.SaveSidecar/LoadSidecar without the
// `extends` marker and without routing extension-only entries through
// RegisterExtension): MultiBundleOrder_ExtensionReplayAfterBase_DoesNotClobberBase and
// SingleBundleOrder_ExtensionReplayBeforeBase_KeepsExtensionValue both fail.
// GREEN (post-fix): both pass, and the merged view always carries base name + all base
// values + all extension values.
using Xunit;

namespace AlRunner.Tests;

public sealed class EnumExtensionSidecarRoundTripTests : IDisposable
{
    private readonly string _root;

    public EnumExtensionSidecarRoundTripTests()
    {
        _root = TestScratch.Dir("al-runner-enum-ext-sidecar-tests");
        Directory.CreateDirectory(_root);
        AlEnumMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlEnumMetadataRegistry.Clear();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // Mirrors the issue's real numbers: Base App enum 7011 "Price Calculation Handler"
    // (0 "Not Defined", 7002 "Business Central (Version 16.0)", 7003 "Business Central
    // (Version 15.0)") extended by enumextension 130515 "Price Calc. Handler - Test"
    // (130514 "Test"). The exact ids don't matter to the mechanism; keeping them makes
    // the test legible against the issue.
    private const int BaseId = 7011;
    private const string BaseName = "Price Calculation Handler";
    private static readonly string[] BaseOptions = { "Not Defined", "Business Central (Version 16.0)", "Business Central (Version 15.0)" };
    private static readonly int[] BaseIndexes = { 0, 7002, 7003 };

    private const string ExtName = "Price Calc. Handler - Test";
    private static readonly string[] ExtOptions = { "Test" };
    private static readonly int[] ExtIndexes = { 130514 };

    /// <summary>Simulates a source-compiled dependency's OWN sidecar write: the
    /// enumextension compiles while the base enum is not yet in the registry (the
    /// exact condition #1731's "own entries this dep's emit contributed" scoping
    /// produces for Tests-TestLibraries extending Base App's 7011). Returns the path
    /// written.</summary>
    private string WriteDependencySidecarWithExtensionOnly()
    {
        AlEnumMetadataRegistry.Clear();
        AlEnumMetadataRegistry.RegisterExtension(BaseId, ExtName, ExtOptions, ExtIndexes);
        var path = Path.Combine(_root, "dep.enum-registry.json");
        var count = AlEnumMetadataRegistry.SaveSidecar(path, new[] { BaseId });
        Assert.Equal(1, count);
        AlEnumMetadataRegistry.Clear();
        return path;
    }

    [Fact]
    public void SaveSidecar_ExtensionOnlyEntry_CarriesExtendsMarker_NotFlattenedUnderBaseName()
    {
        var path = WriteDependencySidecarWithExtensionOnly();
        var json = File.ReadAllText(path);

        // Positive: the persisted entry says "I am an extension of 7011", not "I am
        // enum 7011 named 'Price Calc. Handler - Test'" (the pre-fix flattening the
        // issue quotes verbatim).
        Assert.Contains("\"extends\":7011", json);
        Assert.Contains("\"options\":[\"Test\"]", json);

        // Negative: the base enum's own name must NOT appear as if it were this
        // entry's name — the whole defect was the extension's name overwriting it.
        Assert.DoesNotContain($"\"name\":\"{BaseName}\"", json);
    }

    [Fact]
    public void MultiBundleOrder_ExtensionReplayAfterBase_DoesNotClobberBase()
    {
        var depSidecar = WriteDependencySidecarWithExtensionOnly();

        // Bundle 1: the real Base App registration (AddBcAppPath / RegisterFromAppPath
        // in production) lands first and is never re-run for bundle 2 in the same
        // process (the `_bcAppPaths.Contains` guard the issue names).
        AlEnumMetadataRegistry.Register(BaseId, BaseName, BaseOptions, BaseIndexes);

        // Bundle 2: dependency is a compiled-deps cache HIT, so its sidecar replays
        // instead of re-emitting.
        var replayed = AlEnumMetadataRegistry.LoadSidecar(depSidecar);
        Assert.Equal(1, replayed);

        Assert.True(AlEnumMetadataRegistry.TryGet(BaseId, out var merged),
            "enum 7011 must still be registered after the dependency replay");

        // Positive: the base's own name and every base value survive — this is
        // literally "FixupEnumFieldOptionMetadata resolves 'Price Calculation Handler'
        // by name" from the issue; a clobbered base has no such name at all.
        Assert.Equal(BaseName, merged.Name);
        foreach (var (opt, idx) in BaseOptions.Zip(BaseIndexes))
        {
            var i = Array.IndexOf(merged.Options, opt);
            Assert.True(i >= 0, $"expected base option '{opt}' to survive; got [{string.Join(", ", merged.Options)}]");
            Assert.Equal(idx, merged.Indexes[i]);
        }

        // Positive: the extension's value is ALSO present — replay must merge, not
        // just "not clobber".
        var extIdx = Array.IndexOf(merged.Options, "Test");
        Assert.True(extIdx >= 0, $"expected extension option 'Test' to be merged in; got [{string.Join(", ", merged.Options)}]");
        Assert.Equal(130514, merged.Indexes[extIdx]);

        // Negative: this is exactly the value ALCompiler_ToInterfaceFromOption casts
        // through Price Calculation Mgt.FindSetup's default (7003) — must resolve to a
        // real base option, not the empty-name entry the bug produced.
        Assert.NotEqual(string.Empty, merged.Name);
    }

    [Fact]
    public void SingleBundleOrder_ExtensionReplayBeforeBase_KeepsExtensionValue()
    {
        var depSidecar = WriteDependencySidecarWithExtensionOnly();

        // Single-bundle order per the issue: DependencyLoader.LoadAll (and its sidecar
        // replay) runs BEFORE AddBcAppPath registers the base.
        var replayed = AlEnumMetadataRegistry.LoadSidecar(depSidecar);
        Assert.Equal(1, replayed);

        // The base registers AFTER the replay.
        AlEnumMetadataRegistry.Register(BaseId, BaseName, BaseOptions, BaseIndexes);

        Assert.True(AlEnumMetadataRegistry.TryGet(BaseId, out var merged),
            "enum 7011 must still be registered after the base registers over the replay");

        Assert.Equal(BaseName, merged.Name);

        // Positive: base values intact.
        foreach (var opt in BaseOptions)
            Assert.Contains(opt, merged.Options);

        // Positive: the extension's value ('Test' / 130514, the exact repro's arm B —
        // Codeunit79900.B_Literal_ExtensionValue_CastsToInterface) must NOT have been
        // dropped by the base registering over it.
        var extIdx = Array.IndexOf(merged.Options, "Test");
        Assert.True(extIdx >= 0,
            $"expected extension option 'Test' to survive the base registering afterwards; got [{string.Join(", ", merged.Options)}]");
        Assert.Equal(130514, merged.Indexes[extIdx]);
    }

    [Fact]
    public void LoadSidecar_LegacyEntryWithoutExtendsMarker_StillReplaysAsBaseRegistration()
    {
        // Negative/back-compat: a sidecar entry with no "extends" property at all
        // (the pre-#2709 shape, and any plain base entry written by this build) must
        // keep behaving as a base registration — never silently accumulate as an
        // "extension of itself" or throw.
        var path = Path.Combine(_root, "legacy.enum-registry.json");
        File.WriteAllText(path,
            "{\"enums\":[{\"id\":90300,\"name\":\"Legacy Enum\",\"options\":[\"A\",\"B\"],\"indexes\":[0,1],\"implementations\":[[],[]],\"captions\":[null,null]}]}");

        var replayed = AlEnumMetadataRegistry.LoadSidecar(path);
        Assert.Equal(1, replayed);

        Assert.True(AlEnumMetadataRegistry.TryGet(90300, out var entry));
        Assert.Equal("Legacy Enum", entry.Name);
        Assert.Equal(new[] { "A", "B" }, entry.Options);
    }
}
