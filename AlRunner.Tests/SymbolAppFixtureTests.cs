// SymbolAppFixtureTests — proves SymbolAppFixture produces a package that actually reaches
// RecordPatches._bcAppPaths, rather than one that merely exists.
//
// This test is the point of the fixture, not an accessory to it. Four separate times in one
// day, on this repository, a fixture was built that could not express the defect it was aimed
// at — and the worst instance PASSED and read as a result. The specific way that happens here
// is a `.app` that is emitted, copied into a bundle root, and then silently never registered
// because it carries no SymbolReference. So registration is asserted DIRECTLY, by reading the
// private list, instead of being inferred from a downstream consequence.
//
// The negative arm is what gives that assertion teeth: the identical bundle emitted WITHOUT a
// SymbolReference must NOT register. That is not a hypothetical shape — it is exactly what the
// layered pre-pass produces today (SiblingCompile calls EmitAppPackageToFile with no symbol
// reference), verified byte-for-byte on a real synthesized package whose zip holds only
// NavxManifest.xml and src/Tbl.al.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class SymbolAppFixtureTests
{
    private static readonly Guid WithSymbolsId = new("f6a7b8c9-2755-4a11-9111-111111111111");
    private static readonly Guid NoSymbolsId = new("f6a7b8c9-2755-4a11-9222-222222222222");

    /// <summary>The registered set, read from the private field the derived indexes rebuild
    /// from. Reflection on purpose: an accessor added for a test would be a second way to
    /// observe state whose single-source-of-truth is the point.</summary>
    private static IReadOnlyList<string> RegisteredAppPaths()
    {
        var field = typeof(RecordPatches).GetField("_bcAppPaths",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(field is not null,
            "RecordPatches._bcAppPaths was renamed or removed — this fixture exists to observe it, "
            + "and the accumulation question (#2755) is about that exact list");
        var list = (System.Collections.IEnumerable)field!.GetValue(null)!;
        return list.Cast<string>().ToList();
    }

    [Fact]
    public void AnEmittedPackageWithASymbolReference_IsRegistrable_AndOneWithoutIsNot()
    {
        var root = TestScratch.Dir("al-runner-symbolapp-fixture");

        var withDir = Path.Combine(root, "with-symbols");
        var withApp = Path.Combine(withDir, "WithSymbols.app");
        SymbolAppFixture.WriteBundleAndApp(withDir, withApp, WithSymbolsId,
            "Symbol Fixture With", 60700, "Symbol Fixture With Row", withSymbolReference: true);

        var withoutDir = Path.Combine(root, "no-symbols");
        var withoutApp = Path.Combine(withoutDir, "NoSymbols.app");
        SymbolAppFixture.WriteBundleAndApp(withoutDir, withoutApp, NoSymbolsId,
            "Symbol Fixture Without", 60710, "Symbol Fixture Without Row", withSymbolReference: false);

        Assert.True(File.Exists(withApp), "the with-symbols package was not written");
        Assert.True(File.Exists(withoutApp), "the no-symbols package was not written");

        // The property registration actually gates on.
        Assert.True(AlRunner.AppLoader.HasSymbolReference(withApp),
            "a package emitted WITH a SymbolReference must report one, or RegisterBundleSymbolApps "
            + "will skip it and any fixture built on it silently tests nothing");

        // The negative arm, and the shape the layered pre-pass really produces.
        Assert.False(AlRunner.AppLoader.HasSymbolReference(withoutApp),
            "a package emitted WITHOUT a SymbolReference must not report one — if this ever "
            + "becomes true the negative arm below stops proving anything");
    }

    [Fact]
    public void RegisterBundleSymbolApps_AddsTheSymbolBearingPackage_AndSkipsTheOther()
    {
        var root = TestScratch.Dir("al-runner-symbolapp-register");

        var withDir = Path.Combine(root, "with-symbols");
        var withApp = Path.Combine(withDir, "WithSymbols.app");
        SymbolAppFixture.WriteBundleAndApp(withDir, withApp, WithSymbolsId,
            "Symbol Fixture With", 60700, "Symbol Fixture With Row", withSymbolReference: true);

        var withoutDir = Path.Combine(root, "no-symbols");
        var withoutApp = Path.Combine(withoutDir, "NoSymbols.app");
        SymbolAppFixture.WriteBundleAndApp(withoutDir, withoutApp, NoSymbolsId,
            "Symbol Fixture Without", 60710, "Symbol Fixture Without Row", withSymbolReference: false);

        var before = RegisteredAppPaths().ToList();

        RecordPatches.RegisterBundleSymbolApps(withDir);
        RecordPatches.RegisterBundleSymbolApps(withoutDir);

        var after = RegisteredAppPaths().ToList();
        var added = after.Except(before, StringComparer.Ordinal).ToList();

        // POSITIVE: the symbol-bearing package reached the list the derived indexes rebuild from.
        // Asserted on the list itself, not on a table lookup that happens to succeed — a lookup
        // can be satisfied by _parsedTables, which is a different mechanism entirely.
        Assert.True(added.Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(withApp),
                                                 StringComparison.Ordinal)),
            "the symbol-bearing package did not reach RecordPatches._bcAppPaths, so a fixture "
            + "built on it would exercise nothing. Registered set gained: "
            + (added.Count == 0 ? "(nothing)" : string.Join(", ", added.Select(Path.GetFileName))));

        // NEGATIVE, and this is the teeth: the package without a SymbolReference must be skipped.
        // Without this, "added is non-empty" would be satisfied by registering everything, and
        // the positive assertion above would prove only that the method appends.
        Assert.False(added.Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(withoutApp),
                                                  StringComparison.Ordinal)),
            "a package with no SymbolReference was registered anyway — RegisterBundleSymbolApps "
            + "is documented to skip those, and the layered pre-pass emits exactly that shape, so "
            + "this would change what every derived index is rebuilt from");
    }
}
