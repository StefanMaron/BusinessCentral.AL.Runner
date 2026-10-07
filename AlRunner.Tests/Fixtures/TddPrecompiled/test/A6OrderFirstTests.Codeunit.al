/// <summary>
/// One of two files calling the same missing member with shapes that disagree (here an Integer
/// result, in A7 a Boolean one). This file sorts first, so its call decides the stub, although its
/// call sits further down the file than A7's: the order is file, then position, never position alone.
/// </summary>
codeunit 65328 "Precompiled Order First"
{
    Subtype = Test;
    TestPermissions = Disabled;

    // Padding, so that this file's call has a later offset than the call in A7.
    // Padding, so that this file's call has a later offset than the call in A7.
    // Padding, so that this file's call has a later offset than the call in A7.
    // Padding, so that this file's call has a later offset than the call in A7.
    // Padding, so that this file's call has a later offset than the call in A7.
    // Padding, so that this file's call has a later offset than the call in A7.

    [Test]
    procedure DisagreeingShapes_TheFirstFileDecides()
    var
        Points: Codeunit "Precompiled Points";
        Count: Integer;
    begin
        Count := Points.Pick(1);
    end;
}
