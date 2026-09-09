// DataItemLinkTopLevelSplitTests — the string-level half of #3572, pinned directly.
//
// RecordPatches.ParseDataItemLinks builds real Types.Metadata.MetaQueryDataItemLink instances,
// so calling it needs BC's assemblies loaded and the builder's reflection primed. What decided
// the DEFECT, though, is neither of those: it is where the property text gets split. The old
// code located the '=' and the '.' with a bare IndexOf over the whole property; the fix routes
// both through the quote-aware helpers this file exercises.
//
// So these tests take the two helpers ParseDataItemLinks/ParseDataItemLink actually call —
// SplitTopLevelCommas and TopLevelIndexOf, private statics on RecordPatches, reached by
// reflection the same way BcShapeGapConventionTests and BcAppPathsResetTests reach theirs —
// and pin them against the exact property strings BC's compiler emits. No BC artifacts, no
// fixture bundle, milliseconds.
//
// WHAT THESE ARE, AND ARE NOT. SplitTopLevelCommas and TopLevelIndexOf both PREDATE #3572 —
// the fix routes ParseDataItemLink through them, it does not change them. So these are
// characterization tests of the helpers the fix now relies on, plus one regression pin
// (TheOldWholePropertyParse_ProducesTheBogusSourceFieldName) on the algorithm that was
// replaced. Measured: reverting ParseDataItemLink to the old bare IndexOf leaves all six
// GREEN. They are not a RED -> GREEN on this change and must not be read as one.
//
// What they buy is the thing the end-to-end suite cannot say cheaply: WHY the old parse was
// wrong, in one assertion, in 18ms, without BC artifacts. The RED -> GREEN lives in
// tests/runner-extras/query-dataitem-filter-precompiled-dep, which runs the query for real on
// a precompiled dependency (NullReferenceException in NavQuery.ValidateTablesNotVirtual
// before the fix), and the BC-behaviour claim is adjudicated by corpus PR #303 on a real
// service tier.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class DataItemLinkTopLevelSplitTests
{
    private static MethodInfo Method(string name)
        => typeof(RecordPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"test setup: RecordPatches.{name} not found");

    private static List<string> SplitTopLevelCommas(string s)
        => (List<string>)Method("SplitTopLevelCommas").Invoke(null, new object?[] { s })!;

    private static int TopLevelIndexOf(string s, char c)
        => (int)Method("TopLevelIndexOf").Invoke(null, new object?[] { s, c })!;

    /// <summary>
    /// The exact value BC 28.1's compiler emits into SymbolReference.json for
    /// <c>DataItemLink = "Header No." = Hdr."No.", "Variant Code" = Hdr."Variant Code";</c>
    /// — read back verbatim from the runner's own <c>.query-symbols.json</c> sidecar, not
    /// hand-written, so a spelling the compiler does not produce cannot make these pass.
    /// </summary>
    private const string CompositeLink =
        "\"Header No.\" = Hdr.\"No.\", \"Variant Code\" = Hdr.\"Variant Code\"";

    /// <summary>
    /// #3572's root cause, stated as an assertion. The property carries TWO equalities and must
    /// split into exactly two — a comma inside a quoted identifier must not split, and the
    /// separating comma must.
    /// </summary>
    [Fact]
    public void SplitTopLevelCommas_MultiFieldDataItemLink_YieldsOneEqualityPerField()
    {
        var parts = SplitTopLevelCommas(CompositeLink);

        Assert.Equal(2, parts.Count);
        Assert.Equal("\"Header No.\" = Hdr.\"No.\"", parts[0].Trim());
        Assert.Equal("\"Variant Code\" = Hdr.\"Variant Code\"", parts[1].Trim());
    }

    /// <summary>
    /// Negative control: a single-field link — the shape every DataItemLink in the corpus had
    /// before #3572 — must still yield exactly one part. A splitter that over-split would break
    /// every existing query while making the test above pass.
    /// </summary>
    [Fact]
    public void SplitTopLevelCommas_SingleFieldDataItemLink_YieldsOnePart()
    {
        var parts = SplitTopLevelCommas("\"Customer No.\" = Customer.\"No.\"");

        Assert.Single(parts);
        Assert.Equal("\"Customer No.\" = Customer.\"No.\"", parts[0].Trim());
    }

    /// <summary>
    /// A comma INSIDE a quoted AL identifier is not a separator. AL permits it, and a naive
    /// <c>Split(',')</c> would cut the field name in half — the "no naïve comma splitting"
    /// requirement #3571 states, which this shares with DataItemTableFilter.
    /// </summary>
    [Fact]
    public void SplitTopLevelCommas_CommaInsideAQuotedFieldName_DoesNotSplit()
    {
        var parts = SplitTopLevelCommas("\"Amount, Total\" = Hdr.\"No.\", \"B\" = Hdr.\"C\"");

        Assert.Equal(2, parts.Count);
        Assert.Equal("\"Amount, Total\" = Hdr.\"No.\"", parts[0].Trim());
        Assert.Equal("\"B\" = Hdr.\"C\"", parts[1].Trim());
    }

    /// <summary>
    /// The other half of the old parse. Within ONE equality the '=' must be located outside
    /// quotes: the old code's bare IndexOf is correct here only because no field name in the
    /// fixtures happened to contain '='.
    /// </summary>
    [Fact]
    public void TopLevelIndexOf_EqualsInsideAQuotedFieldName_IsNotTheSeparator()
    {
        const string equality = "\"A=B\" = Hdr.\"No.\"";

        var eq = TopLevelIndexOf(equality, '=');

        // Not index 2, which is the '=' inside "A=B".
        Assert.Equal(6, eq);
        Assert.Equal("\"A=B\"", equality[..eq].Trim());
        Assert.Equal("Hdr.\"No.\"", equality[(eq + 1)..].Trim());
    }

    /// <summary>
    /// The '.' that separates the source dataitem from its field must likewise be found outside
    /// quotes. This is the one that actually bit: <c>"No."</c> ends in a dot and appears in
    /// essentially every link, so on the composite property a bare IndexOf over the un-split
    /// text produced the source field
    /// <c>No.", "Variant Code" = Hdr."Variant Code</c> — a name resolving to nothing, which
    /// made the parse return null and the whole MetaQuery build be abandoned. (The helper
    /// itself is unchanged by #3572; what changed is that ParseDataItemLink now asks it.)
    /// </summary>
    [Fact]
    public void TopLevelIndexOf_DotSeparatingTheSourceDataItem_IgnoresDotsInsideQuotes()
    {
        var rhs = "Hdr.\"No.\"";

        var dot = TopLevelIndexOf(rhs, '.');

        Assert.Equal(3, dot);
        Assert.Equal("Hdr", rhs[..dot].Trim());
        Assert.Equal("\"No.\"", rhs[(dot + 1)..].Trim());
    }

    /// <summary>
    /// The whole-property misread, pinned as a regression: applying the OLD algorithm (bare
    /// IndexOf for '=' then for '.', over the un-split property) to the real compiler value
    /// reproduces the exact bogus source-field name from #3572, while the fixed path does not.
    /// Both algorithms are spelled out here rather than called, so this stays true whatever
    /// ParseDataItemLink later does — it documents why the shape was wrong, and would fail if
    /// someone "simplified" the split back to the naive form believing it equivalent.
    /// </summary>
    [Fact]
    public void TheOldWholePropertyParse_ProducesTheBogusSourceFieldName()
    {
        // OLD: locate '=' and '.' in the whole property, ignoring quotes and commas.
        var eq = CompositeLink.IndexOf('=');
        var rhs = CompositeLink[(eq + 1)..].Trim();
        var dot = rhs.IndexOf('.');
        var oldSourceField = rhs[(dot + 1)..].Trim().Trim('"');

        Assert.Equal("No.\", \"Variant Code\" = Hdr.\"Variant Code", oldSourceField);

        // NEW: split first, then locate both separators at top level within one equality.
        var first = SplitTopLevelCommas(CompositeLink)[0].Trim();
        var eqNew = TopLevelIndexOf(first, '=');
        var rhsNew = first[(eqNew + 1)..].Trim();
        var dotNew = TopLevelIndexOf(rhsNew, '.');
        var newSourceField = rhsNew[(dotNew + 1)..].Trim().Trim('"');

        Assert.Equal("No.", newSourceField);
    }
}
