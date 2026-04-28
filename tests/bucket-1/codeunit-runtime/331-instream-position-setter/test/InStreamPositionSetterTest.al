codeunit 1320419 "InStream Position Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "InStream Position Src";

    [Test]
    procedure Position_Setter_ReadsFromOffset()
    begin
        Assert.AreEqual('World', Src.ReadFromPosition(),
            'Setting InStream.Position should move the read cursor');
    end;

    [Test]
    procedure Position_Setter_OutOfRange_Throws()
    begin
        asserterror Src.SetPositionOutOfRange();
        Assert.ExpectedError('outside the bounds of the stream');
    end;
}
