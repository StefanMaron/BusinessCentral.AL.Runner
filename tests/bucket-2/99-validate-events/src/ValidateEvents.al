table 59901 "VTE Source"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; Amount; Decimal)
        {
            trigger OnValidate()
            begin
                // Normal trigger body — runs when runTrigger=true
            end;
        }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

table 59902 "VTE Counter"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; BeforeCount; Integer) { }
        field(3; AfterCount; Integer) { }
        field(4; LastFieldNo; Integer) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

codeunit 59901 "VTE Subscriber"
{
    [EventSubscriber(ObjectType::Table, Database::"VTE Source", OnBeforeValidateEvent, '', true, true)]
    local procedure OnBeforeValidate(var Rec: Record "VTE Source"; var xRec: Record "VTE Source"; CurrFieldNo: Integer)
    var
        C: Record "VTE Counter";
    begin
        if not C.Get(1) then begin
            C.PK := 1;
            C.BeforeCount := 1;
            C.LastFieldNo := CurrFieldNo;
            C.Insert();
        end else begin
            C.BeforeCount += 1;
            C.LastFieldNo := CurrFieldNo;
            C.Modify();
        end;
    end;

    [EventSubscriber(ObjectType::Table, Database::"VTE Source", OnAfterValidateEvent, '', true, true)]
    local procedure OnAfterValidate(var Rec: Record "VTE Source"; var xRec: Record "VTE Source"; CurrFieldNo: Integer)
    var
        C: Record "VTE Counter";
    begin
        if not C.Get(1) then begin
            C.PK := 1;
            C.AfterCount := 1;
            C.LastFieldNo := CurrFieldNo;
            C.Insert();
        end else begin
            C.AfterCount += 1;
            C.LastFieldNo := CurrFieldNo;
            C.Modify();
        end;
    end;
}
