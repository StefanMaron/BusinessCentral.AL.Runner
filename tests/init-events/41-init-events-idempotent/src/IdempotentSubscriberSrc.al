/// Table written to by the always-insert subscriber.
/// Has a single integer PK so a second Insert() on the same ID
/// would throw "record already exists" without a guard.
table 57200 "Idempotent Sentinel"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Id; Integer) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

/// Subscriber that ALWAYS calls Insert() without a Get-check guard.
/// This mimics real-world extensions where the developer forgot to check
/// for an existing record before inserting.
///
/// The runner fires both codeunit-2 and codeunit-27 OnCompanyInitialize
/// events in the same init cycle. If the subscriber fires on BOTH, the
/// second Insert() throws "The Idempotent Sentinel already exists."
/// The runner must catch and swallow that error so tests still run.
codeunit 57200 "Always-Insert Subscriber"
{
    [EventSubscriber(ObjectType::Codeunit, 2, 'OnCompanyInitialize', '', false, false)]
    local procedure OnCompanyInit_CU2()
    var
        Sentinel: Record "Idempotent Sentinel";
    begin
        // Unconditional Insert — no exists-check.
        // The runner must not let a duplicate-PK error abort init-event firing.
        Sentinel.Id := 1;
        Sentinel.Insert();
    end;

    [EventSubscriber(ObjectType::Codeunit, 27, 'OnCompanyInitialize', '', false, false)]
    local procedure OnCompanyInit_CU27()
    var
        Sentinel: Record "Idempotent Sentinel";
    begin
        // Second unconditional Insert on the same PK — triggers "already exists"
        // if the runner does not catch subscriber errors during init firing.
        Sentinel.Id := 1;
        Sentinel.Insert();
    end;
}
