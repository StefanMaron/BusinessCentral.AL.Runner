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
        ViaInterface: Interface "SIL Counter";
        ViaVariant: Variant;
        ViaList: List of [Interface "SIL Counter"];
        ViaDictionary: Dictionary of [Integer, Interface "SIL Counter"];

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
    var
        ForInterface: Codeunit "SIL Held";
        ForVariant: Codeunit "SIL Held";
        ForList: Codeunit "SIL Held";
        ForDictionary: Codeunit "SIL Held";
    begin
        Row.SetFilter("Key", '<>ZZZ');
        Rows[2].SetRange(Val, 0);
        RRef.Open(Database::"SIL Row");
        Held.Touch();
        Deep.Touch();
        // Each assigned from a local, so the SingleInstance field is the only holder left.
        ForInterface.Touch();
        ViaInterface := ForInterface;
        ForVariant.Touch();
        ViaVariant := ForVariant;
        ForList.Touch();
        ViaList.Add(ForList);
        ForDictionary.Touch();
        ViaDictionary.Add(1, ForDictionary);
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

    // interface / variant / list / dictionary, in that order
    procedure CountIndirect(): Text
    var
        FromVariant: Codeunit "SIL Held";
    begin
        FromVariant := ViaVariant;
        exit(StrSubstNo('%1/%2/%3/%4', ViaInterface.CountRows(), FromVariant.CountRows(),
            ViaList.Get(1).CountRows(), ViaDictionary.Get(1).CountRows()));
    end;

    procedure Filters(): Text
    begin
        exit(Row.GetFilters() + '|' + Rows[2].GetFilters());
    end;
}
