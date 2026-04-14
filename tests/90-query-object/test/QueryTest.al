codeunit 59001 "Query Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Logic: Codeunit "Query Logic";

    // ------------------------------------------------------------------
    // Positive: codeunit with Query variables compiles and business
    // logic that does not call Open/Read runs correctly.
    // ------------------------------------------------------------------

    [Test]
    procedure QueryDeclarationCompiles()
    begin
        // [GIVEN] A codeunit that declares a Query variable
        // [WHEN]  We call a procedure that doesn't invoke Open/Read
        // [THEN]  It returns the expected value — proves compilation succeeded
        Assert.AreEqual('query-ready', Logic.GetStatus(), 'GetStatus should return query-ready');
    end;

    [Test]
    procedure QueryCloseIsNoOp()
    begin
        // [GIVEN] A codeunit that declares a Query variable
        // [WHEN]  We call Close() on an unopened query
        // [THEN]  No error is raised — Close is a no-op
        Logic.TryClose();
    end;

    // ------------------------------------------------------------------
    // Negative: calling Open/Read must throw a NotSupportedException
    // with 'Query' in the message so the developer gets a clear hint.
    // ------------------------------------------------------------------

    [Test]
    procedure QueryOpenThrowsNotSupported()
    begin
        // [GIVEN] A declared Query variable
        // [WHEN]  We call Q.Open()
        // [THEN]  A clear 'Query' error is raised
        asserterror Logic.TryOpen();
        Assert.ExpectedError('Query');
    end;

    [Test]
    procedure QueryReadThrowsNotSupported()
    begin
        // [GIVEN] A declared Query variable
        // [WHEN]  We call Q.Read()
        // [THEN]  A clear 'Query' error is raised
        asserterror Logic.TryRead();
        Assert.ExpectedError('Query');
    end;

    [Test]
    procedure QuerySetFilterAndOpenThrowsNotSupported()
    begin
        // [GIVEN] A declared Query variable with a filter set
        // [WHEN]  We call SetFilter then Open
        // [THEN]  The SetFilter succeeds (no-op) but Open throws
        asserterror Logic.TrySetFilterAndOpen();
        Assert.ExpectedError('Query');
    end;

    [Test]
    procedure QueryTopAndOpenThrowsNotSupported()
    begin
        // [GIVEN] A declared Query variable with TopNumberOfRows set
        // [WHEN]  We call TopNumberOfRows then Open
        // [THEN]  The TopNumberOfRows succeeds (no-op) but Open throws
        asserterror Logic.TrySetTop();
        Assert.ExpectedError('Query');
    end;
}
