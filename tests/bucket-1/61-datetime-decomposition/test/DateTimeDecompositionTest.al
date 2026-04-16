codeunit 61001 "DateTime Decomposition Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CreateDateTime_BuildsFromDateAndTime()
    var
        Helper: Codeunit "DateTime Helper";
        D: Date;
        T: Time;
        DT: DateTime;
    begin
        // [GIVEN] A known date and time
        D := 20240115D;
        T := 120000T;

        // [WHEN] CreateDateTime is called
        DT := Helper.BuildDateTime(D, T);

        // [THEN] The result is not the blank DateTime
        Assert.AreNotEqual(0DT, DT, 'CreateDateTime must not return blank DateTime');
    end;

    [Test]
    procedure DT2Date_ExtractsCorrectDate()
    var
        Helper: Codeunit "DateTime Helper";
        D: Date;
        DT: DateTime;
    begin
        // [GIVEN] A DateTime built from a known date
        D := 20240115D;
        DT := Helper.BuildDateTime(D, 120000T);

        // [WHEN] DT2Date is called
        // [THEN] The extracted date matches the original
        Assert.AreEqual(D, Helper.GetDate(DT), 'DT2Date must return the date used in CreateDateTime');
    end;

    [Test]
    procedure DT2Time_ExtractsCorrectTime()
    var
        Helper: Codeunit "DateTime Helper";
        T: Time;
        DT: DateTime;
    begin
        // [GIVEN] A DateTime built from a known time
        T := 153045T;
        DT := Helper.BuildDateTime(20240115D, T);

        // [WHEN] DT2Time is called
        // [THEN] The extracted time matches the original
        Assert.AreEqual(T, Helper.GetTime(DT), 'DT2Time must return the time used in CreateDateTime');
    end;

    [Test]
    procedure DT2Date_OnBlankDateTime_ReturnsBlankDate()
    begin
        // [GIVEN] The blank DateTime (0DT)
        // [WHEN] DT2Date is called
        // [THEN] Returns blank Date (0D)
        Assert.AreEqual(0D, DT2Date(0DT), 'DT2Date on 0DT must return 0D');
    end;

    [Test]
    procedure DT2Time_OnBlankDateTime_ReturnsBlankTime()
    begin
        // [GIVEN] The blank DateTime (0DT)
        // [WHEN] DT2Time is called
        // [THEN] Returns blank Time (0T)
        Assert.AreEqual(0T, DT2Time(0DT), 'DT2Time on 0DT must return 0T');
    end;

    [Test]
    procedure DT2Date_ReturnsDate_NotDifferentDate()
    var
        Helper: Codeunit "DateTime Helper";
        D: Date;
        DT: DateTime;
    begin
        // [GIVEN] A DateTime built from 20230601D
        D := 20230601D;
        DT := Helper.BuildDateTime(D, 080000T);

        // [THEN] DT2Date does not return a different date (proves round-trip, not no-op)
        Assert.AreNotEqual(20231231D, Helper.GetDate(DT), 'DT2Date must not return a different date');
        Assert.AreEqual(D, Helper.GetDate(DT), 'DT2Date round-trip must match original date');
    end;
}
