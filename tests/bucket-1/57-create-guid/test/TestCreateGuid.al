codeunit 56501 "Test Create Guid"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CreateGuid_ReturnsNonEmptyGuid()
    var
        G: Guid;
        EmptyGuid: Guid;
    begin
        G := CreateGuid();
        Assert.IsFalse(IsNullGuid(G), 'CreateGuid() must return a non-empty GUID');
    end;

    [Test]
    procedure CreateGuid_TwoCallsReturnDifferentValues()
    var
        G1: Guid;
        G2: Guid;
    begin
        G1 := CreateGuid();
        G2 := CreateGuid();
        Assert.AreNotEqual(G1, G2, 'Two CreateGuid() calls must return different GUIDs');
    end;

    [Test]
    procedure CreateSequentialGuid_ReturnsNonEmptyGuid()
    var
        G: Guid;
    begin
        G := CreateSequentialGuid();
        Assert.IsFalse(IsNullGuid(G), 'CreateSequentialGuid() must return a non-empty GUID');
    end;

    [Test]
    procedure CreateGuid_ViaHelper_ReturnsNonEmpty()
    var
        Helper: Codeunit "Guid Helper";
        G: Guid;
    begin
        G := Helper.GetNewGuid();
        Assert.IsFalse(IsNullGuid(G), 'CreateGuid() via codeunit helper must return non-empty GUID');
    end;

    [Test]
    procedure CreateSequentialGuid_ViaHelper_ReturnsNonEmpty()
    var
        Helper: Codeunit "Guid Helper";
        G: Guid;
    begin
        G := Helper.GetNewSequentialGuid();
        Assert.IsFalse(IsNullGuid(G), 'CreateSequentialGuid() via codeunit helper must return non-empty GUID');
    end;
}
