table 60110 "LF Row"
{
    fields
    {
        field(1; "Id"; Integer) { }
        field(2; "Name"; Text[100]) { }
        field(3; "City"; Text[100]) { }
    }
    keys { key(PK; "Id") { Clustered = true; } }

    internal procedure SetLoadFieldsOnSelf()
    begin
        SetLoadFields("Id", "Name");
    end;

    internal procedure AddLoadFieldsOnSelf()
    begin
        AddLoadFields("Name");
    end;

    internal procedure AreFieldsLoadedOnSelf(): Boolean
    begin
        exit(AreFieldsLoaded("Id"));
    end;
}

/// Helper codeunit exercising AddLoadFields (standalone no-op because all
/// fields are always in memory). AreFieldsLoaded is exercised via the
/// RecordRef path where AL 16.2 exposes ALAreFieldsLoaded.
codeunit 60110 "LF Src"
{
    procedure AddLoadFieldsDoesNotThrow(): Boolean
    var
        r: Record "LF Row";
    begin
        r.SetLoadFields("Id", "Name");
        r.AddLoadFields("City");
        exit(true);
    end;

    procedure DataRoundTripAfterLoadFields(): Text
    var
        r: Record "LF Row";
    begin
        // Data reads must still work after SetLoadFields / AddLoadFields — no
        // partial-loading in standalone mode means every field is reachable.
        r."Id" := 1;
        r."Name" := 'Alice';
        r."City" := 'Paris';
        r.Insert();
        Clear(r);
        r.SetLoadFields("Name");
        r.AddLoadFields("City");
        r.Get(1);
        exit(r."Name" + '/' + r."City");
    end;

    procedure AddLoadFieldsMultiple_DataIntact(): Text
    var
        r: Record "LF Row";
    begin
        r."Id" := 2;
        r."Name" := 'Bob';
        r."City" := 'Berlin';
        r.Insert();
        Clear(r);
        r.SetLoadFields("Id");
        r.AddLoadFields("Name");
        r.AddLoadFields("City");
        r.Get(2);
        // "City" is reachable even though SetLoadFields didn't name it — the
        // second AddLoadFields call must be a harmless no-op.
        exit(r."City");
    end;

    procedure AddLoadFields_AfterSet_NotOverridden(): Integer
    var
        r: Record "LF Row";
        i: Integer;
    begin
        // Bulk insert + filtered find; proves that AddLoadFields doesn't
        // corrupt record state or subsequent reads.
        for i := 10 to 14 do begin
            r.Init();
            r."Id" := i;
            r."Name" := Format(i);
            r.Insert();
        end;
        Clear(r);
        r.SetLoadFields("Id");
        r.AddLoadFields("Name");
        r.SetFilter("Id", '>=%1', 12);
        exit(r.Count());
    end;

    procedure RecRefAreFieldsLoaded_ReturnsTrue(): Boolean
    var
        r: Record "LF Row";
        recRef: RecordRef;
    begin
        r."Id" := 99;
        r."Name" := 'X';
        r.Insert();
        recRef.Open(60110);
        // Standalone contract: every field is always in memory.
        exit(recRef.AreFieldsLoaded(1, 2, 3));
    end;

    procedure RecRefAreFieldsLoaded_AfterSetLoadFields(): Boolean
    var
        recRef: RecordRef;
    begin
        recRef.Open(60110);
        recRef.SetLoadFields(1);
        recRef.AddLoadFields(2);
        // Even fields not explicitly in the load set still report as loaded
        // because the standalone store keeps all fields resident.
        exit(recRef.AreFieldsLoaded(1, 2, 3));
    end;

    procedure DriveSetLoadFieldsOnSelf()
    var
        r: Record "LF Row";
    begin
        r.SetLoadFieldsOnSelf();
    end;

    procedure DriveAddLoadFieldsOnSelf()
    var
        r: Record "LF Row";
    begin
        r.AddLoadFieldsOnSelf();
    end;

    procedure DriveAreFieldsLoadedOnSelf(): Boolean
    var
        r: Record "LF Row";
    begin
        exit(r.AreFieldsLoadedOnSelf());
    end;
}
