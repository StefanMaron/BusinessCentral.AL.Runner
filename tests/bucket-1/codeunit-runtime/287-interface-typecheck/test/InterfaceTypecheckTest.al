codeunit 135004 "ITC Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;
        Src: Codeunit "ITC Source";
        BaseOnly: Codeunit "ITC BaseOnly";
        Extended: Codeunit "ITC Extended";

    [Test]
    procedure Is_BaseOnly_ReturnsFalse()
    var
        v: Interface ITC_Base;
    begin
        // [GIVEN] ITC_BaseOnly only implements ITC_Base
        // [WHEN] `v is ITC_Extended` is checked
        // [THEN] false is returned
        v := BaseOnly;
        Assert.IsFalse(Src.IsExtended(v), 'ITC_BaseOnly should not satisfy `is ITC_Extended`');
    end;

    [Test]
    procedure Is_Extended_ReturnsTrue()
    var
        v: Interface ITC_Base;
    begin
        // [GIVEN] ITC_Extended implements both ITC_Base and ITC_Extended
        // [WHEN] `v is ITC_Extended` is checked
        // [THEN] true is returned
        v := Extended;
        Assert.IsTrue(Src.IsExtended(v), 'ITC_Extended should satisfy `is ITC_Extended`');
    end;

    [Test]
    procedure As_Extended_ReturnsExtra()
    var
        v: Interface ITC_Base;
    begin
        // [GIVEN] ITC_Extended implements ITC_Extended
        // [WHEN] v is cast with `as ITC_Extended` and GetExtra() is called
        // [THEN] 99 is returned
        v := Extended;
        Assert.AreEqual(99, Src.AsExtendedGetExtra(v), 'GetExtra via `as ITC_Extended` must return 99');
    end;

    [Test]
    procedure As_BaseOnly_ThrowsOnBadCast()
    var
        v: Interface ITC_Base;
    begin
        // [GIVEN] ITC_BaseOnly does not implement ITC_Extended
        // [WHEN] v is cast with `as ITC_Extended`
        // [THEN] an error is thrown (InvalidCastException wrapped as AL runtime error)
        v := BaseOnly;
        asserterror Src.AsExtendedGetExtra(v);
    end;
}
