/// Table and helper for RecordRef.Insert(Boolean) and Insert(Boolean, Boolean) overload tests.
table 312001 "RecRef Insert Row"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Id; Integer) { }
        field(2; Marker; Text[50]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }

    trigger OnInsert()
    begin
        Rec.Marker := 'triggered';
    end;
}

/// Helper codeunit wrapping RecordRef.Insert overloads so AL compiles each form explicitly.
codeunit 312002 "RecRef Insert Helper"
{
    /// RecordRef.Insert(Boolean) — 1-arg form; runTrigger controls whether OnInsert fires.
    procedure InsertViaRecRefBool(Id: Integer; RunTrigger: Boolean)
    var
        Rec: Record "RecRef Insert Row";
        RecRef: RecordRef;
    begin
        Rec.Init();
        Rec.Id := Id;
        RecRef.GetTable(Rec);
        RecRef.Insert(RunTrigger);
    end;

    /// RecordRef.Insert(Boolean, Boolean) — 2-arg form; BelowXRec is accepted but no-op in standalone.
    procedure InsertViaRecRefBoolBool(Id: Integer; RunTrigger: Boolean; BelowXRec: Boolean)
    var
        Rec: Record "RecRef Insert Row";
        RecRef: RecordRef;
    begin
        Rec.Init();
        Rec.Id := Id;
        RecRef.GetTable(Rec);
        RecRef.Insert(RunTrigger, BelowXRec);
    end;

    /// Read back the Marker field for a given Id.
    procedure GetMarker(Id: Integer): Text[50]
    var
        Rec: Record "RecRef Insert Row";
    begin
        if Rec.Get(Id) then
            exit(Rec.Marker);
        exit('');
    end;

    /// Return whether a record with the given Id exists.
    procedure RecordExists(Id: Integer): Boolean
    var
        Rec: Record "RecRef Insert Row";
    begin
        exit(Rec.Get(Id));
    end;
}
