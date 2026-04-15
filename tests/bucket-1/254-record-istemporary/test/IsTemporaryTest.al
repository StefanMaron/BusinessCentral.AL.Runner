codeunit 50254 "IsTemporary Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure NormalRecord_IsTemporary_False()
    var
        Widget: Record "IsTemp Widget";
    begin
        // [GIVEN] A normal record variable (persisted to the in-memory table)
        // [THEN] IsTemporary returns false
        Assert.IsFalse(Widget.IsTemporary(), 'Normal Record variable must report IsTemporary = false');
    end;

    [Test]
    procedure TempRecord_IsTemporary_True()
    var
        TempWidget: Record "IsTemp Widget" temporary;
    begin
        // [GIVEN] A temporary record variable
        // [THEN] IsTemporary returns true
        Assert.IsTrue(TempWidget.IsTemporary(), 'Temporary Record variable must report IsTemporary = true');
    end;

    [Test]
    procedure TempRecord_StaysTemporary_AfterInsert()
    var
        TempWidget: Record "IsTemp Widget" temporary;
    begin
        TempWidget.Init();
        TempWidget."No." := 'T1';
        TempWidget.Name := 'Alpha';
        TempWidget.Insert();

        // IsTemporary remains true after writes
        Assert.IsTrue(TempWidget.IsTemporary(), 'IsTemporary must remain true after Insert on a temp record');
    end;

    [Test]
    procedure TempRecord_IsIsolated_FromNormalRecord()
    var
        TempWidget: Record "IsTemp Widget" temporary;
        Widget: Record "IsTemp Widget";
    begin
        // [GIVEN] Temporary table gets data
        TempWidget.Init();
        TempWidget."No." := 'T1';
        TempWidget.Name := 'Alpha';
        TempWidget.Insert();
        Assert.AreEqual(1, TempWidget.Count(), 'Sanity: temp row inserted');

        // [THEN] The persisted table is untouched — proves temp/persisted are separate stores
        Assert.AreEqual(0, Widget.Count(), 'Temp inserts must not leak into the persisted table');
        Assert.IsTrue(TempWidget.IsTemporary(), 'TempWidget stays temporary');
        Assert.IsFalse(Widget.IsTemporary(), 'Widget stays persisted');
    end;

    [Test]
    procedure NormalRecord_AfterInsert_IsTemporary_False()
    var
        Widget: Record "IsTemp Widget";
    begin
        Widget.Init();
        Widget."No." := 'P1';
        Widget.Name := 'Persisted';
        Widget.Insert();

        Assert.IsFalse(Widget.IsTemporary(), 'IsTemporary must stay false after Insert on a persisted record');
    end;
}
