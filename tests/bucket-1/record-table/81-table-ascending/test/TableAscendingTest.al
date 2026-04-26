codeunit 81102 "TA Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "TA Helper";

    // -----------------------------------------------------------------------
    // Table.Ascending() — no-arg getter
    // -----------------------------------------------------------------------

    [Test]
    procedure Ascending_Default_IsTrue()
    begin
        // Positive: fresh record defaults to ascending order
        Assert.IsTrue(Helper.GetAscending(), 'Table.Ascending() must return true by default');
    end;

    // -----------------------------------------------------------------------
    // Table.Ascending(false) — setter, then getter
    // -----------------------------------------------------------------------

    [Test]
    procedure Ascending_SetFalse_GetsFalse()
    begin
        // Positive: Ascending(false) followed by Ascending() must return false
        Assert.IsFalse(Helper.SetDescendingThenGet(), 'Ascending(false) must set direction to descending');
    end;

    [Test]
    procedure Ascending_SetTrue_GetsTrue()
    begin
        // Positive: Ascending(true) followed by Ascending() must return true
        Assert.IsTrue(Helper.SetAscendingThenGet(), 'Ascending(true) must set direction to ascending');
    end;

    // -----------------------------------------------------------------------
    // Ascending(false) affects iteration order
    // -----------------------------------------------------------------------

    [Test]
    procedure Ascending_False_IteratesDescending()
    begin
        // Positive: FindSet with Ascending(false) iterates in reverse PK order (C B A)
        Assert.AreEqual('CBA', Helper.IterateDescending(),
            'Ascending(false) with FindSet must yield descending PK order');
    end;

    [Test]
    procedure Ascending_Default_IteratesAscending()
    begin
        // Positive: default FindSet iterates in PK ascending order (A B C)
        Assert.AreEqual('ABC', Helper.IterateAscending(),
            'Default Ascending() must yield ascending PK order');
    end;

    // -----------------------------------------------------------------------
    // Reset restores ascending
    // -----------------------------------------------------------------------

    [Test]
    procedure Ascending_ResetRestoresTrue()
    begin
        // Positive: Reset after Ascending(false) restores ascending = true
        Assert.IsTrue(Helper.ResetRestoresAscending(),
            'Reset must restore Ascending() to true after it was set to false');
    end;

    // -----------------------------------------------------------------------
    // Negative: getter returns opposite of what setter stored
    // -----------------------------------------------------------------------

    [Test]
    procedure Ascending_SetFalse_NotTrue()
    begin
        // Negative: Ascending(false) must NOT return true
        Assert.IsFalse(Helper.SetDescendingThenGet(),
            'Ascending(false) must not leave ascending direction as true');
    end;
}
