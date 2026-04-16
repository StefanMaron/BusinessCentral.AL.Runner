/// Helper codeunit exercising the Session built-in's static methods.
codeunit 60160 "SES Src"
{
    procedure GetClientType(): ClientType
    begin
        exit(Session.CurrentClientType());
    end;

    procedure GetExecutionMode(): ExecutionMode
    begin
        exit(Session.CurrentExecutionMode());
    end;

    procedure GetDefaultClientType(): ClientType
    begin
        exit(Session.DefaultClientType());
    end;

    procedure LogMessageDoesNotThrow(): Boolean
    var
        dims: Dictionary of [Text, Text];
    begin
        dims.Add('source', 'test');
        Session.LogMessage('TAG001', 'test message', Verbosity::Normal,
            DataClassification::SystemMetadata, TelemetryScope::All, dims);
        exit(true);
    end;

    procedure LogAuditMessageDoesNotThrow(): Boolean
    begin
        Session.LogAuditMessage('audit', SecurityOperationResult::Success,
            AuditCategory::UserManagement, 1, 1);
        exit(true);
    end;
}
