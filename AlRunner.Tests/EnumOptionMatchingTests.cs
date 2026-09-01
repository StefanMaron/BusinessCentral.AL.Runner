// EnumOptionMatchingTests — AlEnumOptionMetadata must match option text the way BC's own
// NCLEnumMetadata does.
//
// This is a RUNNER-MECHANISM test. The BC-behaviour claim underneath it — that
// `Rec.SetFilter(<enum field>, '<>''''')` is a legal filter, which Base App's own
// codeunit 5055 "CustVendBank-Update" relies on — is a statement about AL/BC and belongs
// upstream in the al-language corpus. What this file pins is that OUR NCLOptionMetadata
// subclass answers the same way BC's does, without booting the engine.
//
// BC's bodies, decompiled from Microsoft.Dynamics.Nav.Ncl 28.1:
//
//   NCLEnumMetadata.GetIndexFromOption(option):
//       int r = StringHelper.FindStringInStringArrayUsingCurrentCulture(Options, option);
//       if (r != -1) return OrdinalValues[r];
//       if (int.TryParse(option, out r)) return r;
//       return -1;
//
//   StringHelper.FindStringInStringArrayUsingCulture(ci, strings, searchTarget):
//       searchTarget = searchTarget.Trim();
//       for each i: if (strings[i].Length > 0
//                       && string.Compare(strings[i].Trim(), searchTarget, true, ci) == 0)
//                       return i;
//       return -1;
//
//   NCLEnumMetadata.GetIndexFromCaption(caption): trims the caption, compares it
//       case-insensitively against each value's caption (falling back to the member name),
//       also trimmed, and returns OrdinalValues[i]. No Length > 0 guard on that side.
//
// Three properties follow, and our old `string.Equals(..., StringComparison.Ordinal)`
// override had none of them: the comparison trims, it ignores case, and a member whose
// name is only whitespace (AL's `value(0; " ")`, the blank enum member) is therefore
// reachable by the empty string.
//
// RED/GREEN: reverting GetIndexFromOption/GetIndexFromCaption to the ordinal string.Equals
// loop makes every case below except NotAMember and TrulyEmptyMemberName fail.
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class EnumOptionMatchingTests
{
    // The shape of Base App enum 5057 "Contact Business Relation Link To Table": its member 0
    // is named " " (a single space), which is how AL spells a blank enum member.
    private static AlEnumOptionMetadata BlankFirstMember()
        => new(
            name: "Contact Business Relation Link To Table",
            id: 5057,
            options: new[] { " ", "Customer", "Vendor", "Bank Account", "Employee" },
            indexes: new[] { 0, 1, 2, 3, 4 });

    [Fact]
    public void GetIndexFromOption_EmptyString_ResolvesToTheBlankMember()
        => Assert.Equal(0, BlankFirstMember().GetIndexFromOption(string.Empty));

    [Fact]
    public void GetIndexFromCaption_EmptyString_ResolvesToTheBlankMember()
        => Assert.Equal(0, BlankFirstMember().GetIndexFromCaption(string.Empty));

    [Fact]
    public void GetIndexFromOption_DiffersInCaseAndPadding_StillResolves()
        => Assert.Equal(3, BlankFirstMember().GetIndexFromOption("  bank account "));

    [Fact]
    public void GetIndexFromOption_NotAMember_ReturnsMinusOne()
        => Assert.Equal(-1, BlankFirstMember().GetIndexFromOption("Shareholder"));

    // BC's Length > 0 guard: a member whose name is the empty string is never matched by
    // text at all (only by its ordinal). Keeping the guard is what makes " " and "" behave
    // differently, which is the distinction this whole fix turns on.
    [Fact]
    public void GetIndexFromOption_TrulyEmptyMemberName_IsNotMatchedByTheEmptyString()
    {
        var meta = new AlEnumOptionMetadata(
            name: "Empty First Member", id: 5057001,
            options: new[] { string.Empty, "Customer" }, indexes: new[] { 0, 1 });

        Assert.Equal(-1, meta.GetIndexFromOption(string.Empty));
    }

    // The return value is the ORDINAL, not the array index — BC returns OrdinalValues[r].
    [Fact]
    public void GetIndexFromOption_SparseOrdinals_ReturnsTheOrdinalNotTheArrayIndex()
    {
        var meta = new AlEnumOptionMetadata(
            name: "Sparse", id: 5057002,
            options: new[] { " ", "Customer" }, indexes: new[] { 0, 7 });

        Assert.Equal(7, meta.GetIndexFromOption("Customer"));
    }

    // BC's numeric fallback survives, and it takes the text as an ordinal verbatim.
    [Fact]
    public void GetIndexFromOption_NumericText_FallsBackToTheOrdinal()
        => Assert.Equal(4, BlankFirstMember().GetIndexFromOption("4"));

    // A declared Caption wins over the member name on the caption side, and does NOT
    // leak into the option side — the two tables stay distinct.
    [Fact]
    public void GetIndexFromCaption_DeclaredCaption_WinsOverTheMemberName()
    {
        var meta = new AlEnumOptionMetadata(
            name: "Captioned", id: 5057003,
            options: new[] { " ", "Block" }, indexes: new[] { 0, 1 },
            implementations: null, captions: new[] { " ", "Blocks" });

        Assert.Equal(1, meta.GetIndexFromCaption("blocks"));
        Assert.Equal(-1, meta.GetIndexFromOption("Blocks"));
    }
}
