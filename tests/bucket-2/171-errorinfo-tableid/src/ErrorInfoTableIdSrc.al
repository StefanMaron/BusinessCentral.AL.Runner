/// Helper codeunit exercising ErrorInfo.TableId getter/setter.
codeunit 59920 "EIT Src"
{
    procedure SetAndGet(tableId: Integer): Integer
    var
        ei: ErrorInfo;
    begin
        ei.TableId := tableId;
        exit(ei.TableId);
    end;

    procedure FreshTableId(): Integer
    var
        ei: ErrorInfo;
    begin
        exit(ei.TableId);
    end;

    procedure LastWriteWins(a: Integer; b: Integer): Integer
    var
        ei: ErrorInfo;
    begin
        ei.TableId := a;
        ei.TableId := b;
        exit(ei.TableId);
    end;
}
