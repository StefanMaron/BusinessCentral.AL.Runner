codeunit 70000 "Probe Dependency"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure ReadsFieldOfGeneratedEnum_NamesNoValue()
    var
        D: Record "Probe Doc";
    begin
        D.Init();
        if D.Status.AsInteger() <> 0 then
            Error('not 0');
    end;

    [Test]
    procedure CallsHelperNotUsingMissingCodeunit()
    var
        H: Codeunit "Probe Helper";
    begin
        if H.Answer() <> 42 then
            Error('x');
    end;

    [Test]
    procedure CallsAnotherHelper_NamesNoStub()
    var
        H: Codeunit "Other Helper";
    begin
        if H.Answer() <> 7 then
            Error('x');
    end;

    [Test]
    procedure RaisesEventOfSubscriberWithMissingGlobal()
    var
        P: Codeunit "Probe Publisher";
    begin
        P.Raise();
    end;
}
