/// <summary>
/// #5339: tests in a bundle of their own, reaching by id objects of ANOTHER bundle that either was dropped from it or never
/// compiled. Run with the library, each one must fail loudly; run alone, nothing declares the ids and each one passes.
/// </summary>
codeunit 72231 "MOD Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure BareAsserterror_RunDroppedCodeunit()
    begin
        asserterror Codeunit.Run(72212);
    end;

    [Test]
    procedure Guarded_RunDroppedCodeunit()
    var
        Ok: Boolean;
    begin
        asserterror Ok := Codeunit.Run(72212);
    end;

    [Test]
    procedure BareAsserterror_OpenUncompiledTable()
    var
        RecRef: RecordRef;
    begin
        asserterror RecRef.Open(72223);
    end;

    [Test]
    procedure BareAsserterror_RunUncompiledCodeunit()
    begin
        asserterror Codeunit.Run(72222);
    end;
}
