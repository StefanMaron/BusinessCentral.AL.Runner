/// Helper codeunit exercising CalcDate() formula arithmetic.
codeunit 60060 "CDF Src"
{
    procedure Calc(formula: Text; baseDate: Date): Date
    begin
        exit(CalcDate(formula, baseDate));
    end;

    procedure CalcOneArg(formula: Text): Date
    begin
        // Single-arg overload anchors on Today().
        exit(CalcDate(formula));
    end;

    procedure CalcFormulaType(formula: DateFormula; baseDate: Date): Date
    begin
        // Overload taking a typed DateFormula value.
        exit(CalcDate(formula, baseDate));
    end;

    procedure AddOneDay(d: Date): Date
    begin
        exit(CalcDate('<+1D>', d));
    end;

    procedure AddOneMonth(d: Date): Date
    begin
        exit(CalcDate('<+1M>', d));
    end;

    procedure AddOneYear(d: Date): Date
    begin
        exit(CalcDate('<+1Y>', d));
    end;

    procedure SubtractOneWeek(d: Date): Date
    begin
        exit(CalcDate('<-1W>', d));
    end;
}
