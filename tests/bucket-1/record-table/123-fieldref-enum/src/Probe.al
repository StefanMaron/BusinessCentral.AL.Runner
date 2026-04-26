codeunit 56230 "FE Probe"
{
    procedure FieldRefIsEnum(TableId: Integer; FieldNo: Integer): Boolean
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableId);
        FldRef := RecRef.Field(FieldNo);
        exit(FldRef.IsEnum);
    end;

    procedure FieldRefEnumValueCount(TableId: Integer; FieldNo: Integer): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableId);
        FldRef := RecRef.Field(FieldNo);
        exit(FldRef.EnumValueCount);
    end;

    procedure FieldRefGetEnumValueName(TableId: Integer; FieldNo: Integer; Index: Integer): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableId);
        FldRef := RecRef.Field(FieldNo);
        exit(FldRef.GetEnumValueName(Index));
    end;

    procedure FieldRefGetEnumValueCaption(TableId: Integer; FieldNo: Integer; Index: Integer): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableId);
        FldRef := RecRef.Field(FieldNo);
        exit(FldRef.GetEnumValueCaption(Index));
    end;

    procedure FieldRefGetEnumValueOrdinal(TableId: Integer; FieldNo: Integer; Index: Integer): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableId);
        FldRef := RecRef.Field(FieldNo);
        exit(FldRef.GetEnumValueOrdinal(Index));
    end;

    procedure CalcSumPrice(TableId: Integer; FieldNo: Integer): Decimal
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableId);
        FldRef := RecRef.Field(FieldNo);
        FldRef.CalcSum();
        exit(FldRef.Value);
    end;

    procedure CalcSumQuantity(TableId: Integer; FieldNo: Integer): Decimal
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableId);
        FldRef := RecRef.Field(FieldNo);
        FldRef.CalcSum();
        exit(FldRef.Value);
    end;

    procedure CalcSumPriceWithFilter(TableId: Integer; PriceFieldNo: Integer; IdFieldNo: Integer; MinId: Integer): Decimal
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        IdFld: FieldRef;
    begin
        RecRef.Open(TableId);
        IdFld := RecRef.Field(IdFieldNo);
        IdFld.SetFilter('>=%1', MinId);
        FldRef := RecRef.Field(PriceFieldNo);
        FldRef.CalcSum();
        exit(FldRef.Value);
    end;

    procedure SystemFieldNos(): Text
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(56230);
        exit(Format(RecRef.SystemIdNo) + ',' +
             Format(RecRef.SystemCreatedAtNo) + ',' +
             Format(RecRef.SystemCreatedByNo) + ',' +
             Format(RecRef.SystemModifiedAtNo) + ',' +
             Format(RecRef.SystemModifiedByNo));
    end;

    procedure WritePermissionTest(var Rec: Record "FE Test Item"): Boolean
    begin
        exit(Rec.WritePermission);
    end;
}
