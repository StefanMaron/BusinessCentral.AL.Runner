/// Helper codeunit exercising ErrorInfo.FieldNo getter/setter.
codeunit 59930 "EIF Src"
{
    procedure SetAndGet(fieldNo: Integer): Integer
    var
        ei: ErrorInfo;
    begin
        ei.FieldNo := fieldNo;
        exit(ei.FieldNo);
    end;

    procedure FreshFieldNo(): Integer
    var
        ei: ErrorInfo;
    begin
        exit(ei.FieldNo);
    end;

    procedure LastWriteWins(a: Integer; b: Integer): Integer
    var
        ei: ErrorInfo;
    begin
        ei.FieldNo := a;
        ei.FieldNo := b;
        exit(ei.FieldNo);
    end;
}
