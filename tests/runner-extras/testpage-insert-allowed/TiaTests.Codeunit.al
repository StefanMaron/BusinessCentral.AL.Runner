// Reproduces the second layer of the Pageworks TestPage cluster (8 tests).
//
// The runner's ITestPage implementation hardcoded `Creatable => false`, so BC's
// NavTestPageBase.New() ALWAYS threw NavInsertDeniedPermissionException
// ("New method failed because Insert is not allowed. Page = , Id = 0"), regardless
// of the page's actual InsertAllowed property. That is a silent fake: every
// insert-through-TestPage test fails for a reason that has nothing to do with the
// page under test.
//
// RED (before the fix): New() on an ordinary page throws.
// GREEN (after the fix): Creatable reflects the page's parsed InsertAllowed
// (defaulting to true when the property is absent, as AL does).
codeunit 61822 "TIA Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "TIA Assert";

    local procedure ClearRows()
    var
        Row: Record "TIA Row";
    begin
        Row.DeleteAll();
    end;

    // Positive: New() must actually persist a row, not merely "not throw".
    // Asserting the stored field values proves the insert reached the table.
    [Test]
    procedure New_OnInsertablePage_InsertsRow()
    var
        Row: Record "TIA Row";
        Insertable: TestPage "TIA Insertable";
    begin
        ClearRows();

        Insertable.OpenEdit();
        Insertable.New();
        Insertable."No.".SetValue('N1');
        Insertable.Descr.SetValue('Inserted via TestPage');
        Insertable.Close();

        Assert.AreEqual(1, Row.Count(), 'TestPage.New() must insert exactly one row');
        Row.Get('N1');
        Assert.AreEqualText('Inserted via TestPage', Row.Descr,
            'The value typed into the TestPage must reach the backing table');
    end;

    // Positive: two successive New() calls must yield two distinct rows — guards
    // against an implementation where New() silently reuses one buffer.
    [Test]
    procedure New_Twice_InsertsTwoDistinctRows()
    var
        Row: Record "TIA Row";
        Insertable: TestPage "TIA Insertable";
    begin
        ClearRows();

        Insertable.OpenEdit();
        Insertable.New();
        Insertable."No.".SetValue('N1');
        Insertable.New();
        Insertable."No.".SetValue('N2');
        Insertable.Close();

        Assert.AreEqual(2, Row.Count(), 'Two New() calls must insert two rows');
    end;

    // Negative: a page that genuinely declares InsertAllowed = false must STILL
    // refuse. This pins the fix to "honour the declared property" rather than
    // "always allow" — the same silent fake in the opposite direction.
    [Test]
    procedure New_OnInsertAllowedFalsePage_IsDenied()
    var
        Row: Record "TIA Row";
        ReadOnlyPage: TestPage "TIA ReadOnly";
    begin
        ClearRows();

        ReadOnlyPage.OpenEdit();
        asserterror ReadOnlyPage.New();
        Assert.AreEqualText('New method failed because Insert is not allowed.',
            CopyStr(GetLastErrorText(), 1, StrLen('New method failed because Insert is not allowed.')),
            'A page declaring InsertAllowed = false must still refuse TestPage.New()');

        Assert.AreEqual(0, Row.Count(), 'A denied New() must not insert a row');
    end;
}
