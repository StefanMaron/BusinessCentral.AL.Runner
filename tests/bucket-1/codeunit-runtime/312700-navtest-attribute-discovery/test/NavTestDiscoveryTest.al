/// <summary>
/// Suite 312700 (bucket-1/codeunit-runtime): [NavTest] attribute-only discovery.
///
/// Proves that the executor discovers ONLY methods tagged [Test] (i.e. bearing
/// [NavTestAttribute] in the emitted C#). Procedures named Test* on tables or
/// non-test codeunits must be silently ignored — not run as phantom tests.
///
/// RED before fix: the bucket run would show an additional "ERROR TestSomething"
/// entry (NRE from the table procedure) alongside PASS InsertViaOnInsert.
///
/// GREEN after fix: exactly one test is reported; no phantom TestSomething entry.
/// </summary>
codeunit 1312702 "NAD Discovery Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------
    // Positive: a record Insert that goes through OnInsert (which calls
    // the local TestSomething procedure) works correctly.
    // This test ALSO proves that TestSomething is not separately run:
    // if the executor ran TestSomething as a test, it would NRE before
    // reaching this line, failing the whole suite.
    // -----------------------------------------------------------------

    [Test]
    procedure InsertViaOnInsert_ReturnsCorrectNo()
    var
        Rec: Record "NAD Demo Table";
    begin
        // [GIVEN] A fresh record with a valid No.
        Rec.Init();
        Rec."No." := 'X001';
        Rec.Description := 'Test fixture';

        // [WHEN] Insert with trigger fires OnInsert → TestSomething()
        Rec.Insert(true);

        // [THEN] Record persisted with the correct No.
        Assert.AreEqual('X001', Rec."No.", 'Insert via OnInsert must retain No.');
    end;

    // -----------------------------------------------------------------
    // Negative: inserting with an empty No. triggers the guard in
    // TestSomething (called via OnInsert) and raises the expected error.
    // -----------------------------------------------------------------

    [Test]
    procedure InsertWithEmptyNo_RaisesError()
    var
        Rec: Record "NAD Demo Table";
    begin
        // [GIVEN] A record with an empty No.
        Rec.Init();
        Rec."No." := '';

        // [WHEN/THEN] Insert fires OnInsert → TestSomething() → Error(...)
        asserterror Rec.Insert(true);
        Assert.ExpectedError('No. must not be empty on insert.');
    end;

    // -----------------------------------------------------------------
    // Positive: the helper codeunit's Test* procedure works as a normal
    // helper — called by test code, not "discovered" by the executor.
    // -----------------------------------------------------------------

    [Test]
    procedure HelperTestComputeSum_ReturnsCorrectValue()
    var
        Helper: Codeunit "NAD Helper";
        Result: Integer;
    begin
        // [GIVEN] Two positive integers
        // [WHEN] Called through the helper codeunit
        Result := Helper.TestComputeSum(3, 4);

        // [THEN] Sum is 7 — a non-default value so a no-op stub cannot pass
        Assert.AreEqual(7, Result, 'TestComputeSum(3,4) must return 7');
    end;

    [Test]
    procedure HelperTestComputeSum_Negative_WrongExpectation()
    var
        Helper: Codeunit "NAD Helper";
        Result: Integer;
    begin
        // [GIVEN] Two integers whose sum is known (3+4=7)
        Result := Helper.TestComputeSum(3, 4);

        // [WHEN] We assert a wrong value
        // [THEN] Assert fails with the right message
        asserterror Assert.AreEqual(99, Result, 'Sum must not be 99');
        Assert.ExpectedError('Sum must not be 99');
    end;
}
