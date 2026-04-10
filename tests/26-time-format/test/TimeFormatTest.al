codeunit 50601 TimeFormatTest
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestTimeComparison()
    var
        Helper: Codeunit TimeHelper;
        Morning: Time;
        Noon: Time;
    begin
        // [SCENARIO] Time values can be compared in assertions.
        Morning := Helper.GetMorningTime();
        Noon := Helper.GetNoonTime();

        Assert.AreEqual(060000T, Morning, 'Morning should be 06:00.');
        Assert.AreEqual(120000T, Noon, 'Noon should be 12:00.');
        Assert.AreNotEqual(Morning, Noon, 'Morning and Noon should differ.');
    end;

    [Test]
    procedure TestTimeNotEqual()
    var
        Helper: Codeunit TimeHelper;
    begin
        // [SCENARIO] Negative test: wrong time comparison fails.
        asserterror Assert.AreEqual(080000T, Helper.GetMorningTime(), 'Should fail: 08:00 is not 06:00.');
        Assert.ExpectedError('Assert.AreEqual failed');
    end;
}
