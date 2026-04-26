codeunit 62001 "Test Time Decomposition"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // --- Time component extraction via Format picture strings ---

    [Test]
    procedure Format_ExtractsHour_From120000T()
    var
        Helper: Codeunit "Time Decomposition Helper";
    begin
        // 120000T = 12:00:00 → <Hours24,2> = '12'
        Assert.AreEqual('12', Helper.GetHour(120000T), 'Hours24 picture must extract hour 12');
    end;

    [Test]
    procedure Format_ExtractsMinute_From123000T()
    var
        Helper: Codeunit "Time Decomposition Helper";
    begin
        // 123000T = 12:30:00 → <Minutes,2> = '30'
        Assert.AreEqual('30', Helper.GetMinute(123000T), 'Minutes picture must extract minute 30');
    end;

    [Test]
    procedure Format_ExtractsSecond_From123045T()
    var
        Helper: Codeunit "Time Decomposition Helper";
    begin
        // 123045T = 12:30:45 → <Seconds,2> = '45'
        Assert.AreEqual('45', Helper.GetSecond(123045T), 'Seconds picture must extract second 45');
    end;

    [Test]
    procedure Format_MidnightTime_AllZero()
    var
        Helper: Codeunit "Time Decomposition Helper";
    begin
        // 0T = midnight → HMS = '00:00:00'
        Assert.AreEqual('00:00:00', Helper.FormatHMS(0T), 'Midnight 0T must format as 00:00:00');
    end;

    [Test]
    procedure Format_AllComponents_091523T()
    var
        Helper: Codeunit "Time Decomposition Helper";
    begin
        // 091523T = 09:15:23 — proves all three components correct in one call
        Assert.AreEqual('09:15:23', Helper.FormatHMS(091523T), 'FormatHMS of 09:15:23 must be 09:15:23');
    end;

    [Test]
    procedure Format_Hour_NotSameAsMinute()
    var
        Helper: Codeunit "Time Decomposition Helper";
    begin
        // Prove GetHour and GetMinute return different values — a no-op returning '00' would fail
        Assert.AreNotEqual(Helper.GetHour(150000T), Helper.GetMinute(150000T),
            'Hour 15 and minute 00 of 15:00:00 must differ');
    end;

    // --- DT2Time via helper ---

    [Test]
    procedure DT2Time_ExtractsTimeComponent()
    var
        Helper: Codeunit "Time Decomposition Helper";
        DT: DateTime;
        T: Time;
    begin
        // Build a DateTime with a known time and extract it back
        T := 143000T; // 14:30:00
        DT := CreateDateTime(20240115D, T);
        Assert.AreEqual(T, Helper.GetTimeFromDateTime(DT),
            'DT2Time must return the time component used in CreateDateTime');
    end;
}
