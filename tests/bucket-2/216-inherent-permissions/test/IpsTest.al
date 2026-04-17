/// Tests proving that codeunits with the Permissions object property compile and
/// execute correctly in standalone mode (issue #978).
///
/// The BC compiler emits 'protected override NavPermissionList InherentPermissionsList'
/// in codeunit classes that declare a Permissions property (in newer BC versions).
/// The RoslynRewriter must strip that member because AlScope has no virtual
/// InherentPermissionsList to override (CS0115 otherwise).
///
/// Test strategy:
///   Echo — round-trips a Text value; proves the codeunit body executes.
///   Add — arithmeti; proves the inner-scope method survives the rewrite.
codeunit 97906 "IPS Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "IPS Src";

    [Test]
    procedure Echo_ReturnsSameText()
    begin
        // [GIVEN] A codeunit with InherentPermissions attribute
        // [WHEN]  Echo() is called
        // [THEN]  The input text is returned unchanged
        Assert.AreEqual('hello', Src.Echo('hello'),
            'Echo must return the input text');
    end;

    [Test]
    procedure Echo_EmptyString()
    begin
        Assert.AreEqual('', Src.Echo(''),
            'Echo must return empty string for empty input');
    end;

    [Test]
    procedure Add_ReturnsSumOfTwoIntegers()
    begin
        // [GIVEN] Two integers
        // [WHEN]  Add() is called
        // [THEN]  Their sum is returned
        Assert.AreEqual(7, Src.Add(3, 4),
            'Add must return 7 for inputs 3 and 4');
    end;

    [Test]
    procedure Add_NegativeNumbers()
    begin
        Assert.AreEqual(-1, Src.Add(-3, 2),
            'Add must handle negative numbers');
    end;
}
