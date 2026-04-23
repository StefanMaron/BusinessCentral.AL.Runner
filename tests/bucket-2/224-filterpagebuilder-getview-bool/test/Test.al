// Tests for issue #xxx — FilterPageBuilder.GetView(caption, useNames: Boolean).
//
// BC emits ALGetView(NavText, bool) for the two-arg GetView form. Exercises the fix
// that adds the missing overloads to MockFilterPageBuilder.
codeunit 62233 "FPBGetViewTest"
{
    Subtype = Test;

    var Assert: Codeunit Assert;

    // Positive: GetView with two args round-trips the stored view.
    // A no-op stub would return empty string, so checking the exact non-empty result
    // proves the round-trip works.
    [Test]
    procedure GetView_TwoArgs_RoundTripsView()
    var
        Src: Codeunit FPBGetViewSrc;
        FilterIn: Text[2048];
        FilterOut: Text[2048];
    begin
        FilterIn := 'WHERE(No.=FILTER(A..Z))';
        FilterOut := Src.RoundTripView(50000, FilterIn);
        Assert.AreEqual(FilterIn, FilterOut, 'GetView(caption, false) should return the stored view');
    end;

    // Positive: GetView with two args on an empty filter returns empty string.
    [Test]
    procedure GetView_TwoArgs_EmptyFilter_ReturnsEmpty()
    var
        Src: Codeunit FPBGetViewSrc;
        FilterOut: Text[2048];
    begin
        FilterOut := Src.RoundTripView(50000, '');
        Assert.AreEqual('', FilterOut, 'GetView with empty filter should return empty string');
    end;

    // Positive: one-arg GetView still works after adding the two-arg overload.
    [Test]
    procedure GetView_OneArg_StillWorks()
    var
        Src: Codeunit FPBGetViewSrc;
        Result: Text;
    begin
        Result := Src.GetViewOneArg(50000, 'WHERE(No.=FILTER(1..9))');
        Assert.AreEqual('WHERE(No.=FILTER(1..9))', Result, 'One-arg GetView should still return stored view');
    end;

    // Negative: GetView with two args when no view was set returns empty string.
    [Test]
    procedure GetView_TwoArgs_NoView_ReturnsEmpty()
    var
        Src: Codeunit FPBGetViewSrc;
        FilterOut: Text[2048];
    begin
        FilterOut := Src.RoundTripView(50001, '');
        Assert.AreEqual('', FilterOut, 'No view set should return empty string');
    end;
}
