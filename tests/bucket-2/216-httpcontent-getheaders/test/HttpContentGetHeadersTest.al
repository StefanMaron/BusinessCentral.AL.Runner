codeunit 60441 "HCGH Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "HCGH Src";

    [Test]
    procedure GetHeaders_DoesNotThrow()
    begin
        Assert.IsTrue(Src.GetContentHeaders_DoesNotThrow(),
            'HttpContent.GetHeaders must be callable as a method');
    end;

    [Test]
    procedure GetHeaders_Then_Add_Contains()
    begin
        Assert.IsTrue(Src.GetContentHeaders_ThenAdd(),
            'Headers obtained via GetHeaders must be mutable — Add + Contains must work');
    end;
}
