/// Tests string-to-NavText conversion in ErrorInfo.Create — issue #1278.
codeunit 1278002 "StringNavText Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";
        Src: Codeunit "StringNavText Src";

    [Test]
    procedure ErrorInfo_Create_WithLiteral()
    begin
        // Positive: string literal passes through to ErrorInfo.Message
        Assert.AreEqual('Something went wrong', Src.CreateErrorInfoWithLiteral(),
            'ErrorInfo.Create with string literal should set Message');
    end;

    [Test]
    procedure ErrorInfo_Create_FromVar()
    begin
        // Positive: Text variable passes through to ErrorInfo.Message
        Assert.AreEqual('Variable message', Src.CreateErrorInfoFromVar(),
            'ErrorInfo.Create with Text variable should set Message');
    end;

    [Test]
    procedure ErrorInfo_Create_Literal_RaisesCorrectError()
    begin
        // Negative: ErrorInfo.Create with a literal, then Error(ErrorInfo),
        // must raise exactly the message that was passed.
        asserterror Src.RaiseErrorInfoLiteral();
        Assert.ExpectedError('Deliberate test error');
    end;
}
