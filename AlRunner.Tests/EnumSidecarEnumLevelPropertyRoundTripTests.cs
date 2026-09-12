// EnumSidecarEnumLevelPropertyRoundTripTests — issue #3948.
//
// A RUNNER-MECHANISM test, not a claim about BC: it pins that AlEnumMetadataRegistry's
// sidecar round trip (SaveSidecar -> LoadSidecar) carries the three ENUM-LEVEL properties
// — Extensible (#3807) and the two implementation fallbacks DefaultImplementations /
// UnknownImplementations (#2306) — through a save+reload cycle with their values intact.
//
// Why it exists. Entry.Extensible has TWO writers. #3947 covered the compile-time one
// (BcCompiler.ReadEnumExtensible / ReadEnumImplementationFallback, symbol -> record); this
// is the other, the persisted sidecar. Measured on this branch before the tests were
// written: replacing SaveSidecar's `extensible = r.Entry.Extensible` with a constant null
// left the whole enum/sidecar surface green at Failed: 0, Passed: 181 — i.e. no test
// anywhere reached this path. The same held for the read side.
//
// Why the nullable states are the subject rather than an edge case. Extensible is a
// `bool?`, and the three states are genuinely distinct: null means "the enum declares no
// Extensible property at all", which the metadata render LEAVES OFF, while a declared
// false is rendered as a stated zero (see Entry's own doc comment and
// EnumExtensibleAndImplementationRenderTests). A round trip that collapsed absent-to-false
// would be invisible to a test that only ever asserted true — so each state gets its own
// assertion, and the absent case additionally asserts through a sidecar written with NO
// `extensible` property at all, which is what a legacy file looks like.
//
// Scope note: the merged-read path (TryGet taking the BASE entry's Extensible, an
// enumextension carrying none) is asserted by EnumExtensionSidecarRoundTripTests and by
// the render tests; what is new here is that the PERSISTED FORM preserves the value.
using Xunit;

namespace AlRunner.Tests;

public sealed class EnumSidecarEnumLevelPropertyRoundTripTests : IDisposable
{
    private readonly string _root;

