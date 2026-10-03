/// <summary>
/// #5286: a table whose OnModify trigger comes from a table extension that names it by id, not by name: the
/// trigger runs for this table.
/// </summary>
table 72307 "Trg Ext Rec"
{
    fields { field(1; PK; Code[20]) { } }
    keys { key(PK; PK) { Clustered = true; } }
}

tableextension 72350 "Trg Ext By Id" extends 72307
{
    trigger OnModify()
    var
        Target: Codeunit "Trg Target";
        Result: Integer;
    begin
        Result := Target.MissingExt(1);
    end;
}
