codeunit 56490 "RR Open Probe"
{
    procedure ProbeCompany(CompanyName: Text): Integer
    var
        RecRef: RecordRef;
    begin
        // Three-arg form: TableNo, Temporary, CompanyName.
        // The RecordRef stub is compile-only; we only assert here that the
        // procedure compiles and reaches the sentinel assignment after the
        // Open/IsEmpty calls (whatever those return in the current stub).
        RecRef.Open(18, false, CompanyName);
        if RecRef.IsEmpty() then
            exit(42);
        exit(42);
    end;

    procedure ProbeLocalCompiles(): Integer
    var
        RecRef: RecordRef;
    begin
        // Single-arg form must compile. Value of IsEmpty is intentionally
        // not asserted because BC lowering across versions differs.
        RecRef.Open(18);
        if RecRef.IsEmpty() then;
        exit(7);
    end;
}
