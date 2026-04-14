codeunit 85100 PictureFormatHelper
{
    // Returns decimal formatted with precision picture string e.g. Format(1.567, 0, '<Precision,1:2>')
    procedure FormatDecimalPrecision(Value: Decimal; MinDec: Integer; MaxDec: Integer): Text
    var
        FormatStr: Text;
    begin
        FormatStr := '<Precision,' + Format(MinDec) + ':' + Format(MaxDec) + '>';
        exit(Format(Value, 0, FormatStr));
    end;

    // Returns decimal formatted with standard format picture string e.g. Format(42, 0, '<Standard Format,0>')
    procedure FormatStandardFormat(Value: Decimal; FormatNo: Integer): Text
    begin
        exit(Format(Value, 0, '<Standard Format,' + Format(FormatNo) + '>'));
    end;

    // Returns time formatted with hours24:minutes picture string
    procedure FormatTimePicture(Value: Time): Text
    begin
        exit(Format(Value, 0, '<Hours24,2>:<Minutes,2>'));
    end;
}
