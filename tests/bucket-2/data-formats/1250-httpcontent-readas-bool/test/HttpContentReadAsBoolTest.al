/// <summary>
/// Tests for issue #1250: HttpContent.ReadAs(var Text) in an 'if' condition
/// caused CS0019 because MockHttpContent.ALReadAs returned void.
///
/// Positive test: GetProbeVersion() returns the expected value — proves the codeunit
///   compiled (CS0019 is gone) and the pure-logic path works.
/// Negative test: GetProbeVersion() with wrong expected value fires Assert.AreEqual error —
///   proves the assertion framework is live and would catch a broken stub.
/// </summary>
codeunit 1251 "HTTP ReadAs Bool Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "Assert";

    /// <summary>
    /// Positive: probe version returns 1250, proving the codeunit compiled
    /// without CS0019 and the conditional ReadAs pattern is accepted by the runner.
    /// </summary>
    [Test]
    procedure ProbeVersionReturnsExpectedValue()
    var
        Probe: Codeunit "HTTP ReadAs Bool Probe";
    begin
        Assert.AreEqual(1250, Probe.GetProbeVersion(), 'GetProbeVersion should return 1250');
    end;

    /// <summary>
    /// Negative: wrong expected value triggers a failure, proving the assertion
    /// is live and a broken no-op stub returning 0 would be caught.
    /// </summary>
    [Test]
    procedure ProbeVersionWrongExpectationFails()
    var
        Probe: Codeunit "HTTP ReadAs Bool Probe";
    begin
        asserterror Assert.AreEqual(9999, Probe.GetProbeVersion(), 'should not be 9999');
        Assert.ExpectedError('AreEqual');
    end;
}
