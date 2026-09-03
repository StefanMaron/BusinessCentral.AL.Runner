// Issue #2581. The ten columns of "Windows Language" (2000000045) that the runner cannot
// answer from a real source, and the values it answers instead.
//
// WHY THIS IS A RUNNER TEST AND NOT A CORPUS TEST
//   Six columns are license-derived: BC fills them from
//   Database.SecurityAndLicense.License.HasLanguagePermission. The runner has no license, and
//   BC has no no-license ANSWER to copy — get_License() throws rather than returning anything.
//   So the runner is not reproducing BC behaviour here, it is choosing a value, and a chosen
//   value is a runner claim. That is what makes it belong here rather than upstream.
//
//   Four more report whether translation resources are installed. The runner installs no BC
//   translation resources, so it reports none.
//
// WHY IT IS A TEST AT ALL
//   A chosen value that nothing asserts is indistinguishable from a silent default. This suite
//   is what makes the choice DECLARED: change either seam
//   (RecordPatches.WindowsLanguageVirtualTable.StubbedLicensePermission /
//   StubbedLocalizationResources) and this goes red instead of the behaviour drifting quietly.
//   Both are provisional pending a mockable license, at which point the license half of this
//   suite is what will tell you the answer moved.
codeunit 64547 "WLLS Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit "WLLS Assert";

    [Test]
    procedure LicenseColumns_AnswerPermitted()
    var
        W: Record "Windows Language";
    begin
        // Permissive by choice: the runner exists so AL tests run WITHOUT a license, and
        // answering "not permitted" would gate the business logic those tests exist to
        // exercise. See docs/limitations.md.
        Assert.IsTrue(W.Get(1033), 'Get(1033) must find English (United States).');
        Assert.IsTrue(W.Enabled, '"Enabled" must answer permitted.');
        Assert.IsTrue(W."Globally Enabled", '"Globally Enabled" must answer permitted.');
        Assert.IsTrue(W."Form Enabled", '"Form Enabled" must answer permitted.');
        Assert.IsTrue(W."Report Enabled", '"Report Enabled" must answer permitted.');
        Assert.IsTrue(W."Dataport Enabled", '"Dataport Enabled" must answer permitted.');
        Assert.IsTrue(W."XMLport Enabled", '"XMLport Enabled" must answer permitted.');
    end;

    [Test]
    procedure LicenseColumns_AnswerTheSameForEveryLanguage()
    var
        EnUs: Record "Windows Language";
        DeDe: Record "Windows Language";
    begin
        // The stub is language-independent, which is itself part of the declared behaviour: a
        // future license mock is free to vary by language, and this is the assertion that will
        // notice when it does.
        Assert.IsTrue(EnUs.Get(1033), 'Get(1033) must succeed.');
        Assert.IsTrue(DeDe.Get(1031), 'Get(1031) must succeed.');
        Assert.AreEqual(EnUs.Enabled, DeDe.Enabled, 'The license stub must not vary by language.');
        Assert.AreEqual(EnUs."Globally Enabled", DeDe."Globally Enabled",
            'The license stub must not vary by language.');
    end;

    [Test]
    procedure InstalledResourceColumns_AnswerNone()
    var
        W: Record "Windows Language";
    begin
        // Different reason from the license columns: the runner genuinely installs no BC
        // translation resources, so reporting none is a statement about this process. It still
        // diverges from a service tier that has localizations installed, so it is pinned here
        // too, behind its own seam.
        Assert.IsTrue(W.Get(1033), 'Get(1033) must find English (United States).');
        Assert.IsFalse(W."STX File Exist", '"STX File Exist" must answer none.');
        Assert.IsFalse(W."ETX File Exist", '"ETX File Exist" must answer none.');
        Assert.IsFalse(W."Help File Exist", '"Help File Exist" must answer none.');
        Assert.IsFalse(W."Localization Exist", '"Localization Exist" must answer none.');
    end;

    [Test]
    procedure TruthfulColumnsStillComeFromBc_NotFromTheStub()
    var
        W: Record "Windows Language";
    begin
        // The control that keeps this suite honest: the stub must not have leaked into the
        // columns that DO have a source. If it had, every column would read the same and the
        // three tests above would still pass.
        Assert.IsTrue(W.Get(1033), 'Get(1033) must succeed.');
        Assert.AreEqual('English (United States)', W.Name, 'Name must come from BC, not the stub.');
        Assert.AreEqual('en-US', W."Language Tag", 'Language Tag must come from BC, not the stub.');
    end;
}
