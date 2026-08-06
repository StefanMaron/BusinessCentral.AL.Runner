codeunit 62052 "ITTC Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "ITTC Assert";

    // [POSITIVE] The install trigger ran to completion (if it had crashed, this whole
    // bundle would be a suite error with 0 tests — see the bundle summary the runner
    // prints on a crash) AND the tag it set is readable with its exact value, not a
    // default/empty result.
    [Test]
    procedure InstallTagIsSet()
    var
        UpgradeTag: Codeunit "Upgrade Tag";
    begin
        Assert.IsTrue(
            UpgradeTag.HasUpgradeTag('ITTC-TEXTCONST-1.0'),
            'the install trigger''s SetUpgradeTag call must have persisted the exact tag it set');
    end;

    // [NEGATIVE] A tag that was never set must read back false — proves HasUpgradeTag
    // is not hardcoded true and that install-baseline data doesn't leak unrelated codes.
    [Test]
    procedure UnsetTagIsNotPresent()
    var
        UpgradeTag: Codeunit "Upgrade Tag";
    begin
        Assert.IsFalse(
            UpgradeTag.HasUpgradeTag('ITTC-NEVER-SET-TAG'),
            'a tag that was never set must not read back as present');
    end;
}
