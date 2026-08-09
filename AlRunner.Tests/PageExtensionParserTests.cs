// PageExtensionParserTests — RED→GREEN guards for #1710 and #1711.
//
// #1710  `_parsedPages` was keyed on the object id ALONE for both `page` and
//        `pageextension`. AL gives those two SEPARATE id namespaces, so `page 50100` and
//        `pageextension 50100` are both legal in one app — and both wrote key 50100.
//        Extensions were written second, so the extension won, bringing SourceTableName = ""
//        and an empty control map with it: the real page's source table and every
//        control→field binding vanished, silently. `AlReportParser` had the same bug and
//        fixed it with two dictionaries; the page parser never got the same treatment.
//
// #1711  Extension objects were parsed less completely than base objects:
//        (a) `ParseFieldSyntax` ran with `tableLevelExtras: false` for tableextension
//            fields, dropping OptionMembers (wrong NCLOptionMetadata member count — the
//            defect class #1674 fixed for base tables) and AutoIncrement.
//        (b) `TryParsePageFile` stored an empty control map for every pageextension, so a
//            field control added by `addfirst`/`addlast` was invisible to
//            GetPageControlFieldMap and a TestPage could not resolve it.
//
// CONTROL IDS ARE NOT GUESSED HERE. BC derives a control's id with
// IdSpace.GetMemberId(ancestorObjectId, name) — and for a control declared inside a
// pageextension the ancestor is the PAGEEXTENSION's object id, not the base page's. That was
// established empirically, not assumed: a probe bundle (page 64300 "PXP Card",
// pageextension 64301 adding `field(NoteField; Rec."Note")` via addlast, a test reading
// Card.NoteField.Value()) failed in the runner with
//
//     out-of-scope: TestPage control 788108655 — testpage-control-binding …
//
// and 788108655 == MemberId(64301, "NoteField") while MemberId(64300, "NoteField") is
// 321499490. MemberIdMatchesTheIdBcActuallyEmitted below pins that observation so the helper
// here cannot drift away from BC's own hash.
//
// The parsers are private statics reached by reflection (same approach as
// SiblingParserSyntaxTreeTests); assertions otherwise go through the internal accessors the
// runtime itself calls, so these pin observable behaviour rather than a private field's shape.
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class PageExtensionParserTests
{
    private static readonly Type RP = typeof(AlRunner.Patches.RecordPatches);

    // 61950–61964 belong to SiblingParserSyntaxTreeTests; this class owns 61970–61979.
    private const int TableId = 61971;
    private const int PageId = 61970;          // a page AND a pageextension both use this id
    private const int PageExtId = 61972;       // the pageextension that really extends PageId

    private static void Parse(string method, string source) =>
        RP.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
          .Invoke(null, new object[] { source });

    private static System.Collections.IDictionary Dict(string field) =>
        (System.Collections.IDictionary)RP
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static object? Get(string field, object key)
    {
        var d = Dict(field);
        return d.Contains(key) ? d[key] : null;
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);

    private static void Cleanup()
    {
        Dict("_parsedTables").Remove(TableId);
        Dict("_parsedPages").Remove(PageId);
        Dict("_parsedPageExtensions").Remove(PageId);
        Dict("_parsedPageExtensions").Remove(PageExtId);
        Dict("_parsedExtensionFields").Remove("px base table");
    }

    /// <summary>BC's IdSpace.GetMemberId, reached on the real implementation by reflection so
    /// this cannot drift from the hash the runtime resolves controls with.</summary>
    private static int MemberId(int ancestorObjectId, string name)
    {
        var t = RP.Assembly.GetType("AlRunner.Patches.RunnerPageInstance")
            ?? throw new InvalidOperationException("AlRunner.Patches.RunnerPageInstance not found.");
        var m = t.GetMethod("MemberId", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RunnerPageInstance.MemberId not found.");
        return (int)m.Invoke(null, new object[] { ancestorObjectId, name })!;
    }

    private static readonly string TableSource = $$"""
        table {{TableId}} "PX Base Table"
        {
            fields
            {
                field(1; "Code"; Code[20]) { }
                field(2; "Description"; Text[100]) { }
                field(3; "Note"; Text[100]) { }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }
        """;

    // ─── #1710: `page N` and `pageextension N` are different objects ─────────────────────

    [Fact]
    public void PageAndPageExtensionWithTheSameId_DoNotOverwriteEachOther()
    {
        try
        {
            Parse("TryParseTableFile", TableSource);
            Parse("TryParsePageFile", $$"""
                page {{PageId}} "PX Card"
                {
                    PageType = Card;
                    SourceTable = "PX Base Table";
                    layout { area(content) { field(CodeField; Rec."Code") { } } }
                }

                pageextension {{PageId}} "PX Unrelated Ext" extends "Some Other Page"
                {
                    layout { addlast(content) { field(Ghost; Rec."Note") { } } }
                }
                """);

            // The page keeps everything the pageextension used to erase.
            Assert.True(AlRunner.Patches.RecordPatches.IsPageParsed(PageId));
            Assert.True(AlRunner.Patches.RecordPatches.PageDeclaresSourceTable(PageId),
                "a pageextension sharing the id must not erase the page's SourceTable");
            Assert.Equal(TableId, AlRunner.Patches.RecordPatches.GetSourceTableIdForPage(PageId));

            var map = AlRunner.Patches.RecordPatches.GetPageControlFieldMap(PageId);
            Assert.Equal(1, map[MemberId(PageId, "CodeField")]);

            // …and the unrelated extension's control does NOT leak onto this page: it extends
            // a different base page, so binding it here would fabricate a binding the AL
            // never declared.
            Assert.False(map.ContainsKey(MemberId(PageId, "Ghost")),
                "a pageextension of a DIFFERENT base page must not contribute controls here");
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void PageExtensionOnlyId_IsNotReportedAsAParsedPage()
    {
        try
        {
            Parse("TryParsePageFile", $$"""
                pageextension {{PageExtId}} "PX Card Ext" extends "PX Card"
                {
                    layout { addlast(content) { field(NoteField; Rec."Note") { } } }
                }
                """);

            // A pageextension is not a page. Answering IsPageParsed(extId) = true is what let a
            // pageextension stand in for a page of the same number in the first place.
            Assert.False(AlRunner.Patches.RecordPatches.IsPageParsed(PageExtId),
                "a pageextension id must not be reported as a parsed PAGE");

            // Opposite direction: it must still be recorded, in its own namespace, with the
            // base object it extends — otherwise the split has simply thrown the data away.
            var ext = Get("_parsedPageExtensions", PageExtId)!;
            Assert.Equal("PX Card", Prop(ext, "BaseName"));
            Assert.Equal("PX Card Ext", Prop(ext, "Name"));
        }
        finally { Cleanup(); }
    }

    // ─── #1711(b): a pageextension's field controls ──────────────────────────────────────

    [Fact]
    public void PageExtension_AddLastFieldControl_BindsIntoTheBasePagesControlMap()
    {
        try
        {
            Parse("TryParseTableFile", TableSource);
            Parse("TryParsePageFile", $$"""
                page {{PageId}} "PX Card"
                {
                    PageType = Card;
                    SourceTable = "PX Base Table";
                    layout { area(content) { field(CodeField; Rec."Code") { } } }
                }

                pageextension {{PageExtId}} "PX Card Ext" extends "PX Card"
                {
                    layout
                    {
                        addlast(content) { field(NoteField; Rec."Note") { } }
                        modify(CodeField) { Editable = false; }
                    }
                }
                """);

            var map = AlRunner.Patches.RecordPatches.GetPageControlFieldMap(PageId);

            // The control the extension added, keyed in the EXTENSION's id space (see header).
            Assert.Equal(3, map[MemberId(PageExtId, "NoteField")]);
            // The base page's own control is still bound.
            Assert.Equal(1, map[MemberId(PageId, "CodeField")]);
            // `modify(...)` declares no new control, so nothing else appears.
            Assert.Equal(2, map.Count);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void PageExtensionControl_IsNotKeyedInTheBasePagesIdSpace()
    {
        try
        {
            Parse("TryParseTableFile", TableSource);
            Parse("TryParsePageFile", $$"""
                page {{PageId}} "PX Card"
                {
                    SourceTable = "PX Base Table";
                    layout { area(content) { field(CodeField; Rec."Code") { } } }
                }

                pageextension {{PageExtId}} "PX Card Ext" extends "PX Card"
                {
                    layout { addfirst(content) { field(NoteField; Rec."Note") { } } }
                }
                """);

            // Keying the extension's control on the BASE page id is the plausible-looking
            // wrong answer: the entry exists, the map is non-empty, and the id never matches
            // the one BC hands GetField — so the failure lands as a loud but misleading
            // "control not bound to any field" instead of the value the test asked for.
            var map = AlRunner.Patches.RecordPatches.GetPageControlFieldMap(PageId);
            Assert.True(map.ContainsKey(MemberId(PageExtId, "NoteField")),
                "the extension-added control must be bound at all");
            Assert.False(map.ContainsKey(MemberId(PageId, "NoteField")),
                "an extension-added control must not be keyed in the base page's id space");
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void MemberIdMatchesTheIdBcActuallyEmitted()
    {
        // Pins the live observation quoted in this file's header: running a bundle with
        // page 64300 "PXP Card" + pageextension 64301 adding `field(NoteField; Rec."Note")`
        // made BC ask LiveNavTestPage.GetField for control 788108655.
        Assert.Equal(788108655, MemberId(64301, "NoteField"));
        Assert.Equal(321499490, MemberId(64300, "NoteField"));
    }

    // ─── #1711(a): a tableextension's field properties ───────────────────────────────────

    [Fact]
    public void TableExtensionOptionField_CarriesItsOptionMembers()
    {
        try
        {
            Parse("TryParseTableExtensionFile", $$"""
                tableextension {{PageExtId}} "PX Base Table Ext" extends "PX Base Table"
                {
                    fields
                    {
                        field(50000; "PX Status"; Option)
                        {
                            OptionMembers = ,Open,Released;
                        }
                        field(50001; "PX Count"; Integer) { }
                    }
                }
                """);

            var fields = ExtensionFields("PX Base Table");

            // Blank first member kept, tokens trimmed — the exact string BC's
            // NCLOptionMetadata ctor is handed. Dropping it gave the option the wrong member
            // COUNT, not just the wrong names, which is #1674's defect class.
            Assert.Equal(",Open,Released", FieldProp(fields, 50000, "OptionMembers"));

            // Opposite direction: a non-Option field must NOT acquire an option string.
            Assert.Null(FieldProp(fields, 50001, "OptionMembers"));
        }
        finally { Cleanup(); }
    }

    /// <summary>
    /// The AutoIncrement half of #1711(a), pinned HERE and not in an AL bundle: today's AL
    /// compiler rejects the property inside a tableextension outright — AL0404, "Property
    /// 'AutoIncrement' is not allowed on a table extension" (verified against BC
    /// 28.1.49838.50794 by compiling exactly this shape) — so no legal AL reaches it. It still
    /// matters because it is not the only producer of extension fields:
    /// BcAppSymbolCache.TableExtensions reads AutoIncrement out of a precompiled .app's
    /// SymbolReference.json and always has, so the source path was the odd one out, silently
    /// disagreeing with the symbol path about the same field shape.
    /// </summary>
    [Fact]
    public void TableExtensionAutoIncrementField_CarriesAutoIncrement()
    {
        try
        {
            Parse("TryParseTableExtensionFile", $$"""
                tableextension {{PageExtId}} "PX Base Table Ext" extends "PX Base Table"
                {
                    fields
                    {
                        field(50000; "PX Entry No."; Integer) { AutoIncrement = true; }
                        field(50001; "PX Count"; Integer) { }
                    }
                }
                """);

            var fields = ExtensionFields("PX Base Table");

            Assert.Equal(true, FieldProp(fields, 50000, "IsAutoIncrement"));
            // Opposite direction: a field that declares nothing stays non-autoincrement — a
            // table has exactly one AI counter, so a blanket true would hand it to the wrong
            // field.
            Assert.Equal(false, FieldProp(fields, 50001, "IsAutoIncrement"));
        }
        finally { Cleanup(); }
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────────

    private static System.Collections.IEnumerable ExtensionFields(string baseTableName)
    {
        var key = baseTableName.ToLowerInvariant();
        var fields = Get("_parsedExtensionFields", key);
        Assert.True(fields != null, $"_parsedExtensionFields has no entry for '{key}'");
        return (System.Collections.IEnumerable)fields!;
    }

    private static object? FieldProp(System.Collections.IEnumerable fields, int fieldId, string property)
    {
        foreach (var f in fields)
            if ((int)Prop(f, "FieldId")! == fieldId)
                return Prop(f, property);
        throw new Xunit.Sdk.XunitException($"no parsed extension field {fieldId}");
    }
}
