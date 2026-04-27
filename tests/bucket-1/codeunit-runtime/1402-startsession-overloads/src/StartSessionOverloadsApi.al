// Supporting codeunits for testing StartSession overloads with company and record args.

codeunit 1316001 "StartSession Overloads Worker"
{
    trigger OnRun()
    begin
        // No-op worker codeunit dispatched via various StartSession overloads.
    end;
}

codeunit 1316002 "StartSession Overloads Api"
{
    procedure StartWithCompany(var SessionId: Integer): Boolean
    begin
        // 3-arg form: StartSession(var SessionId, CodeunitID, Company)
        exit(StartSession(SessionId, Codeunit::"StartSession Overloads Worker", CompanyName()));
    end;

    procedure StartWithCompanyAndRecord(var SessionId: Integer; var Rec: Record "StartSession Overloads Rec"): Boolean
    begin
        // 4-arg form: StartSession(var SessionId, CodeunitID, Company, Record)
        exit(StartSession(SessionId, Codeunit::"StartSession Overloads Worker", CompanyName(), Rec));
    end;

    procedure StartWithCompanyRecordAndTimeout(var SessionId: Integer; var Rec: Record "StartSession Overloads Rec"): Boolean
    var
        Timeout: Duration;
    begin
        // 5-arg form: StartSession(var SessionId, CodeunitID, Company, Record, Timeout)
        Timeout := 30000; // 30 seconds
        exit(StartSession(SessionId, Codeunit::"StartSession Overloads Worker", CompanyName(), Rec, Timeout));
    end;
}
