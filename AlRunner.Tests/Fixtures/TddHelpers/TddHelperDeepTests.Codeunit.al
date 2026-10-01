/// <summary>
/// #5147: paths to a generated procedure longer than one call — two helpers deep, through a
/// pair of mutually recursive helpers, and through a handler the test names in
/// [HandlerFunctions]. Each test must be annotated with the stub it reaches.
/// </summary>
codeunit 65055 "Tdd Helper Deep Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Tdd Helper Assert";

    [Test]
    procedure TwoHelpersDeep_ReachesGeneratedStub()
    begin
        Assert.AreEqual(0, HopA(), 'an empty generated stub returns 0');
    end;

    [Test]
    procedure MutualRecursion_ReachesGeneratedStub()
    begin
        Assert.AreEqual(0, Ping(2), 'an empty generated stub returns 0');
    end;

    [Test]
    [HandlerFunctions('ConfirmCountsHandler')]
    procedure ViaHandlerFunction_ReachesGeneratedStub()
    begin
        Assert.AreEqual(1, ConfirmAsInteger(), 'the handler answered yes');
    end;

    local procedure HopA(): Integer
    begin
        exit(HopB());
    end;

    local procedure HopB(): Integer
    var
        Target: Codeunit "Tdd Helper Target Cu";
        Result: Integer;
    begin
        Result := Target.CountDeep(5);
        exit(Result);
    end;

    local procedure Ping(N: Integer): Integer
    begin
        if N > 0 then
            exit(Pong(N - 1));
        exit(0);
    end;

    local procedure Pong(N: Integer): Integer
    var
        Target: Codeunit "Tdd Helper Target Cu";
        Result: Integer;
    begin
        Result := Target.CountCycle(N);
        exit(Result + Ping(N));
    end;

    local procedure ConfirmAsInteger(): Integer
    begin
        if Confirm('Proceed?') then
            exit(1);
        exit(0);
    end;

    [ConfirmHandler]
    procedure ConfirmCountsHandler(Question: Text[1024]; var Reply: Boolean)
    var
        Target: Codeunit "Tdd Helper Target Cu";
        Result: Integer;
    begin
        Result := Target.CountHandled(1);
        Reply := Result = 0;
    end;
}
