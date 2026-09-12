// Issue #3400. The runner has no active BC company, so it chooses one: "My Company".
// The corpus pins what a service tier guarantees (CompanyName() non-empty, equal to
// CurrentCompany(), found in Company); the literal is a runner invention, so it is pinned here.
// GuiAllowed() = true is NOT pinned here: real BC answers true in a test session too, and corpus
// codeunit 60173 GuiAllowed_InTestContext_ReturnsTrue asserts it (see docs/limitations.md).
codeunit 62221 "SCD Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "SCD Assert";
        RunnerCompanyTok: Label 'My Company', Locked = true;

    [Test]
    procedure ScdCompanyNameIsTheRunnerLiteral()
    begin
        // Written by the skeleton NavCompany seed in AlRunner/BcRuntime.cs.
        Assert.AreEqual(RunnerCompanyTok, CompanyName(), 'CompanyName()');
    end;

    [Test]
    procedure ScdCurrentCompanyIsTheRunnerLiteral()
    var
        Probe: Record "SCD Probe";
    begin
        // The record store partitions by token '' (Patches/CompanyAccessPatches.cs); the name a
        // record reports must still be the display name, not the partition token.
        Assert.AreEqual(RunnerCompanyTok, Probe.CurrentCompany(), 'Record.CurrentCompany()');
    end;

    [Test]
    procedure ScdCompanyRowNamesTheSameCompany()
    var
        Company: Record Company;
    begin
        // A second writer, the Company (2000000006) row seed. Changing one seed without the
        // other turns this red while the two tests above stay green.
        Assert.IsTrue(Company.Get(RunnerCompanyTok), 'Company.Get(''My Company'') must find the seeded row');
        Assert.AreEqual(RunnerCompanyTok, Company.Name, 'Company.Name');
        Assert.AreEqual(1, Company.Count(), 'the runner seeds exactly one company');
    end;

    [Test]
    procedure ScdOtherCompanyNameIsNotFound()
    var
        Company: Record Company;
    begin
        // Negative control: Get must not answer true for any name.
        Assert.IsFalse(Company.Get('CRONUS International Ltd.'), 'a company the runner never seeded must not be found');
    end;
}
