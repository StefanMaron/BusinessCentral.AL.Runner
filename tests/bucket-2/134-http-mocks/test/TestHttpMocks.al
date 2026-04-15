codeunit 59970 "Test Http Mocks"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestContentWriteRead()
    var
        Logic: Codeunit "Http Test Logic";
    begin
        Assert.AreEqual('hello world', Logic.WriteAndReadContent('hello world'),
            'Content round-trip should preserve text');
    end;

    [Test]
    procedure TestContentWriteReadEmpty()
    var
        Logic: Codeunit "Http Test Logic";
    begin
        Assert.AreEqual('', Logic.WriteAndReadContent(''),
            'Content round-trip should handle empty text');
    end;

    [Test]
    procedure TestDefaultStatusCode()
    var
        Logic: Codeunit "Http Test Logic";
    begin
        Assert.AreEqual(200, Logic.GetDefaultStatusCode(),
            'Default status code should be 200');
    end;

    [Test]
    procedure TestIsSuccessStatusCode()
    var
        Logic: Codeunit "Http Test Logic";
    begin
        Assert.IsTrue(Logic.IsSuccessStatusCode(),
            'Default 200 should be success');
    end;

    [Test]
    procedure TestHeaderAddContains()
    var
        Logic: Codeunit "Http Test Logic";
    begin
        Assert.IsTrue(Logic.AddAndCheckHeader(),
            'Added header should be found via Contains');
    end;

    [Test]
    procedure TestHeaderRemove()
    var
        Logic: Codeunit "Http Test Logic";
    begin
        Assert.IsTrue(Logic.RemoveHeader(),
            'Header should not be found after Remove');
    end;

    [Test]
    procedure TestSendThrowsError()
    var
        Logic: Codeunit "Http Test Logic";
    begin
        asserterror Logic.SendRequestFails();
        Assert.ExpectedError('HTTP calls are not supported by al-runner');
    end;

    [Test]
    procedure TestGetThrowsError()
    var
        Logic: Codeunit "Http Test Logic";
    begin
        asserterror Logic.GetRequestFails();
        Assert.ExpectedError('HTTP calls are not supported by al-runner');
    end;

    [Test]
    procedure TestBuildRequest()
    var
        Logic: Codeunit "Http Test Logic";
    begin
        Logic.BuildRequest();
    end;
}
