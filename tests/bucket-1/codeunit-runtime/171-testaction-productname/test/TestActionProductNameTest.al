codeunit 98001 "TAPN Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "TAPN Src";

    // ── ProductName ──────────────────────────────────────────────

    [Test]
    procedure ProductName_Full_ReturnsNonEmpty()
    begin
        Assert.AreNotEqual('', Src.ProductNameFull(),
            'ProductName.Full() must return a non-empty string');
    end;

    [Test]
    procedure ProductName_Marketing_DoesNotThrow()
    var
        result: Text;
    begin
        result := Src.ProductNameMarketing();
        Assert.IsTrue(true, 'ProductName.Marketing() must not throw');
    end;

    [Test]
    procedure ProductName_Short_DoesNotThrow()
    var
        result: Text;
    begin
        result := Src.ProductNameShort();
        Assert.IsTrue(true, 'ProductName.Short() must not throw');
    end;

    // ── TestAction — Enabled / Visible / Invoke ──────────────────

    [Test]
    procedure TestAction_Enabled_ReturnsTrue()
    var
        Page: TestPage "TAPN Card";
    begin
        Page.OpenView();
        Assert.IsTrue(Page.DoSomething.Enabled(),
            'TestAction.Enabled() must return true');
        Page.Close();
    end;

    [Test]
    procedure TestAction_Visible_ReturnsTrue()
    var
        Page: TestPage "TAPN Card";
    begin
        Page.OpenView();
        Assert.IsTrue(Page.DoSomething.Visible(),
            'TestAction.Visible() must return true');
        Page.Close();
    end;

    [Test]
    procedure TestAction_Invoke_DoesNotThrow()
    var
        Page: TestPage "TAPN Card";
    begin
        Page.OpenView();
        Page.DoSomething.Invoke();
        Assert.IsTrue(true, 'TestAction.Invoke() must not throw');
        Page.Close();
    end;
}
