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

    [Fact]
    public void NullAndEmptyViews_AreLeftAlone()
    {
        Assert.Null(RecordPatches.TableViewText(null));
        Assert.Equal("", RecordPatches.TableViewText(""));
    }
}
