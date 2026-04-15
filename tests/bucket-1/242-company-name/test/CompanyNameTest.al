codeunit 50242 "Company Name Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        RunnerConfig: Codeunit "AL Runner Config";

    [Test]
    procedure CompanyName_Default_IsEmpty()
    begin
        // [GIVEN] No company name configured (runner default + per-test reset)
        // [THEN] CompanyName() returns empty string
        Assert.AreEqual('', CompanyName(), 'CompanyName() must default to empty string when not configured');
    end;

    [Test]
    procedure CompanyName_Configurable_ReturnsSetValue()
    begin
        // [WHEN] SetCompanyName is called via the runner config stub
        RunnerConfig.SetCompanyName('CRONUS Test Co.');

        // [THEN] CompanyName() returns the configured value
        Assert.AreEqual('CRONUS Test Co.', CompanyName(), 'CompanyName() must return the configured company name');
    end;

    [Test]
    procedure CompanyName_SetToEmpty_ReturnsEmpty()
    begin
        // [GIVEN] A name was set in a previous call
        RunnerConfig.SetCompanyName('SomeCo');
        Assert.AreEqual('SomeCo', CompanyName(), 'Sanity: SomeCo set');

        // [WHEN] Cleared via empty string
        RunnerConfig.SetCompanyName('');

        // [THEN] CompanyName() returns empty
        Assert.AreEqual('', CompanyName(), 'CompanyName() must return empty after being cleared');
    end;

    [Test]
    procedure CompanyName_ResetBetweenTests_RestoresDefault()
    begin
        // This test runs after the configurable test — per-test reset must restore default ('').
        // Without reset, the previous test's value would leak here.
        Assert.AreEqual('', CompanyName(), 'Per-test reset must restore CompanyName() to default empty string');
    end;

    [Test]
    procedure CompanyName_UsedInStrSubstNo()
    begin
        RunnerConfig.SetCompanyName('Acme Ltd');
        Assert.AreEqual('Hello from Acme Ltd', StrSubstNo('Hello from %1', CompanyName()), 'CompanyName() must compose correctly in StrSubstNo');
    end;
}
