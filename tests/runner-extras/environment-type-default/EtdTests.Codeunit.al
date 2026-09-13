// Issue #3514. The runner's tenant must not claim to be a sandbox (and so SaaS) on its own.
//
// System Application codeunit 3702 answers:
//   IsSandbox() = TestabilitySandbox or NavTenantSettingsHelper.IsSandbox()
//   IsSaaS()    = TestabilitySoftwareAsAService or IsSandbox() or <membership entitlement>
// and NavTenantSettingsHelper.IsSandbox() is true only when the tenant's configured
// EnvironmentType is Sandbox, or when a test has called SetTestTenantEnvironmentType(true).
// Codeunit 3702 is SingleInstance and latches both answers on first read, so each test below
// reads the predicate the setter it exercises actually drives, and the DotNet helper directly
// where the latch would otherwise hide the change.
codeunit 65961 "ETD Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "ETD Assert";

    [Test]
    procedure EtdDefaultTenantIsNotASandbox()
    var
        TenantSettingsHelper: DotNet NavTenantSettingsHelper;
    begin
        Assert.AreEqual(false, TenantSettingsHelper.IsSandbox(), 'no test asked for a sandbox, so the tenant setting decides');
        Assert.AreEqual(true, TenantSettingsHelper.IsProduction(), 'IsProduction is the negation of IsSandbox');
    end;

    [Test]
    procedure EtdDefaultEnvironmentIsNotSaaS()
    var
        EnvironmentInformation: Codeunit "Environment Information";
    begin
        Assert.AreEqual(false, EnvironmentInformation.IsSandbox(), 'Environment Information must not report a sandbox by default');
        Assert.AreEqual(false, EnvironmentInformation.IsSaaS(), 'IsSaaS follows IsSandbox, so it must be false by default');
    end;

    [Test]
    procedure EtdSetTestTenantEnvironmentTypeIsHonored()
    var
        TenantSettingsHelper: DotNet NavTenantSettingsHelper;
    begin
        TenantSettingsHelper.SetTestTenantEnvironmentType(true);
        Assert.AreEqual(true, TenantSettingsHelper.IsSandbox(), 'SetTestTenantEnvironmentType(true) must make the tenant a sandbox');
        TenantSettingsHelper.SetTestTenantEnvironmentType(false);
        Assert.AreEqual(false, TenantSettingsHelper.IsSandbox(), 'SetTestTenantEnvironmentType(false) must restore the tenant setting');
    end;

    [Test]
    procedure EtdSetTestabilitySoftwareAsAServiceIsHonored()
    var
        EnvironmentInformation: Codeunit "Environment Information";
        EnvironmentInfoTestLibrary: Codeunit "Environment Info Test Library";
        Before: Boolean;
    begin
        Before := EnvironmentInformation.IsSaaS();
        EnvironmentInfoTestLibrary.SetTestabilitySoftwareAsAService(true);
        Assert.AreEqual(true, EnvironmentInformation.IsSaaS(), 'SetTestabilitySoftwareAsAService(true) must make IsSaaS true');
        EnvironmentInfoTestLibrary.SetTestabilitySoftwareAsAService(false);
        Assert.AreEqual(Before, EnvironmentInformation.IsSaaS(), 'SetTestabilitySoftwareAsAService(false) must restore the earlier answer');
    end;
}

dotnet
{
    assembly("Microsoft.Dynamics.Nav.NavUserAccount")
    {
        type("Microsoft.Dynamics.Nav.NavUserAccount.NavTenantSettingsHelper"; "NavTenantSettingsHelper")
        {
        }
    }
}
