/// Helper codeunit that exercises ErrorInfo.DataClassification get and set.
codeunit 61950 "EI DataClass Src"
{
    procedure SetAndGet(): Integer
    var
        ei: ErrorInfo;
    begin
        ei.DataClassification(DataClassification::CustomerContent);
        exit(ei.DataClassification().AsInteger());
    end;

    procedure GetDefault(): Integer
    var
        ei: ErrorInfo;
    begin
        exit(ei.DataClassification().AsInteger());
    end;

    procedure SetEndUserContent(): Integer
    var
        ei: ErrorInfo;
    begin
        ei.DataClassification(DataClassification::EndUserIdentifiableInformation);
        exit(ei.DataClassification().AsInteger());
    end;
}
