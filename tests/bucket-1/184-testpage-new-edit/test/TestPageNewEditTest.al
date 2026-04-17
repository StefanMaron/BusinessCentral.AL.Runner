codeunit 119001 "TPNE Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── TestPage.New() ────────────────────────────────────────────────────────

    [Test]
    procedure New_AfterOpenView_SetsEditable()
    var
        P: TestPage "TPNE Card Page";
    begin
        P.OpenView();
        Assert.IsFalse(P.Editable(), 'OpenView must start non-editable');
        P.New();
        Assert.IsTrue(P.Editable(), 'New() must switch page to editable');
        P.Close();
    end;

    [Test]
    procedure New_AfterOpenEdit_RemainsEditable()
    var
        P: TestPage "TPNE Card Page";
    begin
        P.OpenEdit();
        Assert.IsTrue(P.Editable(), 'OpenEdit must start editable');
        P.New(); // Already editable — must not crash and must remain editable
        Assert.IsTrue(P.Editable(), 'New() on already-editable page must remain editable');
        P.Close();
    end;

    [Test]
    procedure New_ProducesEditableState_NotViewState()
    var
        P: TestPage "TPNE Card Page";
        Q: TestPage "TPNE Card Page";
    begin
        P.OpenView();
        Q.OpenView();
        P.New();
        // P became editable via New(), Q stayed non-editable — must differ
        Assert.AreNotEqual(P.Editable(), Q.Editable(),
            'New() must change editable state vs an OpenView page that had no New() call');
        P.Close();
        Q.Close();
    end;

    // ── TestPage.Edit() ───────────────────────────────────────────────────────

    [Test]
    procedure Edit_AfterOpenView_SetsEditable()
    var
        P: TestPage "TPNE Card Page";
    begin
        P.OpenView();
        Assert.IsFalse(P.Editable(), 'OpenView must start non-editable');
        P.Edit().Invoke();
        Assert.IsTrue(P.Editable(), 'Edit() must switch page to editable');
        P.Close();
    end;

    [Test]
    procedure Edit_AfterOpenEdit_RemainsEditable()
    var
        P: TestPage "TPNE Card Page";
    begin
        P.OpenEdit();
        Assert.IsTrue(P.Editable(), 'OpenEdit must start editable');
        P.Edit().Invoke(); // Idempotent — must not crash
        Assert.IsTrue(P.Editable(), 'Edit() on already-editable page must remain editable');
        P.Close();
    end;

    [Test]
    procedure Edit_ProducesEditableState_NotViewState()
    var
        P: TestPage "TPNE Card Page";
        Q: TestPage "TPNE Card Page";
    begin
        P.OpenView();
        Q.OpenView();
        P.Edit().Invoke();
        // P became editable via Edit(), Q stayed non-editable — must differ
        Assert.AreNotEqual(P.Editable(), Q.Editable(),
            'Edit() must change editable state vs an OpenView page that had no Edit() call');
        P.Close();
        Q.Close();
    end;
}
