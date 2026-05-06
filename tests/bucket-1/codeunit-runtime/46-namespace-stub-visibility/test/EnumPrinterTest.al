// Issue #1589: A namespace-aware test codeunit that imports MySales.Document
// must resolve the unqualified "Print Option" enum declared in that namespace.
// Before the fix, auto-stubs were emitted at global root — an AL0118 error
// would fire when the consumer tried to use the enum via a using directive.

namespace MySales.Tests;

using MySales.Document;

codeunit 50542 "Print Option Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure DefaultOptionIsNone()
    var
        Helper: Codeunit "Print Option Helper";
        Opt: Enum "Print Option";
    begin
        // [GIVEN] A default enum value (ordinal 0)
        // [WHEN] GetDefault is called
        Opt := Helper.GetDefault();
        // [THEN] the ordinal must be 0 (None)
        Assert.AreEqual(0, Opt.AsInteger(), 'Default option must be None (ordinal 0)');
    end;

    [Test]
    procedure FinalOptionOrdinalIsTwo()
    var
        Helper: Codeunit "Print Option Helper";
        Opt: Enum "Print Option";
    begin
        // [GIVEN] The Final enum value
        Opt := Helper.GetFinal();
        // [THEN] its ordinal must be 2
        Assert.AreEqual(2, Helper.GetOrdinal(Opt), 'Final option ordinal must be 2');
    end;

    [Test]
    procedure DefaultIsNotFinal()
    var
        Helper: Codeunit "Print Option Helper";
    begin
        // [GIVEN/WHEN] default and final enum values differ
        // [THEN] their ordinals are different (negative: not equal to 2)
        Assert.AreNotEqual(2, Helper.GetDefault().AsInteger(), 'Default option must not be Final');
    end;
}
