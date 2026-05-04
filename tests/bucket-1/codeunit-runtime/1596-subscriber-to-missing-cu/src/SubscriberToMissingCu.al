// Source for the "subscriber to in-slice publisher fires correctly" test (issue #1596).
//
// Issue #1596: compile-dep now drops [EventSubscriber] attributes that reference
// codeunits not in the slice.  These tests verify that when the publisher IS in
// the slice, the subscriber still fires correctly at runtime.
//
// Object IDs 159600-159602 (table 159600).

table 159600 "SMC Event Log"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; AfterFireCount; Integer) { }
        field(3; LastInput; Integer) { }
        field(4; LastResult; Integer) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

codeunit 159600 "SMC Publisher"
{
    [IntegrationEvent(false, false)]
    procedure OnAfterCalculate(Input: Integer; Result: Integer)
    begin
    end;

    procedure Calculate(Input: Integer): Integer
    var
        Output: Integer;
    begin
        Output := Input * 2;
        OnAfterCalculate(Input, Output);
        exit(Output);
    end;
}

codeunit 159601 "SMC Subscriber"
{
    // Normal subscriber to a publisher that IS in the slice — must fire at runtime.
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"SMC Publisher", 'OnAfterCalculate', '', true, true)]
    local procedure OnAfterCalculateHandler(Input: Integer; Result: Integer)
    var
        Log: Record "SMC Event Log";
    begin
        if not Log.Get(1) then begin
            Log.PK := 1;
            Log.AfterFireCount := 1;
            Log.LastInput := Input;
            Log.LastResult := Result;
            Log.Insert();
        end else begin
            Log.AfterFireCount += 1;
            Log.LastInput := Input;
            Log.LastResult := Result;
            Log.Modify();
        end;
    end;
}

codeunit 159602 "SMC Log Helper"
{
    procedure Reset()
    var
        Log: Record "SMC Event Log";
    begin
        if Log.Get(1) then
            Log.Delete();
    end;

    procedure GetFireCount(): Integer
    var
        Log: Record "SMC Event Log";
    begin
        if Log.Get(1) then
            exit(Log.AfterFireCount);
        exit(0);
    end;

    procedure GetLastInput(): Integer
    var
        Log: Record "SMC Event Log";
    begin
        if Log.Get(1) then
            exit(Log.LastInput);
        exit(0);
    end;

    procedure GetLastResult(): Integer
    var
        Log: Record "SMC Event Log";
    begin
        if Log.Get(1) then
            exit(Log.LastResult);
        exit(0);
    end;
}
