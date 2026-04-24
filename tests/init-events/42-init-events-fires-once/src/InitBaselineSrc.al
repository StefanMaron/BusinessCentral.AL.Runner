/// Table used to observe that the init-event subscriber ran.
/// Has a single-integer PK so the subscriber can safely write with a
/// Get-then-Insert-or-Modify pattern.
table 57300 "Init Baseline Sentinel"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Id; Integer) { }
        field(2; Seeded; Boolean) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

/// Subscriber that seeds a sentinel row on OnCompanyInitialize.
/// Under the fixed --init-events semantics (#1220) this subscriber fires
/// exactly once per runner invocation; the resulting DB state becomes the
/// baseline that every test starts from.
codeunit 57300 "Init Baseline Subscriber"
{
    [EventSubscriber(ObjectType::Codeunit, 27, 'OnCompanyInitialize', '', false, false)]
    local procedure SeedOnCompanyInitialize()
    var
        Sentinel: Record "Init Baseline Sentinel";
    begin
        if not Sentinel.Get(1) then begin
            Sentinel.Id := 1;
            Sentinel.Seeded := true;
            Sentinel.Insert();
        end;
    end;
}
