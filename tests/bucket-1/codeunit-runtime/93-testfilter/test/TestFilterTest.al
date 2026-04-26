/// Tests for TestFilter methods on TestPage.Filter:
/// Ascending (get/set), CurrentKey (get), SetCurrentKey,
/// SetFilter (store by field), GetFilter (retrieve).
///
/// Proof strategy: if MockTestPageFilter is missing any of these methods,
/// Roslyn compilation fails with CS1061 and ALL tests in this bucket go RED.
codeunit 96100 "TPF TestFilter Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── SetFilter / GetFilter ──────────────────────────────────────────────────

    [Test]
    procedure SetFilter_GetFilter_RoundTrip()
    var
        TP: TestPage "TPF Test Page";
    begin
        // Positive: a filter set via SetFilter is returned by GetFilter.
        TP.OpenView();
        TP.Filter.SetFilter(Name, 'Alice*');
        Assert.AreEqual('Alice*', TP.Filter.GetFilter(Name),
            'GetFilter must return the value set by SetFilter');
        TP.Close();
    end;

    [Test]
    procedure GetFilter_ReturnsEmpty_WhenNotSet()
    var
        TP: TestPage "TPF Test Page";
    begin
        // Negative: GetFilter returns empty string when no filter was set.
        TP.OpenView();
        Assert.AreEqual('', TP.Filter.GetFilter(Name),
            'GetFilter must return empty string when no filter is set');
        TP.Close();
    end;

    [Test]
    procedure SetFilter_NotDefaultEmpty()
    var
        TP: TestPage "TPF Test Page";
    begin
        // Negative: a no-op SetFilter that always stores '' would fail this.
        TP.OpenView();
        TP.Filter.SetFilter(Name, 'Bob');
        Assert.AreNotEqual('', TP.Filter.GetFilter(Name),
            'SetFilter must persist a non-empty expression');
        TP.Close();
    end;

    // ── Ascending ─────────────────────────────────────────────────────────────

    [Test]
    procedure Ascending_DefaultIsTrue()
    var
        TP: TestPage "TPF Test Page";
    begin
        // Positive: default sort direction is ascending.
        TP.OpenView();
        Assert.IsTrue(TP.Filter.Ascending(),
            'Ascending() must default to true');
        TP.Close();
    end;

    [Test]
    procedure Ascending_Set_GetRoundTrip()
    var
        TP: TestPage "TPF Test Page";
    begin
        // Positive: Ascending(false) followed by Ascending() returns false.
        TP.OpenView();
        TP.Filter.Ascending(false);
        Assert.IsFalse(TP.Filter.Ascending(),
            'Ascending() must reflect the value set via Ascending(false)');
        TP.Close();
    end;

    [Test]
    procedure Ascending_NotStuckAtDefault()
    var
        TP: TestPage "TPF Test Page";
    begin
        // Negative: a no-op Ascending setter that always returns true would fail.
        TP.OpenView();
        TP.Filter.Ascending(false);
        Assert.AreNotEqual(true, TP.Filter.Ascending(),
            'Ascending setter must actually change the value');
        TP.Close();
    end;

    // ── CurrentKey / SetCurrentKey ─────────────────────────────────────────────

    [Test]
    procedure SetCurrentKey_CurrentKey_RoundTrip()
    var
        TP: TestPage "TPF Test Page";
    begin
        // Positive: SetCurrentKey followed by CurrentKey returns non-empty text.
        TP.OpenView();
        TP.Filter.SetCurrentKey(Name);
        Assert.AreNotEqual('', TP.Filter.CurrentKey(),
            'CurrentKey must be non-empty after SetCurrentKey');
        TP.Close();
    end;

    [Test]
    procedure CurrentKey_AfterSetCurrentKey_NotDefault()
    var
        TP: TestPage "TPF Test Page";
        k: Text;
    begin
        // Negative: a no-op SetCurrentKey that always returns '' would fail this.
        TP.OpenView();
        TP.Filter.SetCurrentKey(Name);
        k := TP.Filter.CurrentKey();
        Assert.AreNotEqual('', k,
            'CurrentKey must return a non-empty value after SetCurrentKey');
        TP.Close();
    end;

    [Test]
    procedure AllFilterMethods_Compile()
    begin
        // Proof: reaching this line means all TestFilter stubs compiled.
        Assert.IsTrue(true,
            'All TestFilter stub methods must compile without CS1061');
    end;
}
