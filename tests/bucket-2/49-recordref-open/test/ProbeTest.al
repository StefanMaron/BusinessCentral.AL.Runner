codeunit 56491 "RR Open Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ThreeArgOpenIsCallable()
    var
        Probe: Codeunit "RR Open Probe";
    begin
        // [GIVEN] A procedure that calls RecRef.Open(int, bool, text) + IsEmpty
        // [THEN] The codeunit compiles and both exit paths return 42
        Assert.AreEqual(42, Probe.ProbeCompany('CRONUS'), '3-arg Open + IsEmpty must compile and run');
    end;

    [Test]
    procedure OneArgOpenCompiles()
    var
        Probe: Codeunit "RR Open Probe";
    begin
        // Single-arg form must compile — the IsEmpty branch has an empty body
        // so we don't depend on its return value.
        Assert.AreEqual(7, Probe.ProbeLocalCompiles(), '1-arg Open should compile and reach the sentinel');
    end;
}
