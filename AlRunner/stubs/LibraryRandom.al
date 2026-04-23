// Stub for BC's "Library - Random" codeunit (ID 130440) from Test-TestLibraries.
// Provides pseudo-random value generation for AL tests.
// All methods use BC built-ins so they run natively inside al-runner.
// Note: The OnBeforeTestRun/OnAfterTestRun event subscriber is omitted because
// "CAL Test Runner Publisher" (codeunit 130451) is not available in al-runner.
codeunit 130440 "Library - Random"
{
    SingleInstance = true;

    trigger OnRun()
    begin
    end;

    var
        Seed: Integer;
        SeedSet: Boolean;

    procedure RandDec(Range: Integer; Decimals: Integer): Decimal
    begin
        exit(RandInt(Range * Power(10, Decimals)) / Power(10, Decimals));
    end;

    procedure RandDecInRange("Min": Integer; "Max": Integer; Decimals: Integer): Decimal
    begin
        exit(Min + RandDec(Max - Min, Decimals));
    end;

    procedure RandDecInDecimalRange("Min": Decimal; "Max": Decimal; Precision: Integer): Decimal
    var
        Min2: Integer;
        Max2: Integer;
        Pow: Integer;
    begin
        Pow := Power(10, Precision);
        Min2 := Round(Min * Pow, 1, '>');
        Max2 := Round(Max * Pow, 1, '<');
        exit(RandIntInRange(Min2, Max2) / Pow);
    end;

    procedure RandInt(Range: Integer): Integer
    begin
        if Range < 1 then
            exit(1);
        exit(GetNextValue(Range));
    end;

    procedure RandIntInRange("Min": Integer; "Max": Integer): Integer
    begin
        exit(Min - 1 + RandInt(Max - Min + 1));
    end;

    procedure RandDate(Delta: Integer): Date
    begin
        if Delta = 0 then
            exit(WorkDate());
        exit(CalcDate(StrSubstNo('<%1D>', Delta / Abs(Delta) * RandInt(Abs(Delta))), WorkDate()));
    end;

    procedure RandDateFrom(FromDate: Date; Range: Integer): Date
    begin
        if Range = 0 then
            exit(FromDate);
        exit(CalcDate(StrSubstNo('<%1D>', Range / Abs(Range) * RandInt(Range)), FromDate));
    end;

    procedure RandDateFromInRange(FromDate: Date; FromRange: Integer; ToRange: Integer): Date
    begin
        if FromRange >= ToRange then
            exit(FromDate);
        exit(CalcDate(StrSubstNo('<+%1D>', RandIntInRange(FromRange, ToRange)), FromDate));
    end;

    procedure RandPrecision(): Decimal
    begin
        exit(1 / Power(10, RandInt(5)));
    end;

    procedure RandText(Length: Integer): Text
    var
        GuidTxt: Text;
    begin
        while StrLen(GuidTxt) < Length do
            GuidTxt += LowerCase(DelChr(Format(CreateGuid()), '=', '{}-'));
        exit(CopyStr(GuidTxt, 1, Length));
    end;

    procedure Init(): Integer
    begin
        exit(SetSeed(Time - 000000T));
    end;

    procedure SetSeed(Val: Integer): Integer
    begin
        Seed := Val;
        SeedSet := true;
        Randomize(Seed);
        exit(Seed);
    end;

    local procedure GetNextValue(MaxValue: Integer): Integer
    begin
        if not SeedSet then
            SetSeed(1);
        exit(Random(MaxValue));
    end;
}
