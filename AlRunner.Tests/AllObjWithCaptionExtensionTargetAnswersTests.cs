// AllObjWithCaptionExtensionTargetAnswersTests — issue #5474, the ANSWER half.
//
// AllObjWithCaption's "Object Subtype" for the five *extension kinds is the target object's id.
// #5474 replaced the per-row linear scan that resolves it by a per-walk index, and a faster
// resolver that answers a different id is worse than a slow one. These tests pin the answers on
// an inventory built to make the two disagree (ExtensionTargetFixture): duplicate names, case,
// one name in several kinds, source versus dependency tier, two dependency .apps, a missing
// target. Every expected id is written out by hand.
//
// They use only APIs that exist before the change, so they were run against the unmodified
// resolver first: green there is what makes "the index answers the same" a claim about the old
// behaviour rather than about the new code.
//
// Runner-local on purpose: the fixture is a runner inventory (parsed source dictionaries plus
// registered .app symbol files), and what is asserted is which of several same-named objects the
// runner's own scan order picks. A service tier has no such scan; the BC claim underneath
// ("Object Subtype carries the target's id") is adjudicated upstream by the corpus
// (codeunit 60802, corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#291).
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public sealed class AllObjWithCaptionExtensionTargetAnswersTests : IDisposable
{
    private readonly string _scratch;

    public AllObjWithCaptionExtensionTargetAnswersTests()
    {
        _scratch = TestScratch.Dir("al-runner-5474-answers");
        Directory.CreateDirectory(_scratch);
        ExtensionTargetFixture.Seed(_scratch);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        // The fixture registers enums, and the registry has no per-id removal.
        AlEnumMetadataRegistry.Clear();
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static readonly Type RP = typeof(RecordPatches);

    internal sealed record Row(string Kind, int Id, string Name, string? Subtype, string Text);

    /// <summary>Every kind the walk may list; the ordinal is irrelevant, it only has to exist.</summary>
    private static Dictionary<string, int> Ordinals() => new[]
    {
        "table", "page", "pageextension", "report", "reportextension", "tableextension", "enum",
        "enumextension", "permissionset", "permissionsetextension", "codeunit", "query", "xmlport",
    }.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);

    private sealed class Recorder
    {
        public readonly List<Row> Rows = new();

        // Same shape as the AllObjWithCaption builder in RecordPatches: ObjectSubtypeTextFor(kind, subtype).
        public Array Build(string kind, int id, string name, string? caption, string? subtype,
            string normalizedKind, int typeOrdinal, Guid owningAppId)
        {
            var text = RecordPatches.ObjectSubtypeTextFor(kind, subtype);
            Rows.Add(new Row(kind, id, name, subtype, text));
            return new[] { text };
        }
    }

    /// <summary>
    /// The real <c>WalkObjectInventory</c> (the loop that builds the rows) over the seeded
    /// inventory, with a builder that records what AllObjWithCaption's own builder would hand
    /// <c>ObjectSubtypeTextFor</c>. Run under <c>ListingObjectInventory</c> exactly as
    /// <c>ObjectInventoryRowsFor</c> does.
    /// </summary>
    internal static List<Row> Walk()
    {
        var walk = RP.GetMethod("WalkObjectInventory", BindingFlags.NonPublic | BindingFlags.Static)!;
        var builderType = RP.GetNestedType("ObjectInventoryRowBuilder", BindingFlags.NonPublic)!;
        var recorder = new Recorder();
        var build = Delegate.CreateDelegate(builderType, recorder, typeof(Recorder).GetMethod(nameof(Recorder.Build))!);
        var key = new RecordPatches.ObjectInventoryKey(default, null, "*");
        try
        {
            RecordPatches.ListingObjectInventory(() => walk.Invoke(null, new object?[]
                { RecordPatches.AllObjWithCaptionVirtualTableId, "AllObjWithCaption", key, null, Ordinals(), null, build }));
        }
        catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        finally { RecordPatches.ResetObjectInventoryMemo(); }
        return recorder.Rows;
    }

    public static IEnumerable<object[]> AllExtensions()
        => ExtensionTargetFixture.SourceExtensions.Concat(ExtensionTargetFixture.DependencyExtensions)
            .Select(e => new object[] { e.Kind, e.Id, e.Target, e.Expected });

    [Theory]
    [MemberData(nameof(AllExtensions))]
    public void ATargetResolvesToTheHandListedId_OutsideAndInsideAWalk_AndOnRepeat(
        string kind, int id, string target, string expected)
    {
        Assert.Equal(expected, RecordPatches.ObjectSubtypeTextFor(kind, target));
        RecordPatches.ListingObjectInventory(() =>
        {
            // Twice in one walk: the second answer comes from whatever the first one built.
            Assert.Equal(expected, RecordPatches.ObjectSubtypeTextFor(kind, target));
            Assert.Equal(expected, RecordPatches.ObjectSubtypeTextFor(kind, target));
            return 0;
        });
        Assert.Equal(expected, RecordPatches.ObjectSubtypeTextFor(kind, target));
    }

    [Fact]
    public void EveryExtensionRowOfARealWalk_CarriesItsHandListedId()
    {
        var rows = Walk();
        foreach (var e in ExtensionTargetFixture.SourceExtensions.Concat(ExtensionTargetFixture.DependencyExtensions))
        {
            var mine = rows.Where(r => r.Kind == e.Kind && r.Id == e.Id).ToList();
            Assert.True(mine.Count == 1, $"{e.Kind} {e.Id} ({e.Name}) listed {mine.Count} times");
            Assert.Equal(e.Expected, mine[0].Text);
            // The target name the inventory carries is the one the fixture declared.
            Assert.Equal(e.Target, mine[0].Subtype);
        }
    }

    [Fact]
    public void KindScoping_ResolvesInTheTargetsOwnKind_NotInWhicheverMatchesFirst()
    {
        // "ET Shared Name" is a page, a report, a table and an enum, each with its own id.
        var shared = new Dictionary<string, string>
        {
            ["PageExtension"] = (ExtensionTargetFixture.B + 20).ToString(),
            ["ReportExtension"] = (ExtensionTargetFixture.B + 21).ToString(),
            ["TableExtension"] = (ExtensionTargetFixture.B + 22).ToString(),
            ["EnumExtension"] = (ExtensionTargetFixture.B + 24).ToString(),
            ["PermissionSetExtension"] = "",
        };
        foreach (var (kind, expected) in shared)
            Assert.Equal(expected, RecordPatches.ObjectSubtypeTextFor(kind, "ET Shared Name"));
        // All five kinds in ONE walk, where an index is shared between the lookups: it must be
        // per kind, or the first kind asked answers for the rest. Both orders.
        foreach (var order in new[] { shared.Keys.ToArray(), shared.Keys.Reverse().ToArray() })
            RecordPatches.ListingObjectInventory(() =>
            {
                foreach (var kind in order)
                    Assert.Equal(shared[kind], RecordPatches.ObjectSubtypeTextFor(kind, "ET Shared Name"));
                return 0;
            });
        // Four distinct ids, so a resolver ignoring the kind cannot satisfy this by accident.
        Assert.Equal(4, shared.Values.Where(v => v.Length > 0).Distinct().Count());
    }

    [Theory]
    [InlineData("PageExtension", "ETDupPage")]
    [InlineData("PageExtension", "ET  Dup Page")]
    [InlineData("TableExtension", "ETSharedName")]
    [InlineData("ReportExtension", "ETDepReport")]
    [InlineData("PermissionSetExtension", "ETPermA")]
    [InlineData("EnumExtension", "ETSharedName")]
    public void ANameDifferingByWhitespace_IsNotTheSameName(string kind, string target)
    {
        // NamesEqual would strip the spaces and let these match; the scan compares exactly.
        Assert.Equal("", RecordPatches.ObjectSubtypeTextFor(kind, target));
        RecordPatches.ListingObjectInventory(() =>
        {
            Assert.Equal("", RecordPatches.ObjectSubtypeTextFor(kind, target));
            return 0;
        });
    }

    [Fact]
    public void ABlankOrAbsentTarget_IsTheEmptyString()
    {
        foreach (var kind in new[] { "PageExtension", "TableExtension", "EnumExtension", "PermissionSetExtension", "ReportExtension" })
        {
            Assert.Equal("", RecordPatches.ObjectSubtypeTextFor(kind, null));
            Assert.Equal("", RecordPatches.ObjectSubtypeTextFor(kind, ""));
            RecordPatches.ListingObjectInventory(() =>
            {
                Assert.Equal("", RecordPatches.ObjectSubtypeTextFor(kind, "   "));
                return 0;
            });
        }
    }

    [Fact]
    public void AnObjectRegisteredBetweenTwoWalks_IsSeenByTheSecond()
    {
        const string name = "ET Late Page";
        string Resolve() => RecordPatches.ListingObjectInventory(
            () => RecordPatches.ObjectSubtypeTextFor("PageExtension", name));

        Assert.Equal("", Resolve());

        var id = ExtensionTargetFixture.B + 9001;
        RP.GetMethod("ParseSourceFileIntoAllExtractorsCore", BindingFlags.NonPublic | BindingFlags.Static)!
          .Invoke(null, new object?[] { $"page {id} \"{name}\" {{ PageType = Card; }}", null });

        Assert.Equal(id.ToString(), Resolve());
        Assert.Equal(id.ToString(), RecordPatches.ObjectSubtypeTextFor("PageExtension", name));
    }

    [Fact]
    public void ADependencyAppIsReadOnlyWhenTheSourceTierMisses_AndAnUnreadableOneKeepsRefusing()
    {
        // Registered last, then corrupted underneath the runner (#3143's shape).
        var path = ExtensionTargetFixture.WriteApp(_scratch,
            tables: Array.Empty<(int, string)>(), pages: new[] { (ExtensionTargetFixture.B + 160, "ET Doomed Page") },
            reports: Array.Empty<(int, string)>(), permissionSets: Array.Empty<(int, string)>(),
            extensions: Array.Empty<ExtensionTargetFixture.Ext>());
        RecordPatches.AddBcAppPath(path);
        File.WriteAllText(path, "not a zip archive at all");

        RecordPatches.ListingObjectInventory(() =>
        {
            // Found in the source tier: the dependency tier is never read, so nothing throws.
            Assert.Equal((ExtensionTargetFixture.B + 1).ToString(),
                RecordPatches.ObjectSubtypeTextFor("PageExtension", "ET Dup Page"));
            // A miss reads the dependencies, and refuses — on the first miss and on every later one.
            for (var i = 0; i < 2; i++)
                Assert.Throws<BcAppSymbolReadException>(
                    () => RecordPatches.ObjectSubtypeTextFor("PageExtension", "ET Nowhere"));
            // A hit in the source tier afterwards still answers.
            Assert.Equal((ExtensionTargetFixture.B + 1).ToString(),
                RecordPatches.ObjectSubtypeTextFor("PageExtension", "ET Dup Page"));
            return 0;
        });
    }
}
