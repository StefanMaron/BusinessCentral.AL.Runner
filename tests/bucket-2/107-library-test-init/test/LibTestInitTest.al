codeunit 107001 "Lib Test Init Test"
{
    Subtype = Test;

    [Test]
    procedure OnTestInitialize_FiresWithoutError()
    var
        LibTestInit: Codeunit "Library - Test Initialize";
    begin
        // [WHEN] Calling OnTestInitialize (an integration event publisher)
        // [THEN] No error — the event fires and any subscribers run
        LibTestInit.OnTestInitialize(Codeunit::"Lib Test Init Test");
    end;

    [Test]
    procedure OnBeforeTestSuiteInitialize_FiresWithoutError()
    var
        LibTestInit: Codeunit "Library - Test Initialize";
    begin
        LibTestInit.OnBeforeTestSuiteInitialize(Codeunit::"Lib Test Init Test");
    end;

    [Test]
    procedure OnAfterTestSuiteInitialize_FiresWithoutError()
    var
        LibTestInit: Codeunit "Library - Test Initialize";
    begin
        LibTestInit.OnAfterTestSuiteInitialize(Codeunit::"Lib Test Init Test");
    end;
}
