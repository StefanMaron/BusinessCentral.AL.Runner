/// Test fixture that deliberately triggers a runner limitation (NotSupportedException
/// from XmlPort.Import). Used by the --strict integration test to verify exit code
/// changes from 2 (non-strict) to 1 (strict).
codeunit 59950 "Test Strict Mode"
{
    Subtype = Test;

    [Test]
    procedure TestXmlPortImportRunnerLimitation()
    var
        XP: XmlPort "Strict Test XmlPort";
        InStr: InStream;
    begin
        // XmlPort.Import() throws NotSupportedException in the runner.
        // Without asserterror, this propagates as a runner limitation (exit 2).
        XP.Import();
    end;
}
