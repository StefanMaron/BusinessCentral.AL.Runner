// AlSourceParserCommentTests — RED→GREEN guard for #1690.
//
// The AL source parser matches table/field properties with plain regexes over raw file
// text, so an ordinary explanatory comment that happens to mention a property name was
// read AS that property. The reported shape:
//
//     // InitValue = true: new rows default to accepted. A consumer filters on
//     // Accept = true before writing anything.
//     InitValue = true;
//
// RxInitValue matched inside the comment first and `[^;]+` ran on to the semicolon four
// words later, so the field's InitValue became the comment prose and the real declaration
// below was never reached. Every Insert of the table then died with
// `NavNCLEvaluateException: The value "true: new rows default to accepted. ..." can not be
// evaluated into type Boolean`.
//
// These drive the real parser (TryParseTableFile) rather than the comment blanker alone —
// the blanker passing in isolation would not prove the parser actually consults it. Like
// BcCompilerLoaderSelfExclusionTests, the private static entry point is reached by
// reflection; it is pure text/regex logic with no BC runtime involved.
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

public class AlSourceParserCommentTests
{
    private const int TableId = 61895; // the issue's repro id

    private static readonly Type RecordPatchesType =
        typeof(AlRunner.Patches.RecordPatches);

    // Parses `source` and returns the ParsedField for `fieldId`, reflected into a tuple so
    // the test does not need the internal record type's shape.
    private static (string? InitValueText, string? Caption, string FieldName) ParseField(
        string source, int fieldId)
    {
        var parse = RecordPatchesType.GetMethod("TryParseTableFile",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "RecordPatches.TryParseTableFile not found by reflection — signature may have changed.");
        var tablesField = RecordPatchesType.GetField("_parsedTables",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RecordPatches._parsedTables not found.");
        var tables = (System.Collections.IDictionary)tablesField.GetValue(null)!;

        try
        {
            parse.Invoke(null, new object[] { source });
            Assert.True(tables.Contains(TableId), $"table {TableId} was not parsed at all");
            var table = tables[TableId]!;
            var fields = (System.Collections.IEnumerable)table.GetType()
                .GetProperty("Fields")!.GetValue(table)!;
            foreach (var f in fields)
            {
                var t = f.GetType();
                if ((int)t.GetProperty("FieldId")!.GetValue(f)! != fieldId) continue;
                return ((string?)t.GetProperty("InitValueText")!.GetValue(f),
                        (string?)t.GetProperty("Caption")!.GetValue(f),
                        (string)t.GetProperty("FieldName")!.GetValue(f)!);
            }
            throw new Xunit.Sdk.XunitException($"field {fieldId} was not parsed");
        }
        finally
        {
            // Don't leave this fixture table in the process-wide parser state.
            tables.Remove(TableId);
        }
    }

    private static string Table(string acceptFieldBody) => $$"""
        table {{TableId}} "IC Flag Table"
        {
            fields
            {
                field(1; "Code"; Code[10]) { }
                field(2; Accept; Boolean)
                {
        {{acceptFieldBody}}
                }
            }

            keys
            {
                key(PK; "Code") { Clustered = true; }
            }
        }
        """;

    [Fact]
    public void InitValue_MentionedInLineComment_TakesTheRealDeclaration()
    {
        var (initValue, _, _) = ParseField(Table("""
                    // Per-row user flag bound by a list page to an accept/skip checkbox.
                    // InitValue = true: new rows default to accepted. A consumer filters on
                    // Accept = true before writing anything.
                    Caption = 'Accept';
                    InitValue = true;
        """), fieldId: 2);

        // Before the fix this was "true: new rows default to accepted. A consumer filters on
        // // Accept = true before writing anything." — the comment prose, glued together.
        Assert.Equal("true", initValue);
    }

    [Fact]
    public void InitValue_MentionedInBlockComment_TakesTheRealDeclaration()
    {
        var (initValue, _, _) = ParseField(Table("""
                    /* legacy: InitValue = false; kept here for the migration note */
                    InitValue = true;
        """), fieldId: 2);

        Assert.Equal("true", initValue);
    }

    [Fact]
    public void Caption_MentionedInComment_TakesTheRealDeclaration()
    {
        // Same class of bug on a different property — proves the fix is in the shared text
        // pass, not a special case bolted onto InitValue.
        var (_, caption, _) = ParseField(Table("""
                    // Caption = 'Do not use this one';
                    Caption = 'Accept';
        """), fieldId: 2);

        Assert.Equal("Accept", caption);
    }

    [Fact]
    public void CommentedOutField_IsNotParsedAsAField()
    {
        var source = Table("""
                    InitValue = true;
        """).Replace("""
                field(1; "Code"; Code[10]) { }
        """, """
                field(1; "Code"; Code[10]) { }
                // field(3; Obsolete; Integer) { InitValue = 42; }
        """);

        var parse = RecordPatchesType.GetMethod("TryParseTableFile",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var tables = (System.Collections.IDictionary)RecordPatchesType
            .GetField("_parsedTables", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        try
        {
            parse.Invoke(null, new object[] { source });
            var table = tables[TableId]!;
            var ids = ((System.Collections.IEnumerable)table.GetType()
                    .GetProperty("Fields")!.GetValue(table)!)
                .Cast<object>()
                .Select(f => (int)f.GetType().GetProperty("FieldId")!.GetValue(f)!)
                .OrderBy(x => x).ToArray();

            // A phantom field id 3 would desync this table's metadata from the DLL the AL
            // compiler actually emitted for it.
            Assert.Equal(new[] { 1, 2 }, ids);
        }
        finally { tables.Remove(TableId); }
    }

    // Negative direction: the fix must not over-reach. `//` inside a string literal is
    // literal text, not a comment — blanking it would truncate the caption instead.
    [Fact]
    public void DoubleSlashInsideAStringLiteral_IsNotTreatedAsAComment()
    {
        var (initValue, caption, _) = ParseField(Table("""
                    Caption = 'Ratio // Net of returns';
                    InitValue = true;
        """), fieldId: 2);

        Assert.Equal("Ratio // Net of returns", caption);
        Assert.Equal("true", initValue);
    }

    [Fact]
    public void QuotedIdentifierContainingSlashes_SurvivesAndStillParses()
    {
        var (initValue, _, name) = ParseField(Table("""
                    InitValue = true;
        """).Replace("field(2; Accept; Boolean)", """field(2; "Accept // Skip"; Boolean)"""),
            fieldId: 2);

        Assert.Equal("Accept // Skip", name);
        Assert.Equal("true", initValue);
    }
}
