/// Tests for Report instance variable RunRequestPage(requestParameters) 1-arg overload — issue #1333.
codeunit 308001 "ReportInstanceRRP Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure Report_Instance_RunRequestPage_1Arg_ReturnsText()
    var
        Src: Codeunit "ReportInstanceRRP Src";
        Result: Text;
    begin
        // [GIVEN] A report instance variable and non-empty request parameters
        // [WHEN]  Rep.RunRequestPage(requestParameters) 1-arg instance overload is called
        Result := Src.RunRequestPage1Arg('<RequestPage />');

        // [THEN]  Returns a string in standalone mode (no UI rendered)
        Assert.AreEqual('', Result, 'RunRequestPage 1-arg instance must return empty string in standalone mode');
    end;

    [Test]
    procedure Report_Instance_RunRequestPage_1Arg_EmptyParams()
    var
        Src: Codeunit "ReportInstanceRRP Src";
        Result: Text;
    begin
        // [GIVEN] A report instance variable and empty request parameters
        // [WHEN]  Rep.RunRequestPage('') 1-arg instance overload is called
        Result := Src.RunRequestPage1Arg('');

        // [THEN]  Returns empty string in standalone mode
        Assert.AreEqual('', Result, 'RunRequestPage 1-arg instance with empty params must return empty string');
    end;

    [Test]
    procedure Report_Instance_RunRequestPage_0Arg_RegressionGuard()
    var
        Src: Codeunit "ReportInstanceRRP Src";
        Result: Text;
    begin
        // [GIVEN] A report instance variable with no registered handler
        // [WHEN]  Rep.RunRequestPage() 0-arg overload is called (no handler registered)
        // [THEN]  It throws the expected error (pre-existing behavior must not regress)
        asserterror Result := Src.RunRequestPage0Arg();
        Assert.ExpectedError('No RequestPageHandler registered');
    end;
}
