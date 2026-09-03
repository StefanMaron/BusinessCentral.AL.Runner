// PageControlExtensionFieldBindingTests — RED→GREEN guard for issue #2490.
//
// A TestPage field control bound to a field a tableextension adds to the page's source
// table used to throw `testpage-control-binding`, because RecordPatches.GetPageControlFieldMap
// resolved a control's field name only against ParsedTable.Fields (the base table's OWN
// declared fields) and never consulted _parsedExtensionFields — the dictionary
// MergeExtensionFields already writes tableextension fields into, and the SAME dictionary
// RecordPatches.NclMetaTableBuilder already reads when it builds the runtime NCLMetaTable.
// So the record itself always carried the extension field; only the control-binding lookup
// disagreed with it.
//
// This pins the fix at the unit level — GetPageControlFieldMap resolving a page control
// against a field that ONLY a tableextension declares — without needing a loaded BC
// runtime. The companion corpus PR (StefanMaron/BusinessCentral.AL.Language.Tests#115)
// proves the full AL-observable behavior (TestPage.Value()/.SetValue(), the extension
// field's OnValidate running) against real BC 27.5-28.4.
using System.Collections;
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class PageControlExtensionFieldBindingTests
{
    private const int TableId = 61897;
    private const int PageId = 61898;
    private const int ExtensionId = 61899;

    private static readonly Type RecordPatchesType = typeof(AlRunner.Patches.RecordPatches);

    private static IDictionary ParsedTables =>
        (IDictionary)RecordPatchesType
            .GetField("_parsedTables", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static IDictionary ParsedPages =>
        (IDictionary)RecordPatchesType
            .GetField("_parsedPages", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static IDictionary ParsedExtensionFields =>
        (IDictionary)RecordPatchesType
            .GetField("_parsedExtensionFields", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static void InvokeParser(string methodName, string source)
        => RecordPatchesType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { source });

    [Fact]
    public void GetPageControlFieldMap_ControlBoundToTableExtensionField_Resolves()
    {
        ParsedTables.Remove(TableId);
        ParsedPages.Remove(PageId);
        try
        {
            InvokeParser("TryParseTableFile", $$"""
                table {{TableId}} "PCE Base Table"
                {
                    fields
                    {
                        field(1; "No."; Code[20]) { }
                    }
                    keys { key(PK; "No.") { Clustered = true; } }
                }
                """);
            InvokeParser("TryParseTableExtensionFile", $$"""
                tableextension {{ExtensionId}} "PCE Base Ext" extends "PCE Base Table"
                {
                    fields
                    {
                        field(50000; "Ext Field"; Decimal) { }
                    }
                }
                """);
            InvokeParser("TryParsePageFile", $$"""
                page {{PageId}} "PCE Card Page"
                {
                    PageType = Card;
                    SourceTable = "PCE Base Table";
                    layout
                    {
                        area(Content)
                        {
                            field("No."; Rec."No.") { }
                            field("Ext Field"; Rec."Ext Field") { }
                        }
                    }
                }
                """);

            var map = AlRunner.Patches.RecordPatches.GetPageControlFieldMap(PageId);

            // The base field control must still resolve — this is the "before" behaviour
            // that never broke, so a fix that only helped the extension field and silently
            // regressed this one would not be caught anywhere else.
            Assert.Contains(map.Values, fieldNo => fieldNo == 1);

            // The extension field control is what #2490 reports as unresolved: before the
            // fix, "Ext Field" is entirely absent from the map (control id -> field 50000).
            Assert.Contains(map.Values, fieldNo => fieldNo == 50000);
            Assert.Equal(2, map.Count);
        }
        finally
        {
            ParsedTables.Remove(TableId);
            ParsedPages.Remove(PageId);
            ParsedExtensionFields.Remove("pce base table");
        }
    }
}
