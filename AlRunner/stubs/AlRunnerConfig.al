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
}
