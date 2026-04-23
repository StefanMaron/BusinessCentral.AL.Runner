/// Test fixture that deliberately triggers a runner limitation (NotSupportedException
/// from Query.SaveAsCsv). Used by the --strict integration test to verify exit code
/// changes from 2 (non-strict) to 1 (strict).
codeunit 59950 "Test Strict Mode"
{
    Subtype = Test;

    [Test]
    procedure TestQuerySaveAsCsvRunnerLimitation()
    var
        Q: Query "Strict Test Query";
    begin
        // Query.SaveAsCsv() throws NotSupportedException in the runner.
        // Query.Open/Read/Close now work in-memory, but SaveAsCsv requires the BC service tier.
        // Without asserterror, this propagates as a runner limitation (exit 2).
        Q.SaveAsCsv('test.csv');
    end;
}
