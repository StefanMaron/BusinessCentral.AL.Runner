/// Table used to record that the init-event subscriber fired.
table 57100 "Init Events Sentinel"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Id; Integer) { }
        field(2; Fired; Boolean) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

/// Subscriber that listens to OnCompanyInitialize (Codeunit 27).
/// When --init-events fires OnCompanyInitialize before tests run,
/// this subscriber sets the sentinel record so the test can detect it.
codeunit 57100 "Init Events Subscriber"
{
    [EventSubscriber(ObjectType::Codeunit, 27, 'OnCompanyInitialize', '', false, false)]
    local procedure HandleOnCompanyInitialize()
    var
        Sentinel: Record "Init Events Sentinel";
    begin
        if not Sentinel.Get(1) then begin
            Sentinel.Id := 1;
            Sentinel.Fired := true;
            Sentinel.Insert();
        end else begin
            Sentinel.Fired := true;
            Sentinel.Modify();
        end;
    end;
}
