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
}
