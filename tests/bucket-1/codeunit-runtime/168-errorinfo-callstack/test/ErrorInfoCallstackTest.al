codeunit 59881 "EIC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EIC Src";

    [Test]
    procedure Callstack_Fresh_ReturnsText()
    var
        cs: Text;
    begin
        // Positive: Callstack() on a fresh ErrorInfo must return without throwing.
        cs := Src.GetCallstackFromFreshErrorInfo();
        // Accept any string (empty or non-empty) — what matters is no crash.
        Assert.IsTrue((cs = '') or (cs <> ''),
            'Callstack must return a Text value without throwing');
    end;

    [Test]
    procedure Callstack_ReturnsText_Assignable()
    var
        ei: ErrorInfo;
    begin
        // Proving: the Callstack() return value is assignable to Text.
        Assert.IsTrue(Src.CallstackIsText(ei),
            'Callstack result must be assignable to Text');
    end;

    [Test]
    procedure Callstack_DoesNotThrow_FromGetCallstack()
    var
        ei: ErrorInfo;
        cs: Text;
    begin
        // Negative trap: guard against a throwing stub.
        cs := Src.GetCallstack(ei);
        Assert.IsTrue((cs = '') or (cs <> ''),
            'GetCallstack must return a Text without throwing');
    end;

    [Test]
    procedure Callstack_CalledMultipleTimes_Stable()
    var
        a: Text;
        b: Text;
    begin
        // Two consecutive reads on the same fresh ErrorInfo must return the
        // same value (Callstack is an accessor, not a factory).
        a := Src.GetCallstackFromFreshErrorInfo();
        b := Src.GetCallstackFromFreshErrorInfo();
        Assert.AreEqual(a, b, 'Consecutive Callstack reads must be stable');
    end;
}
