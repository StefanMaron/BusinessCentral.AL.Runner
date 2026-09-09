// MetadataEquivalencePopulationTests — the gate for issue #3603.
//
// #3598 measured the DataClassification/Editable/EnumTypeId rules and reported "3,848 field
// observations" under the phrase "every field of both apps". Those are two different claims
// and only the first was true: the two apps declare more fields than the join reaches, and
// nobody had established what the difference was. An unexplained gap under the word "every"
// is the kind of overstatement that later reads as a counterexample rather than as an
// unmeasured case.
//
// The gap is now identified (docs/metadata-equivalence.md#the-125-extension-added-fields) and
// this file pins the two facts the prose rests on, so the prose cannot drift from the bundle:
//
//   1. BC emits MORE fields across the MetaTable set than the tables themselves declare,
//      because a tableextension's fields are merged into the extended table's document. That
//      is what makes 978 (symbol-side, table-declared) and 988 (emitted-side) both correct,
//      which is the reconciliation the issue's condition 3 asks for.
//   2. An ObsoleteState=Moved field is absent from the emitted table entirely. That is WHY
//      only 10 of the 125 extension-added fields per build are observable at all -- BC emits
//      nothing for the other 115, so there is no ground truth for them to agree or disagree
//      with. Table 242 is the extreme case: 107 fields named by its extension, 1 emitted.
//
// Both are read straight out of the generated bundle, so this test needs no BC engine and
// does not sit in the bc-engine-serial collection -- it cannot be silenced by the engine
// bootstrap skip that hides the rest of the harness.

using System.Xml;
using Xunit;

namespace AlRunner.Tests;

public sealed class MetadataEquivalencePopulationTests
{
    private const string Ns = "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects";

    private static IReadOnlyList<GroundTruthBundle> Bundles()
        => MetadataEquivalenceHarness.LoadBundles(MetadataEquivalencePaths.GroundTruthDirForThisBuild());

    private static XmlElement Root(GroundTruthBundle b, GroundTruthObject o)
    {
        var doc = new XmlDocument();
        doc.Load(Path.Combine(b.Directory, o.File));
        return doc.DocumentElement!;
    }

    private static IEnumerable<XmlElement> Fields(XmlElement table)
        => table.GetElementsByTagName("Field", Ns).Cast<XmlElement>();

    /// <summary>
    /// The emitted-side field population is what the harness compares, and it is NOT the
    /// count of fields the tables declare — a tableextension's surviving fields are merged in.
    ///
    /// <para>Pinned by naming the two tables that carry merged fields on the builds measured
    /// for #3603, rather than by asserting a total: a total moves on every Microsoft release
    /// and would make this test a version tripwire instead of a statement about the shape.</para>
    /// </summary>
    [SkippableFact]
    public void A_tableextension_field_is_merged_into_the_extended_tables_emitted_document()
    {
        var bundles = Bundles();
        Skip.If(bundles.Count == 0, "no metadata ground-truth bundle for this BC build.");

        // User Details 774: System Application's own extension adds 774-779. They are the six
        // that established the owner is the tableextension and not the extended table --
        // the table declares SystemMetadata, BC answers CustomerContent on all six.
        var userDetails = bundles
            .SelectMany(b => b.Objects.Where(o => o.Kind == "MetaTable" && o.Id == 774).Select(o => Root(b, o)))
            .FirstOrDefault();

        if (userDetails is not null)
        {
            Assert.Equal("SystemMetadata", userDetails.GetAttribute("DataClassification"));

            var merged = Fields(userDetails).Where(f => int.Parse(f.GetAttribute("ID")) >= 774).ToList();
            Assert.Equal(6, merged.Count);

            // Every one of the six is CustomerContent -- the extension is silent, so they take
            // ALDataClassification member 0 and NOT the extended table's SystemMetadata.
            Assert.All(merged, f => Assert.Equal("CustomerContent", f.GetAttribute("DataClassification")));

            // ...and that is a real distinction, not a table where everything is CustomerContent:
            // field 1 is the table's own and carries the table's classification.
            var own = Fields(userDetails).Single(f => f.GetAttribute("ID") == "1");
            Assert.Equal("SystemMetadata", own.GetAttribute("DataClassification"));
        }

        // No. Series Line 309: the other merged case, and the one that shows Removed survives.
        var noSeriesLine = bundles
            .SelectMany(b => b.Objects.Where(o => o.Kind == "MetaTable" && o.Id == 309).Select(o => Root(b, o)))
            .FirstOrDefault();

        if (noSeriesLine is not null)
        {
            var removed = Fields(noSeriesLine)
                .Where(f => f.GetAttribute("ObsoleteState") == "Removed")
                .Select(f => int.Parse(f.GetAttribute("ID")))
                .OrderBy(i => i).ToList();

            Assert.Equal(new[] { 11, 10000, 10001, 10002 }, removed);
            Assert.All(Fields(noSeriesLine).Where(f => f.GetAttribute("ObsoleteState") == "Removed"),
                f => Assert.Equal("CustomerContent", f.GetAttribute("DataClassification")));
        }

        Assert.True(userDetails is not null || noSeriesLine is not null,
            "neither table 774 nor table 309 was found in any bundle; the merged-field claim in "
            + "docs/metadata-equivalence.md#the-125-extension-added-fields is no longer checked "
            + "against anything. Re-derive it rather than deleting this assertion.");
    }