    public EnumSidecarEnumLevelPropertyRoundTripTests()
    {
        _root = TestScratch.Dir("al-runner-enum-sidecar-enum-level-props");
        Directory.CreateDirectory(_root);
        AlEnumMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlEnumMetadataRegistry.Clear();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // Three ids so one sidecar can carry all three Extensible states at once and a
    // cross-contaminating bug (every entry reading the first entry's value) shows up as a
    // mismatch rather than as three tests that happen to agree.
    private const int ExtensibleTrueId = 61501;
    private const int ExtensibleFalseId = 61502;
    private const int ExtensibleAbsentId = 61503;

    private static readonly string[] Options = { "Zero", "One" };
    private static readonly int[] Indexes = { 0, 1 };

    /// <summary>Registers the three enums — declared-true, declared-false and
    /// declares-nothing — writes ONE sidecar holding all three, then clears the registry so
    /// the reload is the only thing that can put the values back. Returns the path.</summary>
    private string WriteSidecarWithAllThreeExtensibleStates()
    {
        AlEnumMetadataRegistry.Clear();
        AlEnumMetadataRegistry.Register(ExtensibleTrueId, "Extensible True", Options, Indexes,
            extensible: true);
        AlEnumMetadataRegistry.Register(ExtensibleFalseId, "Extensible False", Options, Indexes,
            extensible: false);
        AlEnumMetadataRegistry.Register(ExtensibleAbsentId, "Extensible Absent", Options, Indexes,
            extensible: null);

        var path = Path.Combine(_root, "three-states.enum-registry.json");
        var written = AlEnumMetadataRegistry.SaveSidecar(path,
            new[] { ExtensibleTrueId, ExtensibleFalseId, ExtensibleAbsentId });
        Assert.Equal(3, written);

        AlEnumMetadataRegistry.Clear();
        Assert.False(AlEnumMetadataRegistry.TryGet(ExtensibleTrueId, out _),
            "the registry must be empty before the reload, or the assertions below could be "
            + "satisfied by the pre-save registration instead of by the round trip");
        return path;
    }

    [Fact]
    public void RoundTrip_ExtensibleTrue_SurvivesSaveAndLoad()
    {
        var path = WriteSidecarWithAllThreeExtensibleStates();

        var replayed = AlEnumMetadataRegistry.LoadSidecar(path);
        Assert.Equal(3, replayed);

        Assert.True(AlEnumMetadataRegistry.TryGet(ExtensibleTrueId, out var entry));
        // Positive: the concrete value, not merely "has a value" — a round trip that
        // collapsed true to false would satisfy `HasValue`.
        Assert.True(entry.Extensible.HasValue, "a declared Extensible must not come back as absent");
        Assert.True(entry.Extensible!.Value);
    }

    [Fact]
    public void RoundTrip_ExtensibleFalse_SurvivesAsFalse_NotAsAbsent()
    {
        var path = WriteSidecarWithAllThreeExtensibleStates();

        Assert.Equal(3, AlEnumMetadataRegistry.LoadSidecar(path));

        Assert.True(AlEnumMetadataRegistry.TryGet(ExtensibleFalseId, out var entry));
        // The discriminating pair: a declared false must come back as a VALUE that is
        // false, never as null. Collapsing it to null would make the render leave the
        // member off, which is what BC does for an enum that declares nothing — a
        // different statement about a different enum.
        Assert.True(entry.Extensible.HasValue,
            "a declared `Extensible = false` must round-trip as a stated false, not as \"declares none\"");
        Assert.False(entry.Extensible!.Value);
    }

    [Fact]
    public void RoundTrip_ExtensibleAbsent_StaysNull_RatherThanBecomingFalse()
    {
        var path = WriteSidecarWithAllThreeExtensibleStates();

        Assert.Equal(3, AlEnumMetadataRegistry.LoadSidecar(path));

        Assert.True(AlEnumMetadataRegistry.TryGet(ExtensibleAbsentId, out var entry));
        // The other half of the pair above: "declares nothing" must not acquire a value.
        // A `?? false` anywhere on either side of the round trip fails exactly here and
        // nowhere else.
        Assert.Null(entry.Extensible);
    }

    [Fact]
    public void RoundTrip_AllThreeStatesInOneSidecar_StayDistinct()
    {
        // Guards against the whole-file failure mode the three per-state tests cannot see
        // individually: a reader that applies the FIRST entry's value to every entry, or a
        // writer that emits one shared value, passes any single-state test.
        var path = WriteSidecarWithAllThreeExtensibleStates();

        Assert.Equal(3, AlEnumMetadataRegistry.LoadSidecar(path));

        Assert.True(AlEnumMetadataRegistry.TryGet(ExtensibleTrueId, out var t));
        Assert.True(AlEnumMetadataRegistry.TryGet(ExtensibleFalseId, out var f));
        Assert.True(AlEnumMetadataRegistry.TryGet(ExtensibleAbsentId, out var a));

        Assert.Equal(new bool?[] { true, false, null },
            new[] { t.Extensible, f.Extensible, a.Extensible });
    }

    [Fact]
    public void LoadSidecar_EntryWithNoExtensibleProperty_ReadsAsAbsent_NotAsFalse()
    {
        // The legacy/back-compat shape: a sidecar written before #3807 carries no
        // `extensible` property at all. ReadNullableBool's "absent" branch is a different
        // code path from its JSON-null branch, and both must land on null — the state the
        // render leaves off. Written by hand rather than by SaveSidecar because this build
        // always emits the property.
        var path = Path.Combine(_root, "legacy-no-extensible.enum-registry.json");
        File.WriteAllText(path,
            "{\"enums\":[{\"id\":61504,\"name\":\"Legacy No Extensible\",\"options\":[\"A\",\"B\"],"
            + "\"indexes\":[0,1],\"implementations\":[[],[]],\"captions\":[null,null]}]}");

        Assert.Equal(1, AlEnumMetadataRegistry.LoadSidecar(path));

        Assert.True(AlEnumMetadataRegistry.TryGet(61504, out var entry));
        Assert.Equal("Legacy No Extensible", entry.Name);
        Assert.Null(entry.Extensible);
    }

    [Fact]
    public void RoundTrip_DefaultAndUnknownImplementationFallbacks_SurviveSaveAndLoad()
    {
        // Same file, same round trip, same persisted shape as Extensible — #2306's two
        // enum-level fallbacks are written and read by the identical SaveSidecar /
        // LoadSidecar pair, so they belong to this fixture rather than to one of their own.
        // Distinct, non-overlapping codeunit id lists so a reader that swapped the two
        // properties produces a mismatch rather than a coincidence.
        const int Id = 61505;
        var defaults = new[] { 90501 };
        var unknowns = new[] { 90502 };

        AlEnumMetadataRegistry.Clear();
        AlEnumMetadataRegistry.Register(Id, "Fallback Enum", Options, Indexes,
            defaultImplementations: defaults, unknownImplementations: unknowns, extensible: true);

        var path = Path.Combine(_root, "fallbacks.enum-registry.json");
        Assert.Equal(1, AlEnumMetadataRegistry.SaveSidecar(path, new[] { Id }));
        AlEnumMetadataRegistry.Clear();

        Assert.Equal(1, AlEnumMetadataRegistry.LoadSidecar(path));
        Assert.True(AlEnumMetadataRegistry.TryGet(Id, out var entry));

        Assert.Equal(defaults, entry.DefaultImplementations);
        Assert.Equal(unknowns, entry.UnknownImplementations);
        // Negative: the two must not have been read from each other's property.
        Assert.NotEqual(entry.DefaultImplementations, entry.UnknownImplementations);
    }

    [Fact]
    public void RoundTrip_EnumDeclaringNoFallbacks_ReadsAsNull_NotAsEmptyArray()
    {
        // ReadIdList's "absent or empty means declares none" convention, pinned across the
        // round trip: an enum with no DefaultImplementation must not come back carrying an
        // empty array, because null and empty reach different render branches (#2306).
        const int Id = 61506;

        AlEnumMetadataRegistry.Clear();
        AlEnumMetadataRegistry.Register(Id, "No Fallbacks", Options, Indexes,
            defaultImplementations: null, unknownImplementations: null, extensible: false);

        var path = Path.Combine(_root, "no-fallbacks.enum-registry.json");
        Assert.Equal(1, AlEnumMetadataRegistry.SaveSidecar(path, new[] { Id }));
        AlEnumMetadataRegistry.Clear();

        Assert.Equal(1, AlEnumMetadataRegistry.LoadSidecar(path));
        Assert.True(AlEnumMetadataRegistry.TryGet(Id, out var entry));

        Assert.Null(entry.DefaultImplementations);
        Assert.Null(entry.UnknownImplementations);
        // And the Extensible on the same entry is still the declared false — the two
        // properties are read by different helpers and must not interfere.
        Assert.False(entry.Extensible!.Value);
    }
}
