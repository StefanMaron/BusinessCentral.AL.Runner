codeunit 71921 "SIL Single"
{
    SingleInstance = true;

    var
        Bumps: Integer;
        InstallMarks: Integer;
        Row: Record "SIL Row";
        Rows: array[2] of Record "SIL Row";
        RRef: RecordRef;
        Held: Codeunit "SIL Held";
        Deep: Codeunit "SIL Deep 1";

    procedure Bump()
    begin
        Bumps += 1;
    end;

    procedure GetBumps(): Integer
    begin
        exit(Bumps);
    end;

    procedure MarkInstall()
    begin
        InstallMarks += 1;
    end;

    procedure GetInstallMarks(): Integer
    begin
        exit(InstallMarks);
    end;

    procedure OpenAll()
    begin
        Row.SetFilter("Key", '<>ZZZ');
        Rows[2].SetRange(Val, 0);
        RRef.Open(Database::"SIL Row");
        Held.Touch();
        Deep.Touch();
    end;

    procedure InsertRow(NewKey: Code[10])
    begin
        Row.Init();
        Row."Key" := NewKey;
        Row.Insert();
    end;

    procedure CountAll(): Text
    begin
        exit(StrSubstNo('%1/%2/%3/%4/%5', Row.Count(), Rows[2].Count(), RRef.Count(), Held.CountRows(), Deep.CountRows()));
    end;

    procedure Filters(): Text
    begin
        exit(Row.GetFilters() + '|' + Rows[2].GetFilters());
    end;
}
