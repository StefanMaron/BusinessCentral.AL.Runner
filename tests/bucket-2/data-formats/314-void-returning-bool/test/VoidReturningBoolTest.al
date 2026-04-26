/// Tests that methods used in `if not Method(...)` guards return Boolean — issue #1432.
/// Three concrete methods were reported: IsolatedStorage.Set, XmlElement.AddBeforeSelf,
/// and ReportInstance.SaveAs.  All previously returned void in the runner, causing CS0023.
codeunit 1314002 "VRB Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "VRB Src";

    // ── IsolatedStorage.Set ──────────────────────────────────────────────────

    [Test]
    procedure IsoSet_ReturnsTrue_OnSuccess()
    begin
        // Positive: IsolatedStorage.Set must return true (success).
        Assert.IsTrue(Src.IsoSet_ReturnsTrue('vrb-key1', 'value1'),
            'IsolatedStorage.Set must return true');
    end;

    [Test]
    procedure IsoSetWithScope_ReturnsTrue_OnSuccess()
    begin
        // Positive: IsolatedStorage.Set(key, value, DataScope) must return true.
        Assert.IsTrue(Src.IsoSetWithScope_ReturnsTrue('vrb-key2', 'value2', DataScope::Module),
            'IsolatedStorage.Set(key, value, scope) must return true');
    end;

    [Test]
    procedure IsoSet_IfNotGuard_BranchNotTaken()
    begin
        // Positive: the `if not IsolatedStorage.Set(...)` branch must NOT be taken
        // (i.e. Set returns true → not-true is false → guard body is skipped).
        Assert.IsTrue(Src.IsoSet_IfNotGuard('vrb-key3', 'value3'),
            'if-not guard must not fire when IsolatedStorage.Set succeeds');
    end;

    // ── XmlElement.AddBeforeSelf ────────────────────────────────────────────

    [Test]
    procedure XmlAddBeforeSelf_IfNotGuard_BranchNotTaken()
    begin
        // Positive: AddBeforeSelf must return true so the guard body is skipped.
        Assert.IsTrue(Src.XmlAddBeforeSelf_IfNotGuard('child', 'sibling'),
            'if-not guard must not fire when XmlElement.AddBeforeSelf succeeds');
    end;

    [Test]
    procedure XmlAddAfterSelf_IfNotGuard_BranchNotTaken()
    begin
        // Positive: AddAfterSelf must return true so the guard body is skipped.
        Assert.IsTrue(Src.XmlAddAfterSelf_IfNotGuard('child', 'sibling'),
            'if-not guard must not fire when XmlElement.AddAfterSelf succeeds');
    end;

    [Test]
    procedure XmlRemove_IfNotGuard_BranchNotTaken()
    begin
        // Positive: XmlElement.Remove must return true so the guard body is skipped.
        Assert.IsTrue(Src.XmlRemove_IfNotGuard('child'),
            'if-not guard must not fire when XmlElement.Remove succeeds');
    end;

    // ── ReportInstance.SaveAs / SaveAsPdf ───────────────────────────────────

    [Test]
    procedure ReportSaveAsPdf_IfNotGuard_BranchNotTaken()
    begin
        // Positive: ReportInstance.SaveAsPdf must return true so the guard body is skipped.
        Assert.IsTrue(Src.ReportSaveAsPdf_IfNotGuard(''),
            'if-not guard must not fire when ReportInstance.SaveAsPdf succeeds');
    end;

    [Test]
    procedure ReportSaveAs_IfNotGuard_BranchNotTaken()
    var
        BlobRec: Record "VRB Blob Store" temporary;
        OutStr: OutStream;
    begin
        // Positive: ReportInstance.SaveAs must return true so the guard body is skipped.
        BlobRec.Data.CreateOutStream(OutStr);
        Assert.IsTrue(Src.ReportSaveAs_IfNotGuard('', ReportFormat::Pdf, OutStr),
            'if-not guard must not fire when ReportInstance.SaveAs succeeds');
    end;
}
