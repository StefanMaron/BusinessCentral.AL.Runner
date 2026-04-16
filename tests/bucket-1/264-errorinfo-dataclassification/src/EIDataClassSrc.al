/// Helper codeunit that exercises ErrorInfo.DataClassification get and set.
codeunit 61950 "EI DataClass Src"
{
    procedure SetCustomerContent()
    var
        ei: ErrorInfo;
    begin
        ei.DataClassification(DataClassification::CustomerContent);
    end;

    procedure CustomerContentRoundTrips(): Boolean
    var
        ei: ErrorInfo;
    begin
        ei.DataClassification(DataClassification::CustomerContent);
        exit(ei.DataClassification() = DataClassification::CustomerContent);
    end;

    procedure TwoValuesAreDistinct(): Boolean
    var
        ei1: ErrorInfo;
        ei2: ErrorInfo;
    begin
        ei1.DataClassification(DataClassification::CustomerContent);
        ei2.DataClassification(DataClassification::SystemMetadata);
        exit(ei1.DataClassification() <> ei2.DataClassification());
    end;

    procedure SystemMetadataRoundTrips(): Boolean
    var
        ei: ErrorInfo;
    begin
        ei.DataClassification(DataClassification::SystemMetadata);
        exit(ei.DataClassification() = DataClassification::SystemMetadata);
    end;
}
