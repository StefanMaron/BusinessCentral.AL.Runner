codeunit 96001 "Util Helper"
{
    procedure GetClosingDate(d: Date): Date
    begin
        exit(ClosingDate(d));
    end;

    procedure GetNormalDate(d: Date): Date
    begin
        exit(NormalDate(d));
    end;

    procedure GetProductNameFull(): Text
    begin
        exit(ProductName.Full());
    end;

    procedure GetProductNameShort(): Text
    begin
        exit(ProductName.Short());
    end;

    procedure GetProductNameMarketing(): Text
    begin
        exit(ProductName.Marketing());
    end;

    procedure GetCompanyPropertyDisplayName(): Text
    begin
        exit(CompanyProperty.DisplayName());
    end;

    procedure GetCompanyPropertyUrlName(): Text
    begin
        exit(CompanyProperty.UrlName());
    end;

    procedure CallLogMessage()
    begin
        Session.LogMessage('TEST01', 'Test telemetry message', Verbosity::Normal, DataClassification::SystemMetadata, TelemetryScope::ExtensionPublisher, 'key1', 'val1');
    end;

    procedure CallLogMessageWarning()
    begin
        Session.LogMessage('WARN01', 'Warning message', Verbosity::Warning, DataClassification::CustomerContent, TelemetryScope::All, 'dim', 'dimval');
    end;

    procedure GetRoundedDateTime(dt: DateTime): DateTime
    begin
        exit(RoundDateTime(dt));
    end;

    procedure GetRoundedDateTimePrecision(dt: DateTime; precision: BigInteger): DateTime
    begin
        exit(RoundDateTime(dt, precision));
    end;

    procedure GetRoundedDateTimeDirection(dt: DateTime; precision: BigInteger; direction: Text[1]): DateTime
    begin
        exit(RoundDateTime(dt, precision, direction));
    end;

    procedure GetApplicationArea(): Text
    begin
        exit(Session.ApplicationArea());
    end;

    procedure CallLockTimeout(enable: Boolean)
    begin
        Database.LockTimeout(enable);
    end;

    procedure GetExecutionContext(): ExecutionContext
    begin
        exit(Session.GetExecutionContext());
    end;

    procedure GetModuleExecutionContext(): ExecutionContext
    begin
        exit(Session.GetModuleExecutionContext());
    end;
}
