codeunit 59572 "CIR Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "CIR Src";

    [Test]
    procedure InstanceRun_FiresTrigger_SetsValue()
    var
        Proc: Codeunit "CIR Processor";
    begin
        // Positive: Proc.Run() on a codeunit variable must fire the OnRun trigger,
        // which sets ProcessedValue to 42.
        Assert.AreEqual(42, Src.RunAndGetValue(Proc),
            'After Proc.Run(), GetValue must return 42');
    end;

    [Test]
    procedure InstanceRun_FiresTrigger_SetsBoolean()
    var
        Proc: Codeunit "CIR Processor";
    begin
        // Proving: OnRun actually executed (not just returning the default 42).
        Assert.IsTrue(Src.RunAndGetDidRun(Proc),
            'After Proc.Run(), DidRun must be true');
    end;

    [Test]
    procedure NoRun_LeavesValueAtDefault()
    var
        Proc: Codeunit "CIR Processor";
    begin
        // Negative trap: guard against a broken mock where GetValue() always
        // returns 42 regardless of whether Run fired. Without Run, ProcessedValue
        // must still be the Integer default 0.
        Assert.AreEqual(0, Src.GetValueWithoutRun(Proc),
            'Without Run, GetValue must return default 0');
    end;

    [Test]
    procedure InstanceRun_PreservesState()
    var
        Proc: Codeunit "CIR Processor";
        first: Integer;
        second: Integer;
    begin
        // Proving: the SAME codeunit instance observed before and after Run()
        // sees 0 → 42 — which only works if OnRun fires and mutates _this_ instance
        // (not a fresh copy).
        first := Src.GetValueWithoutRun(Proc);
        second := Src.RunAndGetValue(Proc);
        Assert.AreEqual(0, first, 'Pre-Run GetValue must be 0');
        Assert.AreEqual(42, second, 'Post-Run GetValue must be 42');
    end;

    [Test]
    procedure InstanceRun_NotZeroDefault_NegativeTrap()
    var
        Proc: Codeunit "CIR Processor";
    begin
        // Negative: guard against a no-op Run() that leaves the value at default.
        Assert.AreNotEqual(0, Src.RunAndGetValue(Proc),
            'After Run(), the value must no longer be the default 0');
    end;
}
