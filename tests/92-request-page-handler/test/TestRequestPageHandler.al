codeunit 92002 "RPH Request Page Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    [HandlerFunctions('ReqPageHandler')]
    procedure RunRequestPageInvokesHandler()
    var
        Caller: Codeunit "RPH Report Caller";
        Result: Text;
    begin
        // [GIVEN] A RequestPageHandler is registered
        // [WHEN] RunRequestPage is called
        Result := Caller.CallRunRequestPage();

        // [THEN] The handler is invoked and a result string is returned
        Assert.AreNotEqual('', Result, 'RunRequestPage should return a non-empty result');
    end;

    [RequestPageHandler]
    procedure ReqPageHandler(var RequestPage: TestRequestPage "RPH Test Report")
    begin
        // Handler accepts the request page
    end;

    [Test]
    procedure RunRequestPageWithoutHandlerStillReturns()
    var
        Caller: Codeunit "RPH Report Caller";
        Result: Text;
    begin
        // [GIVEN] No handler registered
        // [WHEN] RunRequestPage is called
        Result := Caller.CallRunRequestPage();

        // [THEN] Returns XML stub without crashing (handler is optional)
        Assert.AreNotEqual('', Result, 'RunRequestPage should return XML stub even without handler');
    end;
}
