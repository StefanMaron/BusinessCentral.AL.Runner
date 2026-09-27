// ReportTableViewQuotingTests — #2305, the DataItemTableView half.
//
// BC applies a report data item's DataItemTableView with NavRecord.ALSetView
// (DataItemIterator.ApplyDataItemTableViewAndRequestFormFilters), and TableViewParser's
// grammar, decompiled from Ncl 28.1, uses a DIFFERENT quote character per clause:
//
//     SORTING field names   ReadValue('"', ",)")
//     WHERE field names     ReadValue('"', "=")
//     CONST(...) / FIELD(...)  ReadValueOrEmpty('"', ")")
//     FILTER(...)           ReadValueOrEmpty('\'', "'()")     ← single quote
//
// FILTER's body is a filter expression, so it goes through the same grammar as SetFilter,
// where `"` is an ordinary character. Everything else reads AL's own double quotes already.
// So exactly one part of the string is rewritten and the rest must survive byte for byte.
//
// The end-to-end proof that this reaches the cached report metadata is in
// BcAppSymbolCacheReportTests.Reports_AreReadFromANestedNamespace_WithCaptionAndDataItemTree.
// These cases pin the grammar rules that one shape cannot reach: a field NAMED "Date Filter",
// a member name carrying its own parentheses, and a view with nothing to convert.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class ReportTableViewQuotingTests
{
    [Fact]
    public void FilterBody_QuotedMember_IsReQuotedForTheFilterGrammar()
        => Assert.Equal(
            """sorting("Vendor Ledger Entry No.","Posting Date") where("Entry Type" = filter(<> 'Initial Entry'))""",
            RecordPatches.TableViewText(
                """sorting("Vendor Ledger Entry No.","Posting Date") where("Entry Type" = filter(<> "Initial Entry"))"""));

    [Fact]
    public void ConstBody_KeepsItsDoubleQuotes()
        // CONST reads with '"' — converting it too would break what already works.
        => Assert.Equal(
            """where("Document Type" = const("Credit Memo"))""",
            RecordPatches.TableViewText("""where("Document Type" = const("Credit Memo"))"""));

    [Fact]
    public void FieldNamedDateFilter_IsNotMistakenForAFilterCall()
        // The keyword scan must skip quoted identifiers, or the `Filter` inside this field
        // name starts a conversion in the middle of the name.
        => Assert.Equal(
            """where("Date Filter" = field("Date Filter"))""",
            RecordPatches.TableViewText("""where("Date Filter" = field("Date Filter"))"""));

    [Fact]
    public void MemberNameWithParentheses_DoesNotEndTheFilterCallEarly()
        // `Payment Discount (VAT Excl.)` carries a balanced pair of its own; a naive scan for
        // the closing ')' stops inside the name and truncates the filter.
        => Assert.Equal(
            """where("Entry Type" = filter('Payment Discount (VAT Excl.)'|'Payment Tolerance'))""",
            RecordPatches.TableViewText(
                """where("Entry Type" = filter("Payment Discount (VAT Excl.)"|"Payment Tolerance"))"""));

    [Fact]
    public void ViewWithNoQuotedIdentifier_IsReturnedUnchanged()
        => Assert.Equal(
            "sorting(Number) where(Number = filter(1 ..))",
            RecordPatches.TableViewText("sorting(Number) where(Number = filter(1 ..))"));

    // #4668. A symbol file carries the view as the AL source wrote it, line breaks and
    // indentation included, and TableViewParser pads its clauses with `[ ]*` only — so a
    // view split over lines is refused whole ("Invalid expression of type: table view").
    // Base Application report 302's FilterItem, verbatim from the 28.1 symbol file:
    [Fact]
    public void MultiLineView_LineBreakAndIndentation_CollapseToOneSpace()
        => Assert.Equal(
            """sorting("No.") where(Type = const(Inventory))""",
            RecordPatches.TableViewText(
                "sorting(\"No.\")\r\n                                where(Type = const(Inventory))"));

    // Report 302's SalesOrderLine: breaks BETWEEN where-entries, and a filter(...) body that
    // must still be rewritten for the filter grammar after the collapse.
    [Fact]
    public void MultiLineWhere_EntriesOnSeparateLines_AreJoinedAndTheFilterBodyStillConverts()
        => Assert.Equal(
            """sorting("Document Type", "Document No.") where("Document Type" = const(Order), "Drop Shipment" = const(false), "Outstanding Qty. (Base)" = filter(<> 0))""",
            RecordPatches.TableViewText(
                "sorting(\"Document Type\", \"Document No.\")\r\n                                    where(\"Document Type\" = const(Order),\r\n                                        \"Drop Shipment\" = const(false),\r\n                                        \"Outstanding Qty. (Base)\" = filter(<> 0))"));

    // No quoted identifier at all: the early exit for "nothing to convert" must not also skip
    // the line-break collapse. A bare LF and a tab are line breaks to AL too.
    [Fact]
    public void MultiLineView_WithNoQuotedIdentifier_IsStillCollapsed()
        => Assert.Equal(
            "sorting(Status) where(Status = const(Released))",
            RecordPatches.TableViewText("sorting(Status)\n\twhere(Status = const(Released))"));

    // A DataItemLink goes through the same function and the same parser (RecordDataItemLink).
    [Fact]
    public void MultiLineDataItemLink_IsCollapsed()
        => Assert.Equal(
            "\"Source Type\" = field(\"Source Type\"), \"Source ID\" = field(\"No.\")",
            RecordPatches.TableViewText(
                "\"Source Type\" = field(\"Source Type\"),\r\n                               \"Source ID\" = field(\"No.\")"));

    // Only whitespace runs that contain a line break or a tab are touched; a single-line view
    // keeps its own spacing byte for byte, inside quoted identifiers too.
    [Fact]
    public void SingleLineView_KeepsItsSpacingExactly()
        => Assert.Equal(
            """sorting("No.")  where("Search  Name" = const(A))""",
            RecordPatches.TableViewText("""sorting("No.")  where("Search  Name" = const(A))"""));

    // In a view that does break lines, a space-only run elsewhere is still left as written:
    // only the runs that carry a break are rewritten.
    [Fact]
    public void MultiLineView_SpaceOnlyRunsElsewhere_AreKept()
        => Assert.Equal(
            "sorting(Code)  where(Code = const(A), Kind = const(B))",
            RecordPatches.TableViewText("sorting(Code)  where(Code = const(A),\r\n    Kind = const(B))"));

    // A tab INSIDE a quoted value is part of the value, not layout, while the line break
    // between the clauses is still collapsed.
    [Fact]
    public void TabInsideAQuotedValue_IsKept()
        => Assert.Equal(
            "sorting(Code) where(Code = const(\"A\tB\"), Name = filter('C\tD'))",
            RecordPatches.TableViewText("sorting(Code)\r\n  where(Code = const(\"A\tB\"), Name = filter('C\tD'))"));

    [Fact]
    public void NullAndEmptyViews_AreLeftAlone()
    {
        Assert.Null(RecordPatches.TableViewText(null));
        Assert.Equal("", RecordPatches.TableViewText(""));
    }
}
