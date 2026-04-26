/// Exercises Variant Is* type-checking methods.
codeunit 60340 "VIT Src"
{
    procedure IsIntegerCheck(v: Variant): Boolean
    begin
        exit(v.IsInteger());
    end;

    procedure IsTextCheck(v: Variant): Boolean
    begin
        exit(v.IsText());
    end;

    procedure IsBooleanCheck(v: Variant): Boolean
    begin
        exit(v.IsBoolean());
    end;

    procedure IsDecimalCheck(v: Variant): Boolean
    begin
        exit(v.IsDecimal());
    end;

    procedure IsDateCheck(v: Variant): Boolean
    begin
        exit(v.IsDate());
    end;

    procedure IsDateTimeCheck(v: Variant): Boolean
    begin
        exit(v.IsDateTime());
    end;

    procedure IsTimeCheck(v: Variant): Boolean
    begin
        exit(v.IsTime());
    end;

    procedure IsGuidCheck(v: Variant): Boolean
    begin
        exit(v.IsGuid());
    end;

    procedure IsCodeCheck(v: Variant): Boolean
    begin
        exit(v.IsCode());
    end;

    procedure IsOptionCheck(v: Variant): Boolean
    begin
        exit(v.IsOption());
    end;
}
