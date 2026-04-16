codeunit 85001 "NAS Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "NAS Src";

    // -----------------------------------------------------------------------
    // Positive: NavApp.IsInstalling() must return false in standalone mode —
    // there is no installation lifecycle without a service tier.
    // -----------------------------------------------------------------------

    [Test]
    procedure IsInstalling_ReturnsFalse()
    begin
        // Positive: no installation is in progress in standalone mode.
        Assert.IsFalse(Src.GetIsInstalling(),
            'NavApp.IsInstalling() must return false in standalone mode');
    end;

    // -----------------------------------------------------------------------
    // Positive: NavApp.IsUnlicensed() must return false in standalone mode —
    // no license enforcement is applied without a service tier.
    // -----------------------------------------------------------------------

    [Test]
    procedure IsUnlicensed_ReturnsFalse()
    begin
        // Positive: standalone mode is never considered unlicensed.
        Assert.IsFalse(Src.GetIsUnlicensed(),
            'NavApp.IsUnlicensed() must return false in standalone mode');
    end;

    // -----------------------------------------------------------------------
    // Positive: NavApp.IsEntitled() must return true in standalone mode —
    // the runner grants full entitlement so tests can reach all code paths.
    // -----------------------------------------------------------------------

    [Test]
    procedure IsEntitled_ReturnsTrue()
    begin
        // Positive: standalone mode is always considered entitled for any entitlement ID.
        Assert.IsTrue(Src.GetIsEntitled('STANDARD'),
            'NavApp.IsEntitled(id) must return true in standalone mode');
    end;

    [Test]
    procedure IsEntitled_EmptyId_ReturnsTrue()
    begin
        // Positive: empty entitlement ID is also accepted — standalone grants all.
        Assert.IsTrue(Src.GetIsEntitled(''),
            'NavApp.IsEntitled with empty id must return true in standalone mode');
    end;

    // -----------------------------------------------------------------------
    // Negative: the three values are mutually consistent — entitled=true
    // contradicts unlicensed=true and installing=true simultaneously.
    // -----------------------------------------------------------------------

    [Test]
    procedure StatusFlags_AreConsistent()
    var
        IsInstalling: Boolean;
        IsUnlicensed: Boolean;
        IsEntitled: Boolean;
    begin
        // Negative: if entitled, must not also be unlicensed or installing.
        IsInstalling := Src.GetIsInstalling();
        IsUnlicensed := Src.GetIsUnlicensed();
        IsEntitled := Src.GetIsEntitled('ANY');

        Assert.IsTrue(IsEntitled, 'Must be entitled');
        Assert.IsFalse(IsInstalling, 'Must not be installing when entitled');
        Assert.IsFalse(IsUnlicensed, 'Must not be unlicensed when entitled');
    end;
}
