codeunit 304002 "CalcDate Helper"
{
    /// Reproduce the scenario from telemetry: take CurrentDateTime,
    /// extract the date, CalcDate +7D on it, then CreateDateTime back.
    procedure CalcNextReportDate(CurrentDT: DateTime; DaysToAdd: Integer): DateTime
    var
        d: Date;
        t: Time;
        df: DateFormula;
    begin
        d := DT2Date(CurrentDT);
        t := DT2Time(CurrentDT);
        Evaluate(df, StrSubstNo('<+%1D>', DaysToAdd));
        d := CalcDate(df, d);
        exit(CreateDateTime(d, t));
    end;

    /// CalcDate with a string formula (second overload).
    procedure CalcNextReportDateStr(CurrentDT: DateTime; DaysToAdd: Integer): DateTime
    var
        d: Date;
        t: Time;
    begin
        d := DT2Date(CurrentDT);
        t := DT2Time(CurrentDT);
        d := CalcDate(StrSubstNo('<+%1D>', DaysToAdd), d);
        exit(CreateDateTime(d, t));
    end;
}
