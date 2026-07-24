// Reproduces the Pageworks TestPage cluster (14 tests, bare NullReferenceException).
//
// The runner registers a replacement for NavTestPageBase.ALGoToRecord via Hook(...),
// but Hook() routes into the JmpHook layer, which is DISABLED by default (Cecil-only).
// ALGoToRecord was never migrated to a Cecil IL rewrite, so the registration was a
// silent no-op: BC's own unpatched ALGoToRecord body ran against the runner's skeleton
// state and NREd, with no runner frame on the stack to point back at the missing patch.
//
// RED (before the fix): GoToRecord throws NullReferenceException.
// GREEN (after the fix): GoToRecord positions the page on the requested row.
codeunit 61811 "TGR Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "TGR Assert";

    local procedure SeedRows()
    var
        Row: Record "TGR Row";
    begin
        Row.DeleteAll();

        Row.Init();
        Row."No." := 'A';
        Row.Descr := 'Alpha';
        Row.Insert();

        Row.Init();
        Row."No." := 'B';
        Row.Descr := 'Bravo';
        Row.Insert();

        Row.Init();
        Row."No." := 'C';
        Row.Descr := 'Charlie';
        Row.Insert();
    end;

    // Positive: GoToRecord must land on the requested row, NOT merely "not throw".
    // Asserting Descr (a non-key field) proves the page's cursor actually moved to
    // that row rather than the assertion reading back the key we supplied.
    [Test]
    procedure GoToRecord_PositionsPageOnRequestedRow()
    var
        Row: Record "TGR Row";
        TgrList: TestPage "TGR List";
    begin
        SeedRows();

        Row.Get('B');

        TgrList.OpenView();
        Assert.IsTrue(TgrList.GoToRecord(Row), 'GoToRecord must find the seeded row B');
        Assert.AreEqual('B', TgrList."No.".Value, 'TestPage must be positioned on row B');
        Assert.AreEqual('Bravo', TgrList.Descr.Value, 'TestPage must expose row B''s non-key field');
        TgrList.Close();
    end;

    // Positive: navigating to a DIFFERENT row from an already-positioned page must
    // move the cursor. Guards against an implementation that only ever lands on the
    // first row (which would still satisfy a single-row test).
    [Test]
    procedure GoToRecord_MovesBetweenRows()
    var
        Row: Record "TGR Row";
        TgrList: TestPage "TGR List";
    begin
        SeedRows();

        TgrList.OpenView();

        Row.Get('C');
        Assert.IsTrue(TgrList.GoToRecord(Row), 'GoToRecord must find row C');
        Assert.AreEqual('Charlie', TgrList.Descr.Value, 'TestPage must be positioned on row C');

        Row.Get('A');
        Assert.IsTrue(TgrList.GoToRecord(Row), 'GoToRecord must find row A');
        Assert.AreEqual('Alpha', TgrList.Descr.Value, 'TestPage must have moved back to row A');

        TgrList.Close();
    end;

    // Negative: a record whose key is not present on the page must report "not found"
    // rather than silently succeeding or landing on an arbitrary row.
    [Test]
    procedure GoToRecord_ReturnsFalseForRowNotOnPage()
    var
        Row: Record "TGR Row";
        TgrList: TestPage "TGR List";
    begin
        SeedRows();

        // Build an in-memory record whose key was never inserted.
        Row.Init();
        Row."No." := 'ZZZ';
        Row.Descr := 'Not inserted';

        TgrList.OpenView();
        Assert.IsFalse(TgrList.GoToRecord(Row), 'GoToRecord must not claim to find a row that was never inserted');
        TgrList.Close();
    end;
}
