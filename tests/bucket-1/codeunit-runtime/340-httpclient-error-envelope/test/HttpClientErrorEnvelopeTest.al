codeunit 1320516 "HC Error Envelope Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "HC Error Envelope Src";

    [Test]
    procedure SendTimeout_ThrowsErrorEnvelope()
    var
        Err: Text;
    begin
        asserterror Src.SendTimeoutResponse();
        Err := GetLastErrorText();
        Assert.IsTrue(StrPos(Err, 'HTTP Request resulted in an error.') > 0,
            'Error should start with the standard HTTP failure prefix');
        Assert.IsTrue(StrPos(Err, '"Method":"GET"') > 0,
            'Envelope should include the HTTP method');
        Assert.IsTrue(StrPos(Err, '"URI":"https://somevalidurl.com/SomePath"') > 0,
            'Envelope should include the request URI');
        Assert.IsTrue(StrPos(Err, '"HTTP Status Code":504') > 0,
            'Envelope should include the response status code');
        Assert.IsTrue(StrPos(Err, '"Reason Phrase":"Gateway Timeout"') > 0,
            'Envelope should include the response reason phrase');
        Assert.IsTrue(StrPos(Err, '"message":"Endpoint request timed out"') > 0,
            'Envelope should include the response body');
    end;

    [Test]
    procedure SendDefaultResponse_ThrowsNotSupported()
    begin
        asserterror Src.SendDefaultResponse();
        Assert.ExpectedError('HTTP calls are not supported by al-runner');
    end;
}
