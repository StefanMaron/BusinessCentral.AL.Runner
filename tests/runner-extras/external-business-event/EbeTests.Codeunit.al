namespace AlRunner.Extras.ExternalBusinessEvent;

codeunit 65663 "Ebe Tests"
{
    Subtype = Test;

    [Test]
    procedure RaiseReturnsAndTheAlAfterItRuns()
    var
        Publisher: Codeunit "Ebe Publisher";
    begin
        // [GIVEN] a procedure that counts a step, raises an external business event, counts again
        // [WHEN] it runs
        Publisher.RaiseBetweenSteps();
        // [THEN] the raise returned normally and the statement after it ran
        AssertSteps(2, Publisher.Steps());
    end;

    [Test]
    procedure RaiseWithEveryPayloadKindReturns()
    var
        Publisher: Codeunit "Ebe Publisher";
    begin
        Publisher.RaiseWithPayload();
        AssertSteps(2, Publisher.Steps());
    end;

    [Test]
    procedure RaiseTwiceInOneTransactionThenCommit()
    var
        Publisher: Codeunit "Ebe Publisher";
    begin
        // Delivery on a service tier is queued for after commit; the commit must not
        // surface anything either.
        Publisher.RaiseBetweenSteps();
        Publisher.RaiseBetweenSteps();
        Commit();
        AssertSteps(4, Publisher.Steps());
    end;

    [Test]
    procedure GuardedCodeunitRunContainingTheRaiseSucceeds()
    begin
        ClearLastError();
        if not Codeunit.Run(Codeunit::"Ebe Run Target") then
            Error('Codeunit.Run returned false; last error: ''%1''', GetLastErrorText());
        if GetLastErrorText() <> '' then
            Error('Expected no last error after a successful Codeunit.Run, got ''%1''', GetLastErrorText());
    end;

    [Test]
    procedure UnrelatedErrorAfterTheRaiseStillPropagates()
    var
        Publisher: Codeunit "Ebe Publisher";
    begin
        asserterror Publisher.RaiseThenFail();
        ExpectedError('Ebe unrelated failure after the raise');
        // the raise completed before the error: both steps ran
        AssertSteps(2, Publisher.Steps());
    end;

    [Test]
    procedure UnrelatedErrorBeforeTheRaiseStillPropagates()
    var
        Publisher: Codeunit "Ebe Publisher";
    begin
        asserterror Publisher.FailThenRaise();
        ExpectedError('Ebe unrelated failure before the raise');
        AssertSteps(1, Publisher.Steps());
    end;

    local procedure AssertSteps(Expected: Integer; Actual: Integer)
    begin
        if Expected <> Actual then
            Error('Expected %1 step(s), got %2', Expected, Actual);
    end;

    local procedure ExpectedError(Fragment: Text)
    begin
        if StrPos(GetLastErrorText(), Fragment) = 0 then
            Error('Expected error containing ''%1'' but got ''%2''', Fragment, GetLastErrorText());
    end;
}
