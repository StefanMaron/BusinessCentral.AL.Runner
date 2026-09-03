// TableCaptionResolutionTests — RED→GREEN guard for #2545.
//
// AL's `Caption` on a table was parsed (AlObjectCaptionParser → _parsedObjectCaptions)
// and served to AllObjWithCaption, but never reached the NCLMetaTable the runner builds.
// BC answers Rec.TableCaption() and RecordRef.Caption() off that MetaTable's merged
// caption MultiLanguage and falls back to the object NAME when there is none — so the
// missing wiring did not produce an empty caption, it produced the table's name, which
// looks plausible.
//
// The BC-behavior half is proved upstream, where a real service tier adjudicates it:
// BusinessCentral.AL.Language.Tests `Test Record TableCaption` (60831) against fixture
// `ALT Captioned` (60830). These tests pin what that corpus test cannot reach, because
// it is not expressible in AL: ResolveTableCaption prefers the AL source, and PRESENCE
// in _parsedObjectCaptions is authoritative even when the recorded caption is null. A
// table the runner parsed from source owns its object id for this run, so "parsed,
// declares no Caption" must answer null rather than fall through and inherit a same-id
// caption out of some registered .app's symbols. Only a runner test can stage that id
// collision, and only a runner test can stage a dependency-symbol caption with no .app
// on disk.
//
// The other half of the fix — CallMetaTableCtor setting BOTH `caption` and `captionML`,
// because BC's read is the merged MultiLanguage and the plain string alone does nothing
// (the same pairing BuildMetaField already does one level down for a FIELD's caption,
// #1777) — is proved by the corpus test, which fails if either is missing.
using System.Collections;
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class TableCaptionResolutionTests
{
    private static readonly Type RecordPatchesType = typeof(AlRunner.Patches.RecordPatches);

    // Ids picked outside every other parser test's range so a leak is obvious.
    private const int CaptionedTableId = 61984;
    private const int SilentTableId = 61985;

    private const string CaptionedTableName = "TCR Captioned";
    private const string CaptionedTableCaption = "TCR Captioned Table Caption";

    // One table whose Caption differs from its name, one declaring no Caption at all.
    // The two names are never equal to their captions, so an implementation answering
    // with the name fails on the value, not merely on null-ness.
    private static string Fixture() => $$"""
        table {{CaptionedTableId}} "{{CaptionedTableName}}"
        {
            Caption = '{{CaptionedTableCaption}}';
            fields { field(1; "No."; Code[20]) { } }
        }

        table {{SilentTableId}} "TCR Silent"
        {
            fields { field(1; "No."; Code[20]) { } }
        }
        """;

    [Fact]
    public void ResolveTableCaption_ReturnsTheDeclaredCaption()
    {
        try
        {
            Invoke("TryParseObjectCaptionFile", Fixture());

            Assert.Equal(CaptionedTableCaption, ResolveTableCaption(CaptionedTableId));
            // The caption differs from the name on purpose: returning the name is the
            // exact wrong answer #2545 reports, and it would pass a non-null assertion.
            Assert.NotEqual(CaptionedTableName, ResolveTableCaption(CaptionedTableId));
        }
        finally { ForgetObjectCaptions(); }
    }

    [Fact]
    public void ResolveTableCaption_IsNullWhenTheTableDeclaresNoCaption()
    {
        try
        {
            Invoke("TryParseObjectCaptionFile", Fixture());

            // Null, not the name and not string.Empty. AL's default caption is the object
            // name, and BC's own NCLMetaTable applies that fallback — inventing it here
            // would set `caption` to the name and defeat the fallback rather than use it.
            Assert.Null(ResolveTableCaption(SilentTableId));
            // Control: the parser demonstrably ran over this source, so the null above is
            // an observation about the declaration and not about an unparsed fixture.
            Assert.True(HasObjectCaption("Table", SilentTableId),
                "the object-caption parser recorded no entry for the undeclared table — the fixture or the parser changed");
        }
        finally { ForgetObjectCaptions(); }
    }

    [Fact]
    public void ResolveTableCaption_IsNullForATableTheRunnerNeverSaw()
    {
        try
        {
            Invoke("TryParseObjectCaptionFile", Fixture());

            // Negative: nothing was ever parsed for 61999, and no .app in this test
            // process declares it, so there is no caption to answer with.
            Assert.Null(ResolveTableCaption(61999));
        }
        finally { ForgetObjectCaptions(); }
    }

    [Fact]
    public void ResolveTableCaption_ParsedSourceWithNoCaptionDoesNotInheritASymbolCaption()
    {
        try
        {
            Invoke("TryParseObjectCaptionFile", Fixture());
            // A registered .app claims the SAME table id and does declare a caption.
            SetSymbolTableCaptions(new Dictionary<int, string?>
            {
                [SilentTableId] = "Symbol Caption That Must Not Win",
                [CaptionedTableId] = "Symbol Caption That Must Not Win Either",
            });

            // Source presence wins in BOTH directions: the parsed table that declares no
            // Caption stays null, and the one that declares a Caption keeps its own.
            Assert.Null(ResolveTableCaption(SilentTableId));
            Assert.Equal(CaptionedTableCaption, ResolveTableCaption(CaptionedTableId));
        }
        finally
        {
            ForgetObjectCaptions();
            SetSymbolTableCaptions(null);
        }
    }

    [Fact]
    public void ResolveTableCaption_FallsBackToTheSymbolCaptionForAnUnparsedTable()
    {
        try
        {
            // No source parsed for this id at all — a precompiled dependency table. Its
            // declared caption lives on the .app's SymbolReference Objects[] entry, which
            // is what _bcSymbolTableCaptions holds.
            SetSymbolTableCaptions(new Dictionary<int, string?>
            {
                [61986] = "Dependency Table Caption",
                [61987] = null,
            });

            Assert.Equal("Dependency Table Caption", ResolveTableCaption(61986));
            // A dependency table that declares no Caption answers null, so BC's own name
            // fallback stands there too.
            Assert.Null(ResolveTableCaption(61987));
        }
        finally { SetSymbolTableCaptions(null); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void Invoke(string method, string source)
    {
        var m = RecordPatchesType.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"RecordPatches.{method} not found by reflection — signature may have changed.");
        m.Invoke(null, new object[] { source });
    }

    private static string? ResolveTableCaption(int tableId)
    {
        var m = RecordPatchesType.GetMethod("ResolveTableCaption",
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("RecordPatches.ResolveTableCaption not found by reflection.");
        return (string?)m.Invoke(null, new object[] { tableId });
    }

    private static IDictionary ObjectCaptions() => (IDictionary)RecordPatchesType
        .GetField("_parsedObjectCaptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static bool HasObjectCaption(string kind, int id) => ObjectCaptions().Contains((kind, id));

    private static void ForgetObjectCaptions()
    {
        var d = ObjectCaptions();
        foreach (var id in new[] { CaptionedTableId, SilentTableId })
            d.Remove(("Table", id));
    }

    // _bcSymbolTableCaptions is normally built by EnsureBcSymbolTableIndex from registered
    // .app files. Writing it directly is what lets these tests stage an id collision
    // between source and symbols without a real .app on disk. Setting it non-null also
    // makes EnsureBcSymbolTableIndex's own short-circuit irrelevant here, because that
    // gate reads _bcSymbolTableIndex, which stays as the surrounding process left it.
    private static void SetSymbolTableCaptions(Dictionary<int, string?>? captions)
    {
        var indexField = RecordPatchesType.GetField("_bcSymbolTableIndex", BindingFlags.NonPublic | BindingFlags.Static)!;
        var captionField = RecordPatchesType.GetField("_bcSymbolTableCaptions", BindingFlags.NonPublic | BindingFlags.Static)!;
        if (captions == null)
        {
            captionField.SetValue(null, null);
            indexField.SetValue(null, null);
            return;
        }
        // A non-null table index is what stops EnsureBcSymbolTableIndex rebuilding (and
        // wiping) the captions this test just staged.
        if (indexField.GetValue(null) == null)
            indexField.SetValue(null, Activator.CreateInstance(indexField.FieldType));
        captionField.SetValue(null, captions);
    }

}
