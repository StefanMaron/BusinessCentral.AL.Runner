// Stub for the al-runner-only configuration codeunit.
// At runtime, MockCodeunitHandle routes codeunit 131100 calls to MockSession.
codeunit 131100 "AL Runner Config"
{
    /// <summary>
    /// Sets the value that CompanyName() will return.
    /// Resets to the CLI --company-name default between tests.
    /// </summary>
    procedure SetCompanyName(Name: Text)
    begin
    end;

    /// <summary>
    /// Returns the configured company name (same value as CompanyName()).
    /// </summary>
    procedure GetCompanyName(): Text
    begin
    end;

    /// <summary>
    /// Sets the value that CompanyProperty.DisplayName() will return.
    /// Resets to the default between tests.
    /// </summary>
    procedure SetCompanyDisplayName(Name: Text)
    begin
    end;

    /// <summary>
    /// Returns the configured CompanyProperty.DisplayName() value.
    /// </summary>
    procedure GetCompanyDisplayName(): Text
    begin
    end;

    /// <summary>
    /// Sets the value that CompanyProperty.UrlName() will return.
    /// Resets to the default between tests.
    /// </summary>
    procedure SetCompanyUrlName(Name: Text)
    begin
    end;

    /// <summary>
    /// Returns the configured CompanyProperty.UrlName() value.
    /// </summary>
    procedure GetCompanyUrlName(): Text
    begin
    end;

    /// <summary>
    /// Sets the GUID that CompanyProperty.ID() will return.
    /// Resets to the default between tests.
    /// </summary>
    procedure SetCompanyId(Id: Guid)
    begin
    end;

    /// <summary>
    /// Returns the configured CompanyProperty.ID() GUID.
    /// </summary>
    procedure GetCompanyId(): Guid
    begin
    end;

    /// <summary>
    /// Sets the BCP 47 culture code used by Format(Date) (no format number).
    /// Default is '' (empty) which produces ISO-8601 (yyyy-MM-dd).
    /// Set to a culture code such as 'en-US' or 'de-DE' to match your BC
    /// container's session locale.
    /// Equivalent to the --date-locale CLI flag / ALRUNNER_DATE_LOCALE env var.
    /// </summary>
    procedure SetDateLocale(CultureCode: Text)
    begin
    end;

    /// <summary>
    /// Returns the current date locale culture code ('' = ISO-8601 default).
    /// </summary>
    procedure GetDateLocale(): Text
    begin
    end;

    /// <summary>
    /// Formats a Date value using the configured date locale.
    /// Equivalent to Format(d) but explicitly routed through the configured locale.
    /// </summary>
    procedure FormatDate(d: Date): Text
    begin
    end;
}
