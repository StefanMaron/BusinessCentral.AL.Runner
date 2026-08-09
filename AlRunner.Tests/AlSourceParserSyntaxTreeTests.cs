// AlSourceParserSyntaxTreeTests — RED→GREEN guard for #1696.
//
// The table parser used to match properties with plain regexes over raw .al text. #1690 and
// #1674 each fixed one instance of the resulting bug class by tightening one pattern; the
// class itself survives every such fix, because a regex cannot know where an AL construct
// actually begins and ends.
//
// The case pinned here is the one the class produces most quietly — a SILENT WRONG VALUE
// rather than a crash. `RxInitValue` was `\bInitValue\s*=\s*([^;]+);`: a semicolon inside a
// string literal is a perfectly ordinary character to AL, but `[^;]+` cannot cross one, so
//
//     InitValue = 'Open; pending review';
//
// captured `'Open` — a literal with no closing quote. Downstream, NclMetaTableBuilder strips
// the quotes it expects to find in pairs and the field initialises to the wrong text. Real BC
// compiles this without complaint.
//
// The fix parses with Microsoft.Dynamics.Nav.CodeAnalysis' own AL parser — the same front end
// BcCompiler already runs over the very same files — so property values come back as typed
// nodes and the question "where does this value end" is answered by the compiler, not by a
// character class.
//
// These drive the real parser (TryParseTableFile / TryParseTableExtensionFile) by reflection,
// exactly like AlSourceParserCommentTests, so they prove the parser consults the tree rather
// than proving a helper works in isolation.
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class AlSourceParserSyntaxTreeTests
{
    private const int TableId = 61896;

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

    private static string? Prop(object parsedField, string name) =>
        (string?)parsedField.GetType().GetProperty(name)!.GetValue(parsedField);

    private static string Table(string fieldTwoBody) => $$"""
        table {{TableId}} "Syntax Tree Fixture"
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

    // ─── Positive: the value the regex silently truncated ────────────────────────────────

    [Fact]
    public void InitValue_StringLiteralContainingASemicolon_IsCapturedWhole()
    {
        try
        {
            var field = ParseTableAndGetField(
                Table("            InitValue = 'Open; pending review';"), fieldId: 2);

            // Before the syntax-tree parse this was `'Open` — `[^;]+` stopped at the semicolon
            // inside the literal, leaving an unbalanced quote for NclMetaTableBuilder to strip.
            // The value is deliberately compared WITH its quotes: the output contract is raw AL
            // text, and NclMetaTableBuilder does the type-aware unquoting (that split is what
            // #1674's blank-enum fix depends on).
            Assert.Equal("'Open; pending review'", Prop(field, "InitValueText"));
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void Caption_StringLiteralContainingASemicolon_IsCapturedWhole()
    {
        try
        {
            var field = ParseTableAndGetField(
                Table("            Caption = 'Status; current';"), fieldId: 2);

            Assert.Equal("Status; current", Prop(field, "Caption"));
        }
        finally { ParsedTables.Remove(TableId); }
    }

    // ─── Negative: the traps a naive tree walk would introduce ───────────────────────────

    [Fact]
    public void Caption_WithTrailingCommentProperty_KeepsOnlyTheLabelLiteral()
    {
        // Trap 1 from #1696's implementation map. `LabelPropertyValueSyntax.ToString()` returns
        // the WHOLE label including its trailing parts — `'It''s on', Comment='x'` — so taking
        // the node's text wholesale would silently append `, Comment='x'` to every caption that
        // carries a developer comment. Only the leading literal is the caption, with AL's
        // doubled-quote escape resolved.
        try
        {
            var field = ParseTableAndGetField(
                Table("            Caption = 'It''s on', Comment='translator note';"), fieldId: 2);

            Assert.Equal("It's on", Prop(field, "Caption"));
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void QuotedIdentifiers_AreUnquoted()
    {
        // Trap 2 from the map. `IdentifierNameSyntax.Identifier.ValueText` keeps the double
        // quotes (`"Entry No."`), where the regexes captured the inner group. Leaving them on
        // would break every by-name lookup — key field resolution, extension merges, the lot.
        try
        {
            var field = ParseTableAndGetField(
                Table("            InitValue = 'x';")
                    .Replace("field(2; Status; Text[50])", """field(2; "Status Code"; Text[50])"""),
                fieldId: 2);

            Assert.Equal("Status Code", Prop(field, "FieldName"));

            var table = ParsedTables[TableId]!;
            Assert.Equal("Syntax Tree Fixture",
                (string)table.GetType().GetProperty("TableName")!.GetValue(table)!);

            // The PK is declared as `key(PK; "Code")` — a quoted name that must still resolve
            // to field 1 by name. A stray pair of quotes here would leave the table with no PK.
            var pk = (System.Collections.IEnumerable)table.GetType()
                .GetProperty("PkFieldIds")!.GetValue(table)!;
            Assert.Equal(new[] { 1 }, pk.Cast<int>().ToArray());
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void NonTableObjectsInTheSameFile_DoNotContributeFields()
    {
        // The old slice-between-regex-matches scheme used only `table` and `tableextension`
        // starts as boundaries, so any other object type that followed a table was swept into
        // its slice. Object boundaries are structural in the tree.
        var source = Table("            InitValue = 'x';") + """

            codeunit 61896 "Fixture Helper"
            {
                procedure Reset()
                begin
                end;
            }
            """;
        try
        {
            var parse = RecordPatchesType.GetMethod("TryParseTableFile",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            parse.Invoke(null, new object[] { source });

            var table = ParsedTables[TableId]!;
            var ids = ((System.Collections.IEnumerable)table.GetType()
                    .GetProperty("Fields")!.GetValue(table)!)
                .Cast<object>()
                .Select(f => (int)f.GetType().GetProperty("FieldId")!.GetValue(f)!)
                .OrderBy(x => x).ToArray();

            Assert.Equal(new[] { 1, 2 }, ids);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    // ─── CalcFormula ─────────────────────────────────────────────────────────────────────

    private static object CalcFormula(string formula)
    {
        var parsed = typeof(AlRunner.Patches.RecordPatches)
            .GetMethod("TryParseCalcFormula", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { $"CalcFormula = {formula};" });
        return parsed ?? throw new Xunit.Sdk.XunitException($"formula did not parse: {formula}");
    }

    private static (string Type, string Table, string? Field, List<(string Src, string Parent)> Filters)
        Read(object calcFormula)
    {
        var t = calcFormula.GetType();
        var filters = new List<(string, string)>();
        foreach (var f in (System.Collections.IEnumerable)t.GetProperty("Filters")!.GetValue(calcFormula)!)
            filters.Add(((string)f.GetType().GetProperty("SourceFieldName")!.GetValue(f)!,
                         (string)f.GetType().GetProperty("ParentFieldName")!.GetValue(f)!));
        return ((string)t.GetProperty("FormulaType")!.GetValue(calcFormula)!,
                (string)t.GetProperty("SourceTableName")!.GetValue(calcFormula)!,
                (string?)t.GetProperty("SourceFieldName")!.GetValue(calcFormula),
                filters);
    }

    [Fact]
    public void CalcFormula_FilterLiteralContainingASemicolon_StillParses()
    {
        // Same `[^;]+` class as InitValue, one level down: RxCalcFormula stopped at the first
        // semicolon, so a filter literal containing one truncated the formula, RxCalcFormulaParts
        // then failed to match the fragment, TryParseCalcFormula returned null, and the FlowField
        // was left at EmptyFormula — CalcFields() became a silent no-op returning the type default
        // (0) instead of the summed value. Nothing threw.
        var (type, table, field, filters) = Read(CalcFormula(
            """sum("Sales Line".Amount where("Document No."=field("Code"), Description=filter('A;B')))"""));

        Assert.Equal("sum", type);
        Assert.Equal("Sales Line", table);
        Assert.Equal("Amount", field);
        // filter(...) conditions are excluded, exactly as RxCalcFilter excluded them.
        Assert.Equal(new[] { ("Document No.", "Code") }, filters);
    }

    [Fact]
    public void CalcFormula_CountWithoutAFieldPart_ParsesTableOnly()
    {
        // count/exist are a different node type (TableCalculationFormulaSyntax) carrying a table
        // and no field. A walk that assumed the qualified Table.Field shape would drop these.
        var (type, table, field, filters) = Read(CalcFormula(
            """count("Sales Line" where("Document No."=field("Code")))"""));

        Assert.Equal("count", type);
        Assert.Equal("Sales Line", table);
        Assert.Null(field);
        Assert.Equal(new[] { ("Document No.", "Code") }, filters);
    }

    [Fact]
    public void CalcFormula_UnquotedTableName_Parses()
    {
        // Regression guard for the bug documented at AlSourceParser.cs:100-108 — an unquoted
        // table name silently failed the old pattern and CalcFields() returned 0.
        var (type, table, field, _) = Read(CalcFormula(
            """lookup(PageworksLine.TargetTableNo where(Code=field("Entry No.")))"""));

        Assert.Equal("lookup", type);
        Assert.Equal("PageworksLine", table);
        Assert.Equal("TargetTableNo", field);
    }

    [Fact]
    public void CalcFormula_SignedFormula_IsRefusedRatherThanParsedWithoutItsSign()
    {
        // Negative direction. ParsedCalcFormula cannot carry the sign, so returning a formula
        // here would mean a FlowField silently computing +sum where AL wrote -sum. Refusing
        // preserves the old behaviour (the anchored regex never matched a leading sign).
        var parsed = typeof(AlRunner.Patches.RecordPatches)
            .GetMethod("TryParseCalcFormula", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { """CalcFormula = -sum("Sales Line".Amount);""" });

        Assert.Null(parsed);
    }

    [Fact]
    public void UnparseableSource_IsIgnoredRatherThanThrowing()
    {
        // TryParseTableFile is fed arbitrary .al text — pages, codeunits, and AL sliced out of
        // dependency .app archives. It must never throw on input it does not understand.
        var parse = RecordPatchesType.GetMethod("TryParseTableFile",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        parse.Invoke(null, new object[] { "this is not AL at all { { {" });
        parse.Invoke(null, new object[] { "" });
        parse.Invoke(null, new object[] { "page 50100 P { layout { area(content) { } } }" });

        Assert.False(ParsedTables.Contains(TableId));
    }
}
