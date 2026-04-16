codeunit 56501 "Test Create Guid"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure EmptyGuid(): Guid
    var
        G: Guid;
    begin
        exit(G); // default Guid = all zeros
    end;

    [Test]
    procedure CreateGuid_ReturnsNonDefaultGuid()
    var
        G: Guid;
    begin
        G := CreateGuid();
        Assert.AreNotEqual(EmptyGuid(), G, 'CreateGuid() must return a non-default (non-zero) GUID');
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
    procedure CreateSequentialGuid_ReturnsNonDefaultGuid()
    var
        G: Guid;
    begin
        G := CreateSequentialGuid();
        Assert.AreNotEqual(EmptyGuid(), G, 'CreateSequentialGuid() must return a non-default GUID');
    end;

    [Test]
    procedure CreateGuid_ViaHelper_ReturnsNonDefault()
    var
        Helper: Codeunit "Guid Helper";
        G: Guid;
    begin
        G := Helper.GetNewGuid();
        Assert.AreNotEqual(EmptyGuid(), G, 'CreateGuid() via codeunit helper must return non-default GUID');
    end;

    [Test]
    procedure CreateSequentialGuid_ViaHelper_ReturnsNonDefault()
    var
        Helper: Codeunit "Guid Helper";
        G: Guid;
    begin
        G := Helper.GetNewSequentialGuid();
        Assert.AreNotEqual(EmptyGuid(), G, 'CreateSequentialGuid() via codeunit helper must return non-default GUID');
    end;
}
