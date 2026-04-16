codeunit 58401 "Test Database SessionId"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure SessionId_ReturnsPositiveInteger()
    var
        Id: Integer;
    begin
        // SessionId() must return a non-zero positive integer in the runner
        Id := SessionId();
        Assert.IsTrue(Id > 0, 'SessionId() must return a positive integer');
    end;

    [Test]
    procedure SessionId_IsStable_WithinSameContext()
    var
        Id1: Integer;
        Id2: Integer;
    begin
        // SessionId() must return the same value on consecutive calls
        Id1 := SessionId();
        Id2 := SessionId();
        Assert.AreEqual(Id1, Id2, 'SessionId() must return the same value on consecutive calls');
    end;
}
