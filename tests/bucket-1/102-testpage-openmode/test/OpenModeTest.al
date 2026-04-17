codeunit 117001 "OPM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure OpenEdit_IsEditable()
    var
        P: TestPage "OPM Card Page";
    begin
        P.OpenEdit();
        Assert.IsTrue(P.Editable(), 'OpenEdit must set page editable = true');
        P.Close();
    end;

    [Test]
    procedure OpenView_IsNotEditable()
    var
        P: TestPage "OPM Card Page";
    begin
        P.OpenView();
        Assert.IsFalse(P.Editable(), 'OpenView must set page editable = false');
        P.Close();
    end;

    [Test]
    procedure OpenNew_IsEditable()
    var
        P: TestPage "OPM Card Page";
    begin
        P.OpenNew();
        Assert.IsTrue(P.Editable(), 'OpenNew must set page editable = true');
        P.Close();
    end;

    [Test]
    procedure OpenView_NotANoop_EditDiffers()
    begin
        // Negative trap: OpenView and OpenEdit must produce different Editable states.
        // If Editable() always returned true (no-op) this would fail.
        Assert.AreNotEqual(
            IsViewEditable(),
            true,
            'OpenView must produce editable=false, not the same as OpenEdit');
    end;

    local procedure IsViewEditable(): Boolean
    var
        P: TestPage "OPM Card Page";
    begin
        P.OpenView();
        exit(P.Editable());
    end;
}
