/// Test fixture that deliberately triggers a runner limitation (NotSupportedException
/// from Query.Open). Used by the --strict integration test to verify exit code
/// changes from 2 (non-strict) to 1 (strict).
codeunit 59950 "Test Strict Mode"
{
    Subtype = Test;

    [Test]
    procedure TestQueryOpenRunnerLimitation()
    var
        Q: Query "Strict Test Query";
    begin
        // Query.Open() throws NotSupportedException in the runner (SQL views require BC service tier).
        // Without asserterror, this propagates as a runner limitation (exit 2).
        Q.Open();
    end;
}
