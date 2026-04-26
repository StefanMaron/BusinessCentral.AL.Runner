table 50900 "Session Work Item"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Description"; Text[100]) { }
        field(3; "Processed"; Boolean) { }
    }
    keys
    {
        key(PK; "Entry No.") { }
    }
}

// Renumbered from 50900 to avoid collision in new bucket layout (#1385).
codeunit 1050900 "Session Api"
{
    procedure TryStartSession(): Boolean
    var
        SessionId: Integer;
    begin
        // Start a session running the worker codeunit.
        exit(StartSession(SessionId, Codeunit::"Session Worker", CompanyName()));
    end;

    procedure TryStartSessionWithRecord(var SessionId: Integer): Boolean
    var
        WorkItem: Record "Session Work Item";
    begin
        WorkItem."Entry No." := 1;
        WorkItem.Description := 'Test work';
        WorkItem.Insert();
        exit(StartSession(SessionId, Codeunit::"Session Worker", CompanyName(), WorkItem));
    end;

    procedure CheckIsSessionActive(SessionId: Integer): Boolean
    begin
        exit(IsSessionActive(SessionId));
    end;

    procedure DoSleep()
    begin
        Sleep(10);
    end;

    procedure DoStopSession(SessionId: Integer)
    begin
        StopSession(SessionId);
    end;
}

// Renumbered from 50902 to avoid collision in new bucket layout (#1385).
codeunit 1050902 "Session Worker"
{
    trigger OnRun()
    begin
        // Simple no-op worker that StartSession can dispatch.
    end;
}
