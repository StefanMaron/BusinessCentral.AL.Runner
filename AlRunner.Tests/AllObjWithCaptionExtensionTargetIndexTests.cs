// AllObjWithCaptionExtensionTargetIndexTests — issue #5474, the COST half, plus the differential.
//
// Building the AllObjWithCaption rows resolved every extension row's target with a linear scan of
// every object of the target kind, so a walk cost (extension rows x objects). The fix answers the
// rows from one name -> id map per kind built on first use inside the walk and dropped when the
// walk ends.
//
// COST is asserted as a COUNT of entries examined (RecordPatches.ExtensionTargetEntriesExamined),
// never a duration: a count cannot flake on a loaded box and a fast box cannot satisfy it.
// ANSWERS are asserted by a differential: every lookup is made inside a walk (the indexed path)
// and outside one (the linear scan, the reference) over the adversarial inventory of
// ExtensionTargetFixture, and the two must agree on every name. The hand-listed expected ids for
// that inventory live in AllObjWithCaptionExtensionTargetAnswersTests, which also ran green
// against the unmodified resolver.
//
// Runner-local: real BC has no scan to count and no registry that grows between walks.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public sealed class AllObjWithCaptionExtensionTargetIndexTests : IDisposable
{
    private readonly string _scratch;
    private const int N = 120;          // objects of the target kind
    private const int Rows = 120;       // extension rows resolving against them
    private static readonly Type RP = typeof(RecordPatches);
    private static readonly string[] ExtensionKinds =
        { "PageExtension", "TableExtension", "EnumExtension", "PermissionSetExtension", "ReportExtension" };

    public AllObjWithCaptionExtensionTargetIndexTests()
    {
        _scratch = TestScratch.Dir("al-runner-5474-index");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        // The fixture registers enums, and the registry has no per-id removal.
        AlEnumMetadataRegistry.Clear();
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void Parse(string text)
        => RP.GetMethod("ParseSourceFileIntoAllExtractorsCore", BindingFlags.NonPublic | BindingFlags.Static)!
             .Invoke(null, new object?[] { text, null });

    private static string Name(int i) => $"ET Cnt {i:D3}";

    /// <summary>N objects of the extension kind's TARGET kind, in the source tier or in a
    /// dependency .app. Returns the id text the LAST one must resolve to.</summary>
    private string Populate(string extKind, bool dependency)
    {
        RecordPatches.ResetForReload();
        var ids = Enumerable.Range(0, N).Select(i => ExtensionTargetFixture.B + 5000 + i).ToArray();
        string Id(int i) => ids[i].ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (dependency)
        {
            var items = ids.Select((id, i) => (id, Name(i))).ToArray();
            var none = Array.Empty<(int, string)>();
            RecordPatches.AddBcAppPath(ExtensionTargetFixture.WriteApp(_scratch,
                tables: extKind == "TableExtension" ? items : none,
                pages: extKind == "PageExtension" ? items : none,
                reports: extKind == "ReportExtension" ? items : none,
                permissionSets: extKind == "PermissionSetExtension" ? items : none,
                extensions: Array.Empty<ExtensionTargetFixture.Ext>()));
            return Id(N - 1);
        }

        for (var i = 0; i < N; i++)
        {
            switch (extKind)
            {
                case "PageExtension": Parse($"page {ids[i]} \"{Name(i)}\" {{ PageType = Card; }}"); break;
                case "ReportExtension": Parse($"report {ids[i]} \"{Name(i)}\" {{ }}"); break;
                case "TableExtension":
                    Parse($"table {ids[i]} \"{Name(i)}\" {{ fields {{ field(1; \"Code\"; Code[20]) {{ }} }} keys {{ key(PK; \"Code\") {{ Clustered = true; }} }} }}");
                    break;
                case "PermissionSetExtension": Parse($"permissionset {ids[i]} \"{Name(i)}\" {{ Assignable = true; }}"); break;
                case "EnumExtension": AlEnumMetadataRegistry.Register(ids[i], Name(i), new[] { "A" }, new[] { 0 }); break;
                default: throw new ArgumentOutOfRangeException(nameof(extKind));
            }
        }
        return Id(N - 1);
    }

    private static long ExaminedBy(Action work)
    {
        var before = RecordPatches.ExtensionTargetEntriesExamined;
        work();
        return RecordPatches.ExtensionTargetEntriesExamined - before;
    }

    private static long WalkWith(string extKind, string target, string expected, int rows)
        => ExaminedBy(() => RecordPatches.ListingObjectInventory(() =>
        {
            for (var r = 0; r < rows; r++)
                Assert.Equal(expected, RecordPatches.ObjectSubtypeTextFor(extKind, target));
            return 0;
        }));

    public static IEnumerable<object[]> Cases()
    {
        foreach (var k in ExtensionKinds) yield return new object[] { k, false };
        // Enums have no dependency tier here (AlEnumMetadataRegistry already merges them).
        foreach (var k in ExtensionKinds.Where(k => k != "EnumExtension")) yield return new object[] { k, true };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ManyExtensionRowsInOneWalk_CostOnePassOverTheTargets_NotOnePassPerRow(string extKind, bool dependency)
    {
        var expected = Populate(extKind, dependency);
        var target = Name(N - 1);   // the last declared, so a linear scan reads all N

        // Everything a scan can read for this kind (the N targets plus whatever else is registered,
        // e.g. the process-lifetime System Application): a linear MISS reads each entry once.
        var everything = ExaminedBy(() => Assert.Equal("", RecordPatches.ObjectSubtypeTextFor(extKind, "ET No Such Target")));

        var once = WalkWith(extKind, target, expected, 1);
        var many = WalkWith(extKind, target, expected, Rows);
        var manyMore = WalkWith(extKind, target, expected, Rows * 2);

        Assert.True(everything >= N, $"a linear miss examined {everything} entries, fewer than the {N} targets");
        Assert.True(once > 0, "the walk examined nothing: the count seam is not counting this kind/tier");
        // One pass over what is registered, not one pass per row: Rows x N = Rows x N entries.
        Assert.True(many <= everything, $"{Rows} rows examined {many} entries; a single pass reads at most {everything}");
        // And the cost does not move with the number of rows resolved in the walk.
        Assert.Equal(many, manyMore);
        Assert.Equal(once, many);
    }

    [Fact]
    public void TheIndexIsDroppedWhenTheWalkEnds_SoTheNextWalkReadsTheRegistriesAgain()
    {
        var expected = Populate("PageExtension", dependency: false);
        var target = Name(N - 1);

        var first = WalkWith("PageExtension", target, expected, 1);
        var second = WalkWith("PageExtension", target, expected, 1);

        Assert.True(first > 0);
        // A kept index would answer the second walk with no scan at all.
        Assert.Equal(first, second);

        // And the second walk really does read the registry, not a remembered answer.
        var lateId = ExtensionTargetFixture.B + 9100;
        Parse($"page {lateId} \"ET Cnt Late\" {{ PageType = Card; }}");
        RecordPatches.ListingObjectInventory(() =>
        {
            Assert.Equal(lateId.ToString(), RecordPatches.ObjectSubtypeTextFor("PageExtension", "ET Cnt Late"));
            return 0;
        });
    }

    // ── the differential ────────────────────────────────────────────────────────────────────

    /// <summary>Every name the fixture declares or targets, plus spellings meant to break a
    /// naive index: case changes, a stripped space, a doubled space, a surrounding space.</summary>
    private static IEnumerable<string> NameGrid()
    {
        var names = new[]
        {
            "ET Dup Page", "et dup page", "ET Tie Page", "ET Shared Name", "ET Perm A", "et perm a",
            "ET Both Tiers", "ET Both Table", "ET Dep Page", "ET Dep Tie Page", "et dep tie page",
            "ET Dep Table", "ET Dep Report", "ET Dep Perm", "ET DEP PERM", "ET Missing", "ET Nowhere",
        };
        foreach (var n in names)
        {
            yield return n;
            yield return n.ToUpperInvariant();
            yield return n.ToLowerInvariant();
            yield return n.Replace(" ", "");          // NamesEqual would match this; the scan must not
            yield return n.Replace(" ", "  ");
            yield return " " + n;
            yield return n + " ";
        }
    }

    [Fact]
    public void EveryNameInEveryExtensionKind_ResolvesTheSameInsideAWalkAsByTheLinearScan()
    {
        var scratch = Path.Combine(_scratch, "diff");
        Directory.CreateDirectory(scratch);
        ExtensionTargetFixture.Seed(scratch);

        var grid = NameGrid().Distinct().ToList();
        var linear = new Dictionary<(string, string), string>();
        foreach (var kind in ExtensionKinds)
            foreach (var n in grid)
                linear[(kind, n)] = RecordPatches.ObjectSubtypeTextFor(kind, n);   // outside a walk: the scan

        var checkedCount = 0;
        var resolvedCount = 0;
        RecordPatches.ListingObjectInventory(() =>
        {
            foreach (var kind in ExtensionKinds)
                foreach (var n in grid)
                {
                    var inWalk = RecordPatches.ObjectSubtypeTextFor(kind, n);       // inside: the index
                    Assert.True(linear[(kind, n)] == inWalk,
                        $"{kind} target '{n}': linear scan says '{linear[(kind, n)]}', walk index says '{inWalk}'");
                    checkedCount++;
                    if (inWalk.Length > 0) resolvedCount++;
                }
            return 0;
        });

        Assert.Equal(ExtensionKinds.Length * grid.Count, checkedCount);
        // Not vacuous: a fair share of the grid resolves, and a fair share does not.
        Assert.InRange(resolvedCount, ExtensionKinds.Length * 8, checkedCount - 50);
    }

    [Fact]
    public void EveryRowOfARealWalk_AgreesWithTheLinearScanOutsideIt()
    {
        var scratch = Path.Combine(_scratch, "rows");
        Directory.CreateDirectory(scratch);
        ExtensionTargetFixture.Seed(scratch);

        var rows = AllObjWithCaptionExtensionTargetAnswersTests.Walk();
        var extensionRows = rows.Where(r => ExtensionKinds.Contains(r.Kind)).ToList();
        Assert.True(extensionRows.Count >= ExtensionTargetFixture.SourceExtensions.Length
                    + ExtensionTargetFixture.DependencyExtensions.Length);
        foreach (var r in extensionRows)
            Assert.True(RecordPatches.ObjectSubtypeTextFor(r.Kind, r.Subtype) == r.Text,
                $"{r.Kind} {r.Id} '{r.Name}' -> '{r.Subtype}': walk says '{r.Text}', linear scan says "
                + $"'{RecordPatches.ObjectSubtypeTextFor(r.Kind, r.Subtype)}'");
    }
}
