/// Exercises Session methods not covered by suite 190:
/// ApplicationArea, GetExecutionContext, GetModuleExecutionContext,
/// GetCurrentModuleExecutionContext, SendTraceTag, LogSecurityAudit,
/// EnableVerboseTelemetry, ApplicationIdentifier, SetDocumentServiceToken.
codeunit 60270 "SXE Src"
{
    procedure GetAppArea(): Text
    begin
        exit(Session.ApplicationArea());
    end;

    procedure GetExecContext(): ExecutionContext
    begin
        exit(Session.GetExecutionContext());
    end;

    procedure GetModuleExecContext(): ExecutionContext
    var
        id: Guid;
    begin
        exit(Session.GetModuleExecutionContext(id));
    end;

    procedure GetCurrentModuleExecContext(): ExecutionContext
    begin
        exit(Session.GetCurrentModuleExecutionContext());
    end;

    procedure SendTraceTag_DoesNotThrow(): Boolean
    begin
        Session.SendTraceTag('TAG001', 'MyCategory', Verbosity::Normal,
            'test message', DataClassification::SystemMetadata);
        exit(true);
    end;

    procedure LogSecurityAudit_DoesNotThrow(): Boolean
    begin
        Session.LogSecurityAudit('securityEvent', SecurityOperationResult::Success,
            'description', AuditCategory::UserManagement);
        exit(true);
    end;

    procedure EnableVerboseTelemetry_DoesNotThrow(): Boolean
    begin
        Session.EnableVerboseTelemetry(true, 60000);
        exit(true);
    end;

    procedure ApplicationIdentifier_DoesNotThrow(): Text
    begin
        exit(Session.ApplicationIdentifier());
    end;
}
