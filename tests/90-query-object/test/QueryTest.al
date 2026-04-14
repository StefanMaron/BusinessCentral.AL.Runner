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

    [Test]
    procedure QuerySetRangeIsNoOp()
    begin
        // [GIVEN] A declared Query variable
        // [WHEN]  We call SetRange with from/to bounds on a column
        // [THEN]  No error is raised — SetRange is a no-op
        Logic.TrySetRange();
    end;

    [Test]
    procedure QuerySetSingleRangeIsNoOp()
    begin
        // [GIVEN] A declared Query variable
        // [WHEN]  We call SetRange with a single value on a column
        // [THEN]  No error is raised — SetRange is a no-op
        Logic.TrySetSingleRange();
    end;

    [Test]
    procedure QueryClearRangeIsNoOp()
    begin
        // [GIVEN] A declared Query variable
        // [WHEN]  We call SetRange with no value (clear range) on a column
        // [THEN]  No error is raised — SetRange is a no-op
        Logic.TryClearRange();
    end;

    [Test]
    procedure QueryMultipleFiltersIsNoOp()
    begin
        // [GIVEN] A declared Query variable
        // [WHEN]  We call SetFilter, SetRange, TopNumberOfRows, and Close
        // [THEN]  All succeed without error — all are no-ops except Close
        Logic.TryMultipleFilters();
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

    [Test]
    procedure QuerySaveAsCsvThrowsNotSupported()
    begin
        // [GIVEN] A declared Query variable
        // [WHEN]  We call Q.SaveAsCsv()
        // [THEN]  A clear 'Query' error is raised
        asserterror Logic.TrySaveAsCsv();
        Assert.ExpectedError('Query');
    end;

    [Test]
    procedure QuerySaveAsXmlThrowsNotSupported()
    begin
        // [GIVEN] A declared Query variable
        // [WHEN]  We call Q.SaveAsXml()
        // [THEN]  A clear 'Query' error is raised
        asserterror Logic.TrySaveAsXml();
        Assert.ExpectedError('Query');
    end;
}
