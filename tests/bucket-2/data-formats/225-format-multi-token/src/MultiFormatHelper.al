codeunit 50225 "Multi Format Helper"
{
    procedure FormatPrecisionStd(Value: Decimal): Text
    begin
        exit(Format(Value, 0, '<Precision,2:2><Standard Format,0>'));
    end;

    procedure FormatDatePrecision(MyDate: Date; MyDec: Decimal): Text
    begin
        exit(Format(MyDate, 0, '<Year4>-<Month,2>-<Day,2>') + ' ' + Format(MyDec, 0, '<Precision,2:2><Standard Format,0>'));
    end;

    procedure FormatPrecisionOnly(Value: Decimal): Text
    begin
        exit(Format(Value, 0, '<Precision,2:2>'));
    end;

    procedure FormatUnknownToken(Value: Decimal): Text
    begin
        // Unknown/unsupported token — runner must not silently replace with ''
        exit(Format(Value, 0, '<NotARealToken>'));
    end;
}
