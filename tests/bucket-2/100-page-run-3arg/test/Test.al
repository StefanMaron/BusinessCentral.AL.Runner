/// Tests for Page.Run / Page.RunModal 3-argument overloads — issue #1374.
/// All 10 overloads are exercised. The position/focus argument is accepted and
/// ignored by the runner (no real UI). Tests are "no-throw" stubs per CLAUDE.md
/// exception: when the *entire* claim is "this does not crash", names make that explicit.
codeunit 309601 "P3A Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "P3A Source";

    // -----------------------------------------------------------------------
    // Page.Run 3-arg — no-throw stubs
    // -----------------------------------------------------------------------

    [Test]
    procedure PageRun_IntTableInt_NoThrow()
    var
        Rec: Record "P3A Row";
    begin
        // [WHEN] Page.Run(Integer, Table, Integer) is called
        // [THEN] No error is raised — position arg is accepted and ignored
        Src.RunIntTableInt(Rec);
        Assert.IsTrue(true, 'Page.Run(Integer, Table, Integer) must be a no-op');
    end;

    [Test]
    procedure PageRun_IntTableJoker_NoThrow()
    var
        Rec: Record "P3A Row";
    begin
        // [WHEN] Page.Run(Integer, Table, Joker) is called
        // [THEN] No error is raised
        Src.RunIntTableJoker(Rec);
        Assert.IsTrue(true, 'Page.Run(Integer, Table, Joker) must be a no-op');
    end;

    [Test]
    procedure PageRun_TextTableInt_NoThrow()
    var
        Rec: Record "P3A Row";
    begin
        // [WHEN] Page.Run(Text, Table, Integer) is called
        // [THEN] No error is raised
        Src.RunTextTableInt(Rec, 'P3A Card');
        Assert.IsTrue(true, 'Page.Run(Text, Table, Integer) must be a no-op');
    end;

    [Test]
    procedure PageRun_TextTableJoker_NoThrow()
    var
        Rec: Record "P3A Row";
    begin
        // [WHEN] Page.Run(Text, Table, Joker) is called
        // [THEN] No error is raised
        Src.RunTextTableJoker(Rec, 'P3A Card');
        Assert.IsTrue(true, 'Page.Run(Text, Table, Joker) must be a no-op');
    end;

    // -----------------------------------------------------------------------
    // Page.RunModal 3-arg — return Action::None (stub default)
    // -----------------------------------------------------------------------

    [Test]
    procedure PageRunModal_IntTableFieldRef_ReturnsNone()
    var
        Rec: Record "P3A Row";
        Result: Action;
    begin
        // [WHEN] Page.RunModal(Integer, Table, FieldRef) is called
        Result := Src.RunModalIntTableFieldRef(Rec);
        // [THEN] Returns Action::None (stub default = 0) — proves a value was returned
        Assert.AreEqual(Action::None, Result, 'Page.RunModal(Integer, Table, FieldRef) must return Action::None');
    end;

    [Test]
    procedure PageRunModal_IntTableInt_ReturnsNone()
    var
        Rec: Record "P3A Row";
        Result: Action;
    begin
        // [WHEN] Page.RunModal(Integer, Table, Integer) is called
        Result := Src.RunModalIntTableInt(Rec);
        // [THEN] Returns Action::None (stub default)
        Assert.AreEqual(Action::None, Result, 'Page.RunModal(Integer, Table, Integer) must return Action::None');
    end;

    [Test]
    procedure PageRunModal_IntTableJoker_ReturnsNone()
    var
        Rec: Record "P3A Row";
        Result: Action;
    begin
        // [WHEN] Page.RunModal(Integer, Table, Joker) is called
        Result := Src.RunModalIntTableJoker(Rec);
        // [THEN] Returns Action::None (stub default)
        Assert.AreEqual(Action::None, Result, 'Page.RunModal(Integer, Table, Joker) must return Action::None');
    end;

    [Test]
    procedure PageRunModal_TextTableFieldRef_ReturnsNone()
    var
        Rec: Record "P3A Row";
        Result: Action;
    begin
        // [WHEN] Page.RunModal(Text, Table, FieldRef) is called
        Result := Src.RunModalTextTableFieldRef(Rec, 'P3A Card');
        // [THEN] Returns Action::None (stub default)
        Assert.AreEqual(Action::None, Result, 'Page.RunModal(Text, Table, FieldRef) must return Action::None');
    end;

    [Test]
    procedure PageRunModal_TextTableInt_ReturnsNone()
    var
        Rec: Record "P3A Row";
        Result: Action;
    begin
        // [WHEN] Page.RunModal(Text, Table, Integer) is called
        Result := Src.RunModalTextTableInt(Rec, 'P3A Card');
        // [THEN] Returns Action::None (stub default)
        Assert.AreEqual(Action::None, Result, 'Page.RunModal(Text, Table, Integer) must return Action::None');
    end;

    [Test]
    procedure PageRunModal_TextTableJoker_ReturnsNone()
    var
        Rec: Record "P3A Row";
        Result: Action;
    begin
        // [WHEN] Page.RunModal(Text, Table, Joker) is called
        Result := Src.RunModalTextTableJoker(Rec, 'P3A Card');
        // [THEN] Returns Action::None (stub default)
        Assert.AreEqual(Action::None, Result, 'Page.RunModal(Text, Table, Joker) must return Action::None');
    end;
}
