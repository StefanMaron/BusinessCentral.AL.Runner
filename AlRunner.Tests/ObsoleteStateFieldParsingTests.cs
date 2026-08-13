// ObsoleteStateFieldParsingTests — RED→GREEN guard for #1780.
//
// The Field virtual table (2000000041) answered ObsoleteState = No for every field,
// including one declared `ObsoleteState = Removed`, because ParseFieldSyntax
// (RecordPatches.AlSourceParser.cs) never read the field's ObsoleteState/ObsoleteReason
// properties at all — ParsedField had no slot for them, so BuildMetaField
// (RecordPatches.NclMetaTableBuilder.cs) had nothing to pass to MetaField's
// obsoleteState/obsoleteReason ctor params and every field fell through to the ctor's
// own "No" default.
//
// This drives the real parser (TryParseTableFile) by reflection, exactly like
// AlSourceParserSyntaxTreeTests, so it proves the parser itself captures the AL
// declaration rather than proving a helper works in isolation. The BEHAVIORAL claim
// ("the Field virtual table reports these values") is proven upstream against a live
// BC service tier — see StefanMaron/BusinessCentral.AL.Language.Tests PR #41
// (codeunit 60956 "Test Field ObsoleteState VTbl" / table 60984 "ALT ObsoleteState
// Fixture"), per docs/rules/bc-behavior-tests-go-upstream.md.
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class ObsoleteStateFieldParsingTests
{
    private const int TableId = 61897;

    private static readonly Type RecordPatchesType = typeof(AlRunner.Patches.RecordPatches);

    private static System.Collections.IDictionary ParsedTables =>
        (System.Collections.IDictionary)RecordPatchesType
            .GetField("_parsedTables", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static object ParseTableAndGetField(string source, int fieldId)
    {
        var parse = RecordPatchesType.GetMethod("TryParseTableFile",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        parse.Invoke(null, new object[] { source });
        Assert.True(ParsedTables.Contains(TableId), $"table {TableId} was not parsed at all");
        var table = ParsedTables[TableId]!;
        foreach (var f in (System.Collections.IEnumerable)table.GetType()
                     .GetProperty("Fields")!.GetValue(table)!)
        {
            if ((int)f.GetType().GetProperty("FieldId")!.GetValue(f)! == fieldId) return f;
        }
        throw new Xunit.Sdk.XunitException($"field {fieldId} was not parsed");
    }

    private static string ObsoleteState(object parsedField) =>
        (string)parsedField.GetType().GetProperty("ObsoleteState")!.GetValue(parsedField)!;

    private static string? ObsoleteReason(object parsedField) =>
        (string?)parsedField.GetType().GetProperty("ObsoleteReason")!.GetValue(parsedField);

    private static string Table(string fieldTwoBody) => $$"""
        table {{TableId}} "ObsoleteState Parse Fixture"
        {
            fields
            {
                field(1; "Code"; Code[10]) { }
                field(2; Status; Text[50])
                {
        {{fieldTwoBody}}
                }
            }

            keys
            {
                key(PK; "Code") { Clustered = true; }
            }
        }
        """;

    // ─── Positive: each declared state is captured, not collapsed to "No" ────────────────

    [Fact]
    public void ObsoleteState_Removed_IsCapturedWithItsReason()
    {
        try
        {
            var field = ParseTableAndGetField(
                Table("""
                        ObsoleteState = Removed;
                        ObsoleteReason = 'no longer used';
                    """),
                fieldId: 2);

            Assert.Equal("Removed", ObsoleteState(field));
            Assert.Equal("no longer used", ObsoleteReason(field));
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void ObsoleteState_Pending_IsCapturedWithItsReason()
    {
        try
        {
            var field = ParseTableAndGetField(
                Table("""
                        ObsoleteState = Pending;
                        ObsoleteReason = 'replaced by Status Code';
                    """),
                fieldId: 2);

            Assert.Equal("Pending", ObsoleteState(field));
            Assert.Equal("replaced by Status Code", ObsoleteReason(field));
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void ObsoleteReason_WithEmbeddedQuote_IsUnescaped()
    {
        // Same AL doubled-quote escape as InitValue/Caption/const() — a reason literal
        // containing an embedded quote must come back with it resolved, not doubled.
        try
        {
            var field = ParseTableAndGetField(
                Table("""
                        ObsoleteState = Removed;
                        ObsoleteReason = 'won''t be used again';
                    """),
                fieldId: 2);

            Assert.Equal("won't be used again", ObsoleteReason(field));
        }
        finally { ParsedTables.Remove(TableId); }
    }

    // ─── Negative: the undeclared default, and the trap a naive implementation falls into ─

    [Fact]
    public void ObsoleteState_Undeclared_DefaultsToNo_WithNoReason()
    {
        // A field with no ObsoleteState/ObsoleteReason property at all — the AL/BC default —
        // must read back as the explicit "No" state with no reason, distinguishing it from a
        // declared-but-empty state.
        try
        {
            var field = ParseTableAndGetField(Table(""), fieldId: 2);

            Assert.Equal("No", ObsoleteState(field));
            Assert.Null(ObsoleteReason(field));
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void ObsoleteState_DeclaredOnOneFieldOnly_DoesNotLeakToItsSibling()
    {
        // Negative control against the exact defect class #1780 reported: a provider that
        // answered every row with a fixed ObsoleteState would pass a test that only checks
        // the Removed field. Parsing two fields in the same table, only one Removed, and
        // asserting the OTHER stays "No" rules that out at the parser layer.
        var source = $$"""
            table {{TableId}} "ObsoleteState Parse Fixture"
            {
                fields
                {
                    field(1; "Code"; Code[10]) { }
                    field(2; Live; Text[30]) { }
                    field(3; Gone; Text[30])
                    {
                        ObsoleteState = Removed;
                        ObsoleteReason = 'removed';
                    }
                }

                keys
                {
                    key(PK; "Code") { Clustered = true; }
                }
            }
            """;
        try
        {
            var parse = RecordPatchesType.GetMethod("TryParseTableFile",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            parse.Invoke(null, new object[] { source });

            var live = ParseTableAndGetField(source, fieldId: 2);
            var gone = ParseTableAndGetField(source, fieldId: 3);

            Assert.Equal("No", ObsoleteState(live));
            Assert.Null(ObsoleteReason(live));
            Assert.Equal("Removed", ObsoleteState(gone));
            Assert.Equal("removed", ObsoleteReason(gone));
        }
        finally { ParsedTables.Remove(TableId); }
    }
}
