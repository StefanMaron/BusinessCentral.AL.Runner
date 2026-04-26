table 60050 "CLR Row"
{
    fields
    {
        field(1; "Id"; Integer) { }
        field(2; "Name"; Text[100]) { }
    }
    keys { key(PK; "Id") { Clustered = true; } }
}

/// Helper codeunit exercising Clear() and ClearAll().
codeunit 60050 "CLR Src"
{
    var
        GlobalText: Text;
        GlobalInt: Integer;

    procedure SetGlobals(t: Text; n: Integer)
    begin
        GlobalText := t;
        GlobalInt := n;
    end;

    procedure ClearTextAndReturn(v: Text): Text
    begin
        Clear(v);
        exit(v);
    end;

    procedure ClearIntAndReturn(v: Integer): Integer
    begin
        Clear(v);
        exit(v);
    end;

    procedure ClearDecimalAndReturn(v: Decimal): Decimal
    begin
        Clear(v);
        exit(v);
    end;

    procedure ClearBooleanAndReturn(v: Boolean): Boolean
    begin
        Clear(v);
        exit(v);
    end;

    procedure ClearDateAndReturn(v: Date): Date
    begin
        Clear(v);
        exit(v);
    end;

    procedure ClearRecordReturnsEmptyFields(): Boolean
    var
        r: Record "CLR Row";
    begin
        r."Id" := 7;
        r."Name" := 'something';
        Clear(r);
        exit((r."Id" = 0) and (r."Name" = ''));
    end;

    procedure ClearAllClearsBothGlobals(): Boolean
    begin
        GlobalText := 'hello';
        GlobalInt := 42;
        ClearAll();
        exit((GlobalText = '') and (GlobalInt = 0));
    end;

    procedure ClearListAndReturnCount(): Integer
    var
        list: List of [Integer];
    begin
        list.Add(1);
        list.Add(2);
        list.Add(3);
        Clear(list);
        exit(list.Count());
    end;
}
