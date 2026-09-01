// CalcFormulaSyntaxTests — RED→GREEN guard for #1708 (a signed FlowField formula) and
// #1709 (const()/filter() where-conditions).
//
// Both are the same failure class #1696 exists to remove: a SILENT WRONG VALUE. #1696 moved
// the parse onto BC's own AL syntax tree but deliberately left both behaviours alone, because
// changing either one changes what a FlowField COMPUTES rather than how it is parsed.
//
//   #1708  CalcFormula = -sum("Sales Line".Amount where(...));
//          `CalcFormulaFrom` refused the formula outright (`if (signText.Length > 0) return
//          null;`) because ParsedCalcFormula had nowhere to put the sign. The FlowField's
//          CalculationFormula stayed EmptyFormula, CalcFields() left the field at 0, and
//          nothing threw. 100 of the Base Application's 2055 CalcFormulas are signed
//          (95 `-sum`, 5 `-exist`).
//
//   #1709  CalcFormula = sum("Sales Line".Amount where(Open = const(true)));
//          `if (cond is not SimpleFieldExpressionSyntax) continue;` — so `Open = const(true)`
//          was dropped and the FlowField summed BOTH open and closed lines: a plausible wrong
//          number. The Base Application writes 1215 const and 285 filter conditions.
//
// Each where-condition is its own node type (SimpleFieldExpressionSyntax /
// ConstExpressionSyntax / FilterExpressionSyntax / the two FlowFilter forms) and the sign is a
// token on the formula node, so all of this is selected structurally.
//
// These tests cover the PARSE. That the parsed sign and conditions actually change the value
// CalcFields() computes is proved end to end by tests/runner-extras/calcformula-sign-and-filters.
//
// These drive the real parser (TryParseTableFile) by reflection, exactly like
// AlSourceParserSyntaxTreeTests, so they prove the parser produces the formula rather than
// proving a helper works in isolation.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class CalcFormulaSyntaxTests
{
    private const int TableId = 61897;

    private static readonly Type RecordPatchesType = typeof(RecordPatches);

    private static System.Collections.IDictionary ParsedTables =>
        (System.Collections.IDictionary)RecordPatchesType
            .GetField("_parsedTables", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    /// <summary>
    /// A two-field fixture table whose field 2 is the FlowField under test. Field 1 is the
    /// PK and doubles as the parent field a <c>field(...)</c> condition can point at.
    /// </summary>
    private static string Table(string calcFormula) => $$"""
        table {{TableId}} "Calc Formula Fixture"
        {
            fields
            {
                field(1; "Code"; Code[10]) { }
                field(2; Total; Decimal)
                {
                    FieldClass = FlowField;
                    CalcFormula = {{calcFormula}};
                }
            }

            keys
            {
                key(PK; "Code") { Clustered = true; }
            }
        }
        """;

    private static ParsedCalcFormula? ParseFormula(string calcFormula)
    {
        var parse = RecordPatchesType.GetMethod("TryParseTableFile",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        parse.Invoke(null, new object[] { Table(calcFormula) });
        Assert.True(ParsedTables.Contains(TableId), $"table {TableId} was not parsed at all");
        var table = (ParsedTable)ParsedTables[TableId]!;
        var field = table.Fields.Single(f => f.FieldId == 2);
        Assert.True(field.IsFlowField, "field 2 was not recognised as a FlowField");
        return field.CalcFormula;
    }

    private static ParsedCalcFormula ParseFormulaRequired(string calcFormula) =>
        ParseFormula(calcFormula)
        ?? throw new Xunit.Sdk.XunitException($"CalcFormula was refused: {calcFormula}");

    // ─── #1708 — the sign ────────────────────────────────────────────────────────────────

    [Fact]
    public void SignedSumFormula_CarriesItsSign()
    {
        try
        {
            var f = ParseFormulaRequired(
                """-sum("Sales Line".Amount where("Document No." = field("Code")))""");

            Assert.Equal("sum", f.FormulaType, ignoreCase: true);
            Assert.Equal("Sales Line", f.SourceTableName);
            Assert.Equal("Amount", f.SourceFieldName);
            Assert.True(f.Negated,
                "a `-sum(...)` formula must be carried as negated; dropping the sign turns a " +
                "visible 0 into a plausible wrong (positive) number");
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void UnsignedSumFormula_IsNotNegated()
    {
        // The other direction: Negated must come from the source, not be hard-coded.
        try
        {
            var f = ParseFormulaRequired(
                """sum("Sales Line".Amount where("Document No." = field("Code")))""");

            Assert.Equal("sum", f.FormulaType, ignoreCase: true);
            Assert.False(f.Negated);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void SignedCountFormula_CarriesItsSignAndHasNoSourceField()
    {
        // count/exist parse to TableCalculationFormulaSyntax — a different node type from
        // sum/lookup/min/max/average (FieldCalculationFormulaSyntax), with a Table instead
        // of a qualified Field. Both carry a Sign token.
        try
        {
            var f = ParseFormulaRequired(
                """-count("Sales Line" where("Document No." = field("Code")))""");

            Assert.Equal("count", f.FormulaType, ignoreCase: true);
            Assert.Equal("Sales Line", f.SourceTableName);
            Assert.Null(f.SourceFieldName);
            Assert.True(f.Negated);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    // ─── #1709 — const() and filter() conditions ─────────────────────────────────────────

    [Fact]
    public void ConstCondition_IsCarriedAsAConstFilter()
    {
        try
        {
            var f = ParseFormulaRequired(
                """sum("Sales Line".Amount where("Document No." = field("Code"), Open = const(true)))""");

            Assert.Equal(2, f.Filters.Count);

            var link = f.Filters[0];
            Assert.Equal(ParsedCalcFilterKind.Field, link.Kind);
            Assert.Equal("Document No.", link.SourceFieldName);
            Assert.Equal("Code", link.ParentFieldName);

            var open = f.Filters[1];
            Assert.Equal(ParsedCalcFilterKind.Const, open.Kind);
            Assert.Equal("Open", open.SourceFieldName);
            Assert.Equal("true", open.Value);
            Assert.Null(open.ParentFieldName);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void QuotedConstValue_IsUnquoted()
    {
        // AL quotes an option/code const value that needs it — `const("On Hold")`. The value
        // handed on must be the member text itself, because NCLMetaFilterConst evaluates it
        // against the source field's own type (option string / code), where a stray pair of
        // quotes matches nothing. Same rule as InitValue (#1674).
        try
        {
            var f = ParseFormulaRequired(
                """sum("Sales Line".Amount where(Status = const("On Hold")))""");

            var c = Assert.Single(f.Filters);
            Assert.Equal(ParsedCalcFilterKind.Const, c.Kind);
            Assert.Equal("Status", c.SourceFieldName);
            Assert.Equal("On Hold", c.Value);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void FilterCondition_IsCarriedAsAFilterFilterWithItsExpressionText()
    {
        try
        {
            var f = ParseFormulaRequired(
                """count("Sales Line" where(Status = filter(Open|Released)))""");

            var c = Assert.Single(f.Filters);
            Assert.Equal(ParsedCalcFilterKind.Filter, c.Kind);
            Assert.Equal("Status", c.SourceFieldName);
            // The filter expression text is passed through to BC's own filter parser, so it
            // must arrive intact — an alternation is a single expression, not three filters.
            Assert.Equal("Open|Released", c.Value);
            Assert.Null(c.ParentFieldName);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void RangeFilterCondition_KeepsItsRangeExpressionVerbatim()
    {
        // The expression text is handed to BC's own filter parser untouched — deliberately
        // NOT whitespace-normalised. An option member may legally contain spaces
        // (`filter(Initial Entry|Application)`), so collapsing whitespace would silently
        // rewrite the member being filtered on. AL's own spacing is what BC sees.
        try
        {
            var f = ParseFormulaRequired(
                """sum("Sales Line".Amount where("Line No." = filter(10000 .. 20000)))""");

            var c = Assert.Single(f.Filters);
            Assert.Equal(ParsedCalcFilterKind.Filter, c.Kind);
            Assert.Equal("Line No.", c.SourceFieldName);
            Assert.Equal("10000 .. 20000", c.Value);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void FieldCondition_StillLinksTheParentField()
    {
        // Regression guard: the field-to-field link is the form that already worked, and it
        // must keep its exact (unquoted, both sides) shape.
        try
        {
            var f = ParseFormulaRequired(
                """sum("Sales Line".Amount where("Document No." = field("Code")))""");

            var c = Assert.Single(f.Filters);
            Assert.Equal(ParsedCalcFilterKind.Field, c.Kind);
            Assert.Equal("Document No.", c.SourceFieldName);
            Assert.Equal("Code", c.ParentFieldName);
            Assert.Null(c.Value);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    // ─── Negative: forms that must NOT be invented ───────────────────────────────────────

    [Fact]
    public void FlowFilterForms_CarryTheirOwnModeFlags_NotAPlainFieldLink()
    {
        // #1716. `field(filter(X))`, `field(upperlimit(X))` and `field(upperlimit(filter(X)))`
        // ARE field links in BC's metadata — a MetaFilter of type FIELD — but each carries a
        // different pair of mode flags (MetaFilter.ValueIsFilter / .OnlyMaxLimit), and those
        // flags are the whole difference between "compare against the parent's value", "parse
        // the parent's value as a filter" and "keep only that filter's upper bound".
        //
        // Two ways to get this wrong, both silent: dropping the flags (the link becomes a
        // plain equality BC never wrote) and confusing the two forms with each other. The
        // parent field name must survive as well — without it there is nothing to read.
        try
        {
            var f = ParseFormulaRequired(
                """
                sum("Sales Line".Amount where("Posting Date" = field(upperlimit("Code")),
                                              "Account No." = field(filter("Code")),
                                              "Shipment Date" = field(upperlimit(filter("Code")))))
                """);

            Assert.Equal(3, f.Filters.Count);
            Assert.All(f.Filters, c =>
            {
                Assert.Equal(ParsedCalcFilterKind.Field, c.Kind);
                Assert.Equal("Code", c.ParentFieldName);
                Assert.Null(c.Value);
            });

            var upperLimit = f.Filters.Single(c => c.SourceFieldName == "Posting Date");
            Assert.True(upperLimit.OnlyMaxLimit);
            Assert.False(upperLimit.ValueIsFilter);

            var valueIsFilter = f.Filters.Single(c => c.SourceFieldName == "Account No.");
            Assert.True(valueIsFilter.ValueIsFilter);
            Assert.False(valueIsFilter.OnlyMaxLimit);

            var both = f.Filters.Single(c => c.SourceFieldName == "Shipment Date");
            Assert.True(both.ValueIsFilter);
            Assert.True(both.OnlyMaxLimit);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void PlainFieldLink_CarriesNeitherModeFlag()
    {
        // The negative direction of the test above: an ordinary `field(X)` must NOT come out
        // looking like a flow-filter form, or every existing FlowField would start reading
        // its parent's value as a filter expression.
        try
        {
            var f = ParseFormulaRequired(
                """sum("Sales Line".Amount where("Document No." = field("Code")))""");

            var c = Assert.Single(f.Filters);
            Assert.Equal(ParsedCalcFilterKind.Field, c.Kind);
            Assert.False(c.ValueIsFilter);
            Assert.False(c.OnlyMaxLimit);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    // ─── #2305 — an AL quoted identifier inside filter(...) ──────────────────────────────
    //
    // AL quotes an identifier with double quotes; BC's FILTER grammar quotes a literal with
    // SINGLE quotes and treats `"` as an ordinary character (FilterExpressionTokenizer.GetTokens
    // in Ncl 28.1 has a case for '\'' and none for '"'). So `filter("Initial Entry")` must reach
    // the runtime as `'Initial Entry'`. Carried through verbatim it arrives as the 15-character
    // literal `"Initial Entry"`, which is not an option member, and the FlowField throws.
    //
    // Converting rather than stripping is the point: several Base App members contain filter
    // metacharacters — `Payment Discount (VAT Excl.)` has parentheses, which the tokenizer reads
    // as grouping — so quotes cannot simply be removed the way ConstValueText removes them
    // (a const value is evaluated as a bare value, not parsed as an expression).

    [Fact]
    public void FilterCondition_QuotedOptionMember_BecomesASingleQuotedFilterLiteral()
    {
        try
        {
            var f = ParseFormulaRequired(
                """sum("Detailed Vendor Ledg. Entry".Amount where("Entry Type" = filter("Initial Entry")))""");

            var c = Assert.Single(f.Filters);
            Assert.Equal("Entry Type", c.SourceFieldName);
            Assert.Equal(ParsedCalcFilterKind.Filter, c.Kind);
            Assert.Equal("'Initial Entry'", c.Value);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void FilterCondition_MixedQuotedAndBareMembers_ConvertsOnlyTheQuotedOnes()
    {
        try
        {
            var f = ParseFormulaRequired(
                """sum("Sales Line".Amount where("Document Type" = filter(Invoice | "Credit Memo")))""");

            var c = Assert.Single(f.Filters);
            Assert.Equal("'Credit Memo'", c.Value.Replace("Invoice | ", ""));
            Assert.StartsWith("Invoice", c.Value);
            Assert.DoesNotContain('"', c.Value);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void FilterCondition_MemberNameWithParentheses_SurvivesAsAQuotedLiteral()
    {
        // The case that rules out stripping the quotes: unquoted, the tokenizer would read
        // `(` and `)` as grouping and `..` would bind to the wrong operand.
        try
        {
            var f = ParseFormulaRequired(
                """sum("Detailed Vendor Ledg. Entry".Amount where("Entry Type" = filter("Payment Discount" .. "Payment Discount (VAT Adjustment)")))""");

            var c = Assert.Single(f.Filters);
            Assert.Contains("'Payment Discount (VAT Adjustment)'", c.Value);
            Assert.DoesNotContain('"', c.Value);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void FilterCondition_BlankMember_BecomesAQuotedSpace()
    {
        // `filter(<> " ")` — the blank member of an option set, negated. The space must stay
        // inside quotes; bare, the tokenizer discards it as whitespace and the filter becomes
        // `<>` with no operand.
        try
        {
            var f = ParseFormulaRequired(
                """sum("Warehouse Activity Line".Quantity where("Activity Type" = filter(<> " ")))""");

            var c = Assert.Single(f.Filters);
            Assert.Contains("' '", c.Value);
            Assert.StartsWith("<>", c.Value);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void FilterCondition_UnquotedMembers_AreLeftExactlyAsWritten()
    {
        // The other direction: a filter with no AL quoting must come through untouched — the
        // conversion must be driven by the source text, not applied to every filter.
        try
        {
            var f = ParseFormulaRequired(
                """sum("Sales Line".Amount where(Status = filter(Open | Released)))""");

            var c = Assert.Single(f.Filters);
            Assert.Equal("Open | Released", c.Value);
        }
        finally { ParsedTables.Remove(TableId); }
    }

    [Fact]
    public void NonFormulaPropertyValue_YieldsNoFormula()
    {
        // A CalcFormula the AL parser cannot read as a calculation formula must leave the
        // field with NO formula at all. Returning a half-parsed one would be the silent
        // wrong value this whole file exists to prevent.
        try
        {
            Assert.Null(ParseFormula("'not a formula at all'"));
        }
        finally { ParsedTables.Remove(TableId); }
    }
}
