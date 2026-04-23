// Stub for BC's "Any" codeunit (ID 130500) from System.TestLibraries.Utilities.
// Provides pseudo-random test data generation. Methods use BC built-ins (Random,
// CreateGuid, CalcDate) so they run natively inside al-runner without special routing.
codeunit 130500 "Any"
{
    var
        Seed: Integer;
        SeedSet: Boolean;

    procedure Boolean(): Boolean
    begin
        exit(GetNextValue(2) = 2);
    end;

    procedure IntegerInRange(MaxValue: Integer): Integer
    begin
        if MaxValue < 1 then
            exit(1);
        exit(GetNextValue(MaxValue));
    end;

    procedure IntegerInRange(MinValue: Integer; MaxValue: Integer): Integer
    begin
        exit(MinValue - 1 + GetNextValue(MaxValue - MinValue + 1));
    end;

    procedure DecimalInRange(MaxValue: Integer; DecimalPlaces: Integer): Decimal
    var
        PseudoRandomInteger: Integer;
        Pow: Integer;
    begin
        Pow := Power(10, DecimalPlaces);
        PseudoRandomInteger := IntegerInRange(MaxValue * Pow);
        if PseudoRandomInteger = 0 then
            PseudoRandomInteger := 1
        else
            if PseudoRandomInteger mod 10 = 0 then
                PseudoRandomInteger -= IntegerInRange(1, 9);
        exit(PseudoRandomInteger / Pow);
    end;

    procedure DecimalInRange(MinValue: Integer; MaxValue: Integer; DecimalPlaces: Integer): Decimal
    begin
        exit(MinValue + DecimalInRange(MaxValue - MinValue, DecimalPlaces));
    end;

    procedure DecimalInRange(MinValue: Decimal; MaxValue: Decimal; DecimalPlaces: Integer): Decimal
    var
        Min: Integer;
        Max: Integer;
        Pow: Integer;
    begin
        Pow := Power(10, DecimalPlaces);
        Min := Round(MinValue * Pow, 1, '>');
        Max := Round(MaxValue * Pow, 1, '<');
        exit(IntegerInRange(Min, Max) / Pow);
    end;

    procedure DateInRange(MaxNumberOfDays: Integer): Date
    begin
        exit(DateInRange(WorkDate(), 0, MaxNumberOfDays));
    end;

    procedure DateInRange(StartingDate: Date; MaxNumberOfDays: Integer): Date
    begin
        exit(DateInRange(StartingDate, 0, MaxNumberOfDays));
    end;

    procedure DateInRange(StartingDate: Date; MinNumberOfDays: Integer; MaxNumberOfDays: Integer): Date
    begin
        if MinNumberOfDays >= MaxNumberOfDays then
            exit(StartingDate);
        exit(CalcDate(StrSubstNo('<+%1D>', IntegerInRange(MinNumberOfDays, MaxNumberOfDays)), StartingDate));
    end;

    procedure AlphabeticText(Length: Integer): Text
    var
        ASCIICodeFrom: Integer;
        ASCIICodeTo: Integer;
        Number: Integer;
        i: Integer;
        TextValue: Text;
    begin
        ASCIICodeFrom := 97;
        ASCIICodeTo := 122;
        for i := 1 to Length do begin
            Number := IntegerInRange(ASCIICodeFrom, ASCIICodeTo);
            TextValue[i] := Number;
        end;
        exit(TextValue);
    end;

    procedure AlphanumericText(Length: Integer): Text
    var
        GuidTxt: Text;
    begin
        while StrLen(GuidTxt) < Length do
            GuidTxt += LowerCase(DelChr(Format(GuidValue()), '=', '{}-'));
        exit(CopyStr(GuidTxt, 1, Length));
    end;

    procedure UnicodeText(Length: Integer) String: Text
    var
        i: Integer;
    begin
        for i := 1 to Length do
            String[i] := IntegerInRange(1072, 1103);
        exit(String);
    end;

    procedure Email(): Text
    begin
        exit(Email(20, 20));
    end;

    procedure Email(LocalPartLength: Integer; DomainLength: Integer): Text
    begin
        exit(AlphanumericText(LocalPartLength) + '@' + AlphabeticText(DomainLength) + '.' + AlphabeticText(3));
    end;

    procedure GuidValue(): Guid
    begin
        exit(CreateGuid());
    end;

    procedure SetSeed(NewSeed: Integer)
    begin
        Seed := NewSeed;
        SeedSet := true;
        Randomize(Seed);
    end;

    procedure GetSeed(): Integer
    begin
        exit(Seed);
    end;

    procedure SetDefaultSeed()
    begin
        SeedSet := true;
        SetSeed(Time() - 000000T);
    end;

    local procedure GetNextValue(MaxValue: Integer): Integer
    begin
        if not SeedSet then
            SetSeed(1);
        exit(Random(MaxValue));
    end;
}
