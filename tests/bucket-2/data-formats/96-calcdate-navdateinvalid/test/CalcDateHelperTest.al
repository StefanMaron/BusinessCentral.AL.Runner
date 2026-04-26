codeunit 304003 "CalcDate Helper Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";
        Helper: Codeunit "CalcDate Helper";

    [Test]
    procedure CalcDateFormulaAdd7Days()
    var
        BaseDT: DateTime;
        ResultDT: DateTime;
        ExpectedDate: Date;
    begin
        // Positive: CalcDate with DateFormula overload adds 7 days correctly
        BaseDT := CreateDateTime(DMY2Date(15, 3, 2025), 120000T);
        ResultDT := Helper.CalcNextReportDate(BaseDT, 7);
        ExpectedDate := DMY2Date(22, 3, 2025);
        Assert.AreEqual(ExpectedDate, DT2Date(ResultDT), 'Date should be 7 days later');
        Assert.AreEqual(120000T, DT2Time(ResultDT), 'Time component should be preserved');
    end;

    [Test]
    procedure CalcDateStringAdd7Days()
    var
        BaseDT: DateTime;
        ResultDT: DateTime;
        ExpectedDate: Date;
    begin
        // Positive: CalcDate with string overload adds 7 days correctly
        BaseDT := CreateDateTime(DMY2Date(15, 3, 2025), 120000T);
        ResultDT := Helper.CalcNextReportDateStr(BaseDT, 7);
        ExpectedDate := DMY2Date(22, 3, 2025);
        Assert.AreEqual(ExpectedDate, DT2Date(ResultDT), 'Date should be 7 days later');
        Assert.AreEqual(120000T, DT2Time(ResultDT), 'Time component should be preserved');
    end;

    [Test]
    procedure CalcDateFormulaAdd30Days()
    var
        BaseDT: DateTime;
        ResultDT: DateTime;
        ExpectedDate: Date;
    begin
        // Positive: CalcDate with DateFormula adds 30 days, crossing month boundary
        BaseDT := CreateDateTime(DMY2Date(15, 3, 2025), 080000T);
        ResultDT := Helper.CalcNextReportDate(BaseDT, 30);
        ExpectedDate := DMY2Date(14, 4, 2025);
        Assert.AreEqual(ExpectedDate, DT2Date(ResultDT), 'Date should be 30 days later');
    end;

    [Test]
    procedure CalcDateFormulaFromCurrentDateTime()
    var
        BaseDT: DateTime;
        ResultDT: DateTime;
        BaseDate: Date;
        ResultDate: Date;
    begin
        // Positive: CalcDate from CurrentDateTime (the telemetry scenario)
        BaseDT := CurrentDateTime;
        ResultDT := Helper.CalcNextReportDate(BaseDT, 7);
        BaseDate := DT2Date(BaseDT);
        ResultDate := DT2Date(ResultDT);
        Assert.IsTrue(ResultDate > BaseDate, 'Result date must be after base date');
    end;

    [Test]
    procedure CalcDateWithZeroDateThrows()
    var
        d: Date;
    begin
        // Negative: CalcDate on 0D should throw
        asserterror d := CalcDate('<+7D>', 0D);
        Assert.ExpectedError('undefined date');
    end;
}
