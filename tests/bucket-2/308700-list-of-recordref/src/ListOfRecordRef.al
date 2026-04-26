table 308700 "List RecordRef Table"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Function Id"; Integer) { }
    }
    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

codeunit 308700 "List Of RecordRef Helper"
{
    /// Reproduces CS1503 from issue #1335:
    /// A procedure with List of [RecordRef] var param and Integer param.
    /// The BC compiler emits C# where the list parameter uses a type that
    /// the runner must handle correctly.
    procedure GetRecRef(var Rec: RecordRef; var RelatedRefs: List of [RecordRef]; FunctionId: Integer; var CurrentRef: RecordRef): Boolean
    begin
        exit(FunctionId > 0);
    end;

    /// Caller passes Integer field (matching telemetry: TempFunctionField2."Function Id")
    /// and a List of [RecordRef] var parameter — exact pattern from issue #1335.
    procedure CallGetRecRef(var TempTable: Record "List RecordRef Table" temporary): Boolean
    var
        Rec: RecordRef;
        RelatedRefs: List of [RecordRef];
        CurrentRef: RecordRef;
    begin
        exit(GetRecRef(Rec, RelatedRefs, TempTable."Function Id", CurrentRef));
    end;

    /// Assign a new list to a var List of [RecordRef] parameter.
    procedure UpdateList(var Refs: List of [RecordRef])
    var
        NewList: List of [RecordRef];
    begin
        Refs := NewList;
    end;
}
