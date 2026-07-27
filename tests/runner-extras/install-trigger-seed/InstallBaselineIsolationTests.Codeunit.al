// Pins the per-codeunit install baseline: install triggers now run ONCE and their result
// is snapshotted, with each codeunit boundary restoring the snapshot instead of re-running
// AL (see AlRunner/Patches/RecordPatches.InstallBaseline.cs — 2.07x on Pageworks).
//
// The claim spans two codeunits: one mutates the install-seeded table, the other must not
// see it. ORDER-INDEPENDENT ON PURPOSE — the runner does not execute test codeunits in
// object-id order (observed: 60716 ran before 60715), so a mutate-then-assert pair would
// pass without proving anything whenever it happened to run backwards. Instead BOTH
// codeunits assert the pristine baseline on entry and THEN mutate it with their own
// distinct marker. Whichever runs second is a genuine proof that the boundary restored the
// baseline; whichever runs first is merely true. The pair therefore proves the restore in
// either order, and neither test can pass vacuously.
codeunit 60715 "ITS Baseline Mutation"
{
    Subtype = Test;

    var
        Assert: Codeunit "ITS Assert";

    [Test]
    procedure BaselineIsPristineOnEntryThenMutatedLocally()
    var
        Seed: Record "Install Seed";
    begin
        // [GIVEN] Whatever ran before this codeunit — including the sibling below, which
        // inserts its own marker — must not be visible here.
        Assert.IsFalse(Seed.Get('LEAK-A'), 'this codeunit''s own marker must not survive into a later run of it');
        Assert.IsFalse(Seed.Get('LEAK-B'), 'the sibling codeunit''s mutation must not cross the isolation boundary');
        Assert.AreEqual(3, Seed.Count(), 'exactly the three install-seeded rows must be present on entry');

        // [WHEN] This codeunit mutates the install-seeded table.
        Seed."Code" := 'LEAK-A';
        Seed."Value" := 123;
        Seed.Insert();

        // [THEN] The mutation is visible to the rest of THIS codeunit — proving the
        // baseline is restored at the boundary, not frozen against writes altogether.
        Assert.AreEqual(4, Seed.Count(), 'the mutation must be visible inside its own test codeunit');
        Assert.IsTrue(Seed.Get('LEAK-A'), 'the inserted row must be readable inside its own test codeunit');
    end;
}

codeunit 60716 "ITS Baseline Restored"
{
    Subtype = Test;

    var
        Assert: Codeunit "ITS Assert";

    [Test]
    procedure BaselineIsPristineOnEntryThenMutatedLocally()
    var
        Seed: Record "Install Seed";
    begin
        Assert.IsFalse(Seed.Get('LEAK-A'), 'the sibling codeunit''s mutation must not cross the isolation boundary');
        Assert.IsFalse(Seed.Get('LEAK-B'), 'this codeunit''s own marker must not survive into a later run of it');
        Assert.AreEqual(3, Seed.Count(), 'exactly the three install-seeded rows must be present on entry');

        Seed."Code" := 'LEAK-B';
        Seed."Value" := 456;
        Seed.Insert();

        Assert.AreEqual(4, Seed.Count(), 'the mutation must be visible inside its own test codeunit');
        Assert.IsTrue(Seed.Get('LEAK-B'), 'the inserted row must be readable inside its own test codeunit');
    end;
}
