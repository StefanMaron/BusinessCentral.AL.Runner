/// Tests for Report.RunRequestPage 2-arg overload — issue #1329.
codeunit 307401 "RRP Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure Report_RunRequestPage_2Arg_ReturnsText()
    var
        Src: Codeunit "RRP Src";
        Result: Text;
    begin
        // [GIVEN] A valid (dummy) report id and non-empty request parameters
        // [WHEN]  Report.RunRequestPage(reportId, requestParameters) is called
        Result := Src.RunRequestPage2Arg(99999, '<ReqParams />');

        // [THEN]  Returns empty string in standalone mode (no UI, no request page rendered)
        Assert.AreEqual('', Result, 'RunRequestPage 2-arg must return empty string in standalone mode');
    end;

    [Test]
    procedure Report_RunRequestPage_2Arg_EmptyParams_ReturnsText()
    var
        Src: Codeunit "RRP Src";
        Result: Text;
    begin
        // [GIVEN] A valid (dummy) report id and an empty request parameters string
        // [WHEN]  Report.RunRequestPage(reportId, '') is called
        Result := Src.RunRequestPage2Arg(99999, '');

        // [THEN]  Returns empty string in standalone mode
        Assert.AreEqual('', Result, 'RunRequestPage 2-arg with empty params must return empty string in standalone mode');
    end;

    [Test]
    procedure Report_RunRequestPage_1Arg_StillWorks()
    var
        Src: Codeunit "RRP Src";
        Result: Text;
    begin
        // [GIVEN] A valid (dummy) report id
        // [WHEN]  Report.RunRequestPage(reportId) 1-arg overload is called
        Result := Src.RunRequestPage1Arg(99999);

        // [THEN]  Returns empty string in standalone mode (regression guard)
        Assert.AreEqual('', Result, 'RunRequestPage 1-arg must still return empty string in standalone mode');
    end;
}
