enum 60020 "FREO Status"
{
    Extensible = true;
    value(0; Open) { Caption = 'Open'; }
    value(5; InProgress) { Caption = 'In Progress'; }
    value(10; Closed) { Caption = 'Closed'; }
}

table 60020 "FREO Order"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Status; Enum "FREO Status") { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// Helper codeunit exercising
/// FieldRef.GetEnumValueCaptionFromOrdinalValue / GetEnumValueNameFromOrdinalValue.
/// These look up metadata by the enum's ORDINAL value, not by 1-based index.
codeunit 60020 "FREO Src"
{
    procedure CaptionForOrdinal(ordinal: Integer): Text
    var
        Order: Record "FREO Order";
        RecRef: RecordRef;
        FRef: FieldRef;
    begin
        Order.Init();
        Order."No." := 'O1';
        Order.Insert();
        RecRef.GetTable(Order);
        FRef := RecRef.Field(Order.FieldNo(Status));
        exit(FRef.GetEnumValueCaptionFromOrdinalValue(ordinal));
    end;

    procedure NameForOrdinal(ordinal: Integer): Text
    var
        Order: Record "FREO Order";
        RecRef: RecordRef;
        FRef: FieldRef;
    begin
        Order.Init();
        Order."No." := 'O2';
        Order.Insert();
        RecRef.GetTable(Order);
        FRef := RecRef.Field(Order.FieldNo(Status));
        exit(FRef.GetEnumValueNameFromOrdinalValue(ordinal));
    end;
}
