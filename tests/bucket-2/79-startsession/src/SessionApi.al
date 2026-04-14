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

codeunit 50900 "Session Api"
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

codeunit 50902 "Session Worker"
{
    trigger OnRun()
    begin
        // Simple no-op worker that StartSession can dispatch.
    end;
}
