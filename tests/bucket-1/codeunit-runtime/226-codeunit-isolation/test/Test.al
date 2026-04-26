/// Tests for test isolation flag functionality.
///
/// These tests verify that the --test-isolation flag works correctly.
/// CI runs with --test-isolation method (each test method gets a fresh table).
/// Users default to --test-isolation codeunit (BC default).
///
/// The tests here are written to work under --test-isolation method:
/// each test sets up its own state and verifies it correctly.

codeunit 226002 "CI Isolation Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ---------------------------------------------------------------
    // Positive: table starts empty at the beginning of each method
    // (under both method and codeunit isolation).
    // ---------------------------------------------------------------
    [Test]
    procedure TableIsEmptyAtStart_NoInsertHappened()
    var
        Rec: Record "CI Shared Table";
    begin
        // [GIVEN] No inserts have been done in this test
        // [THEN] Table count is 0 — proves isolation reset happened
        Assert.AreEqual(0, Rec.Count(), 'Table must be empty at start of each test method');
        Assert.IsFalse(Rec.FindFirst(), 'FindFirst on empty table must return false');
    end;

    // ---------------------------------------------------------------
    // Positive: insert and read back in same test method.
    // ---------------------------------------------------------------
    [Test]
    procedure InsertAndReadBack_SameMethod()
    var
        Rec: Record "CI Shared Table";
    begin
        // [GIVEN] Table is empty
        Assert.AreEqual(0, Rec.Count(), 'Pre-condition: table must be empty');

        // [WHEN] A record is inserted
        Rec.Id := 1;
        Rec.Value := 42;
        Rec.Insert();

        // [THEN] The record is readable in the same test method
        Assert.IsTrue(Rec.Get(1), 'Inserted record must be findable in same test');
        Assert.AreEqual(42, Rec.Value, 'Value must be 42');
        Assert.AreEqual(1, Rec.Count(), 'Count must be 1 after insert');
    end;

    // ---------------------------------------------------------------
    // Positive: second test method starts with empty table.
    // This runs after InsertAndReadBack_SameMethod in alphabetical order.
    // Under --test-isolation method, the table is reset between methods,
    // so this test sees an empty table even though the previous test inserted.
    // ---------------------------------------------------------------
    [Test]
    procedure TableIsEmptyAtStart_AfterPreviousInsert()
    var
        Rec: Record "CI Shared Table";
    begin
        // [GIVEN] Previous test (InsertAndReadBack_SameMethod) inserted a record
        // [WHEN] Running under --test-isolation method
        // [THEN] Table is empty — the per-method reset fired
        Assert.AreEqual(0, Rec.Count(), 'Table must be empty at start — per-method reset must have fired');
        Assert.IsFalse(Rec.Get(1), 'Record from previous method must not exist after method reset');
    end;
}
