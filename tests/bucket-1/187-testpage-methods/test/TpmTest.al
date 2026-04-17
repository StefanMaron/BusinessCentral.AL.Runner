/// Tests for TestPage gap methods — issue #678.
/// Covers: Close, Caption, Expand/IsExpanded, First, Last, Previous,
///         ValidationErrorCount, FindFirstField, View, No, Yes.
codeunit 123001 "TPM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── Close ─────────────────────────────────────────────────────────────────

    [Test]
    procedure Close_DoesNotThrow()
    var
        P: TestPage "TPM Card Page";
    begin
        P.OpenView();
        P.Close();
        // No assertion needed — absence of error is the result
        Assert.IsTrue(true, 'Close must not throw');
    end;

    // ── Caption ───────────────────────────────────────────────────────────────

    [Test]
    procedure Caption_ReturnsNonEmpty()
    var
        P: TestPage "TPM Card Page";
        Cap: Text;
    begin
        P.OpenView();
        Cap := P.Caption;
        Assert.AreNotEqual('', Cap, 'Caption must not be empty');
        P.Close();
    end;

    // ── ValidationErrorCount ──────────────────────────────────────────────────

    [Test]
    procedure ValidationErrorCount_Default_IsZero()
    var
        P: TestPage "TPM Card Page";
    begin
        P.OpenView();
        Assert.AreEqual(0, P.ValidationErrorCount(), 'ValidationErrorCount must default to 0');
        P.Close();
    end;

    [Test]
    procedure ValidationErrorCount_IsNotPositive()
    var
        P: TestPage "TPM Card Page";
    begin
        P.OpenView();
        Assert.IsFalse(P.ValidationErrorCount() > 0,
            'ValidationErrorCount must not be positive on a fresh page');
        P.Close();
    end;

    // ── First / Last / Previous ───────────────────────────────────────────────

    [Test]
    procedure First_ReturnsTrue()
    var
        P: TestPage "TPM Card Page";
    begin
        P.OpenView();
        Assert.IsTrue(P.First(), 'First() must return true');
        P.Close();
    end;

    [Test]
    procedure Last_ReturnsFalse()
    var
        P: TestPage "TPM Card Page";
    begin
        P.OpenView();
        Assert.IsFalse(P.Last(), 'Last() must return false');
        P.Close();
    end;

    [Test]
    procedure Previous_ReturnsFalse()
    var
        P: TestPage "TPM Card Page";
    begin
        P.OpenView();
        Assert.IsFalse(P.Previous(), 'Previous() must return false');
        P.Close();
    end;

    // ── Expand / IsExpanded ───────────────────────────────────────────────────

    [Test]
    procedure Expand_DoesNotThrow()
    var
        P: TestPage "TPM Card Page";
    begin
        P.OpenView();
        P.Expand(true);
        Assert.IsTrue(true, 'Expand must not throw');
        P.Close();
    end;

    [Test]
    procedure IsExpanded_ReturnsFalse()
    var
        P: TestPage "TPM Card Page";
    begin
        P.OpenView();
        Assert.IsFalse(P.IsExpanded(), 'IsExpanded must return false');
        P.Close();
    end;

    // ── View ──────────────────────────────────────────────────────────────────

    [Test]
    procedure View_Default_IsEmpty()
    var
        P: TestPage "TPM Card Page";
    begin
        P.OpenView();
        Assert.AreEqual('', P.View, 'View must default to empty string');
        P.Close();
    end;

    // ── No / Yes ─────────────────────────────────────────────────────────────

    [Test]
    procedure No_ReturnsAction_Invokable()
    var
        P: TestPage "TPM Card Page";
    begin
        P.OpenView();
        P.No().Invoke();
        Assert.IsTrue(true, 'No().Invoke() must not throw');
        P.Close();
    end;

    [Test]
    procedure Yes_ReturnsAction_Invokable()
    var
        P: TestPage "TPM Card Page";
    begin
        P.OpenView();
        P.Yes().Invoke();
        Assert.IsTrue(true, 'Yes().Invoke() must not throw');
        P.Close();
    end;
}
