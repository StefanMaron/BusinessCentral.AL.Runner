codeunit 60381 "TBL Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "TBL Src";

    [Test]
    procedure Truncate_KeepsPrefix()
    begin
        Assert.AreEqual('Hello', Src.Truncate('Hello World Extra', 5),
            'Length := 5 must truncate to "Hello"');
    end;

    [Test]
    procedure TruncateToZero_ReturnsEmpty()
    begin
        Assert.AreEqual('', Src.TruncateToZero('Hello'),
            'Length := 0 must clear the buffer');
    end;

    [Test]
    procedure LengthAfterSet_IsNewLength()
    begin
        Assert.AreEqual(3, Src.LengthAfterSet('Hello', 3),
            'Length after Length := 3 must be 3');
    end;

    [Test]
    procedure Truncate_DoesNotKeepFullLength_NegativeTrap()
    begin
        // Negative trap: if the setter is a no-op, the returned text
        // would still be the full input.
        Assert.AreNotEqual('Hello World Extra', Src.Truncate('Hello World Extra', 5),
            'Length setter must not be a no-op — full input must not be returned');
    end;
}
