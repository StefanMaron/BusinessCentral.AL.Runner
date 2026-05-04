// Tests for the "in-slice subscriber fires correctly" scenario (issue #1596).
//
// Issue #1596: compile-dep now drops [EventSubscriber] attributes that reference
// codeunits NOT in the slice.  These AL end-to-end tests verify that when the
// publisher IS in the slice, the subscriber fires correctly — i.e. the fix does
// not break the working subscriber path.
//
// Test codeunit ID: 159603.

codeunit 159603 "SMC Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Publisher: Codeunit "SMC Publisher";
        Helper: Codeunit "SMC Log Helper";

    [Test]
    procedure Calculate_FiresOnAfterCalculate_Subscriber()
    begin
        // Positive: Calculate must fire the OnAfterCalculate subscriber, which
        // records the event in the SMC Event Log table.
        Helper.Reset();

        Publisher.Calculate(5);

        Assert.AreEqual(1, Helper.GetFireCount(),
            'OnAfterCalculate subscriber must fire exactly once');
        Assert.AreEqual(5, Helper.GetLastInput(),
            'Subscriber must record Input=5');
        Assert.AreEqual(10, Helper.GetLastResult(),
            'Subscriber must record Result=10 (5*2)');
    end;

    [Test]
    procedure Calculate_MultipleCallsFireSubscriberEachTime()
    begin
        // Positive: each Calculate call fires the subscriber once.
        Helper.Reset();

        Publisher.Calculate(3);
        Publisher.Calculate(7);

        Assert.AreEqual(2, Helper.GetFireCount(),
            'Subscriber must fire once per Calculate call');
        // Last call wins for Input/Result
        Assert.AreEqual(7, Helper.GetLastInput(),
            'Last subscriber call must record Input=7');
        Assert.AreEqual(14, Helper.GetLastResult(),
            'Last subscriber call must record Result=14 (7*2)');
    end;

    [Test]
    procedure Calculate_ZeroInput_SubscriberReceivesZero()
    begin
        // Edge case: Calculate(0) → Result=0; subscriber receives both as zero.
        Helper.Reset();

        Publisher.Calculate(0);

        Assert.AreEqual(1, Helper.GetFireCount(),
            'Subscriber must fire even for zero input');
        Assert.AreEqual(0, Helper.GetLastInput(), 'Last Input must be 0');
        Assert.AreEqual(0, Helper.GetLastResult(), 'Last Result must be 0');
    end;

    [Test]
    procedure Calculate_NegativeInput_SubscriberReceivesNegativeResult()
    begin
        // Negative direction: Calculate(-4) → Result=-8.
        Helper.Reset();

        Publisher.Calculate(-4);

        Assert.AreEqual(1, Helper.GetFireCount(),
            'Subscriber must fire for negative input');
        Assert.AreEqual(-4, Helper.GetLastInput(), 'Last Input must be -4');
        Assert.AreEqual(-8, Helper.GetLastResult(), 'Last Result must be -8');
    end;
}