    /// <summary>
    /// An <c>ObsoleteState = Moved</c> field is omitted from the emitted table, which is why
    /// 115 of the 125 extension-added fields per build have no ground truth at all. Table 242
    /// is the extreme case and the one the "613 of 978 versus 988" reconciliation turns on.
    /// </summary>
    [SkippableFact]
    public void A_moved_field_is_absent_from_the_emitted_table_so_nothing_can_be_observed_about_it()
    {
        var bundles = Bundles();
        Skip.If(bundles.Count == 0, "no metadata ground-truth bundle for this BC build.");

        var sourceCodeSetup = bundles
            .SelectMany(b => b.Objects.Where(o => o.Kind == "MetaTable" && o.Id == 242).Select(o => Root(b, o)))
            .FirstOrDefault();
        Skip.If(sourceCodeSetup is null, "Business Foundation table 242 is not in any bundle for this build.");

        // ObsoleteSourceCodeSetupExt names 107 fields, every one of them Moved. BC emits one.
        var emitted = Fields(sourceCodeSetup!).ToList();
        Assert.Single(emitted);
        Assert.Equal("1", emitted[0].GetAttribute("ID"));

        // The positive half: nothing in this table is emitted carrying Moved. If BC ever starts
        // emitting Moved fields, the 115 become observable and the doc's arithmetic changes.
        Assert.DoesNotContain(emitted, f => f.GetAttribute("ObsoleteState") == "Moved");
    }

    /// <summary>
    /// The harness compares <c>MetaTable</c> only, and a <c>MetadataRuntimeDeltas</c> document
    /// carries no <c>Field</c> element — so a tableextension's fields are reachable ONLY
    /// through the merged table above. This is the negative that makes "40 observations, not
    /// 500" a fact about the data rather than a choice the harness made.
    /// </summary>
    [SkippableFact]
    public void A_MetadataRuntimeDeltas_document_carries_no_fields()
    {
        var bundles = Bundles();
        Skip.If(bundles.Count == 0, "no metadata ground-truth bundle for this BC build.");

        var deltas = bundles
            .SelectMany(b => b.Objects.Where(o => o.Kind == "MetadataRuntimeDeltas").Select(o => (b, o)))
            .ToList();
        Skip.If(deltas.Count == 0, "no MetadataRuntimeDeltas object in any bundle for this build.");

        foreach (var (bundle, obj) in deltas)
            Assert.Empty(Fields(Root(bundle, obj)));
    }
}
