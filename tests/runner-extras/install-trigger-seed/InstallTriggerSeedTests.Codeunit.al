// Proves the runner fires Subtype=Install codeunit lifecycle triggers once per
// bundle BEFORE the first [Test] runs — modelling a freshly-installed app,
// exactly like real BC's NavAppInstallationProcessor raising
// OnInstallAppPerDatabase / OnInstallAppPerCompany on install.
//
// RED (before the fix): the runner fired NO install triggers, so table 60710
// had 0 rows and every seeding assertion here failed — the same gap that made
// Pageworks's PageworksInstall never seed PageworksPageSize (162 test failures
// on 'page-size has unsupported value').
//
// GREEN (after the fix): 'DATABASE' (per-database trigger), 'COMPANY1' and
// 'COMPANY2' (per-company trigger) exist with exact values; the look-alike
// procedure on the NORMAL codeunit 60712 did NOT run (no 'ROGUE' row, count
// stays exactly 3) — the step is scoped to Subtype=Install, not name-matched.
codeunit 60714 "ITS Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "ITS Assert";

    [Test]
    procedure SeededPerCompanyRowsExist()
    var
        Seed: Record "Install Seed";
    begin
        Seed.Get('COMPANY1');
        Assert.AreEqual(11, Seed."Value", 'OnInstallAppPerCompany must have seeded COMPANY1 with Value 11');
        Seed.Get('COMPANY2');
        Assert.AreEqual(22, Seed."Value", 'OnInstallAppPerCompany must have seeded COMPANY2 with Value 22');
    end;

    [Test]
    procedure SeededPerDatabaseRowExists()
    var
        Seed: Record "Install Seed";
    begin
        Seed.Get('DATABASE');
        Assert.AreEqual(99, Seed."Value", 'OnInstallAppPerDatabase must have seeded DATABASE with Value 99');
    end;

    [Test]
    procedure ExactlyTheThreeSeededRowsExist()
    var
        Seed: Record "Install Seed";
    begin
        Assert.AreEqual(3, Seed.Count(), 'install triggers must have seeded exactly 3 rows (1 per-database + 2 per-company)');
    end;

    [Test]
    procedure NonInstallCodeunitDidNotAutoRun()
    var
        Seed: Record "Install Seed";
    begin
        // Codeunit 60712 is Subtype=Normal but has a public procedure named
        // OnInstallAppPerCompany. If the runner matched by method name instead
        // of Subtype=Install, a 'ROGUE' row would exist.
        Assert.IsFalse(Seed.Get('ROGUE'), 'the look-alike procedure on a NON-Install codeunit must not auto-run');
    end;

    [Test]
    procedure UnseededRowRaisesExpectedError()
    var
        Seed: Record "Install Seed";
    begin
        asserterror RequireRow('MISSING');
        Assert.ExpectedError('row MISSING was not seeded', GetLastErrorText());
    end;

    local procedure RequireRow("Code": Code[20])
    var
        Seed: Record "Install Seed";
    begin
        if not Seed.Get("Code") then
            Error('row %1 was not seeded', "Code");
    end;
}
