// Issue #4468 — the runner's one remaining refusal on the code-coverage surface, and its scope.
//
// CODECOVERAGELOG(TRUE) starts BC's own recorder (corpus codeunit 60339 pins what a service tier
// does there). The three code-coverage virtual tables are BC's own data providers, filled by
// CODECOVERAGELOG(FALSE). Their Code Coverage (2000000049) LINE rows are a projection of each
// covered object's AL source text, which BC reads from the application database; the runner has
// no store for it, so reading a line row refuses by name.
//
// The controls at the bottom are what keep that refusal honest: a refusal widened to the whole
// surface would pass the first two tests and take working recording away.
codeunit 65651 "Ccv Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Ccv Assert";

    local procedure Work(x: Integer): Integer
    begin
        if x > 1 then
            exit(x * 2);
        exit(x);
    end;

    local procedure RecordSomething()
    begin
        CodeCoverageLog(true, false);
        Work(3);
        CodeCoverageLog(false, false);
    end;

    [Test]
    procedure LineRows_AfterRecording_RefusedByName()
    var
        CodeCoverage: Record "Code Coverage";
    begin
        RecordSomething();

        asserterror CodeCoverage.FindFirst();

        Assert.ExpectedError('out-of-scope: Record "Code Coverage" (2000000049) line rows');
        Assert.ExpectedError('not-yet-implemented');
        Assert.ExpectedError('docs/limitations.md#code-coverage-virtual-tables');
        Assert.ErrorContainsExactlyOnce('see docs/limitations.md');
        // BC's own failure on this path names the wrong cause: that the covered object does
        // not exist in the application.
        Assert.NotExpectedError('does not exist');
        // Nor may the runner's empty store answer: that is "nothing was covered", a lie.
        Assert.NotExpectedError('There is no Code Coverage within the filter');
    end;

    [Test]
    procedure LineRows_RefusalTearsThroughTryFunction()
    begin
        RecordSomething();

        // not-yet-implemented is the anchor ApplicationObjectBasePatches.IsPermanentOutOfScope
        // lets through a [TryFunction], so the gap cannot be absorbed into a quiet `false`.
        asserterror if TryReadLineRow() then;

        Assert.ExpectedError('out-of-scope: Record "Code Coverage" (2000000049) line rows');
    end;

    // --- the controls: what must NOT refuse ---

    [Test]
    procedure Recording_StartQueryStop_DoesNotRefuse()
    begin
        Assert.IsTrue(CodeCoverageLog(true, false), 'CODECOVERAGELOG(TRUE, FALSE) returns TRUE');
        Assert.IsTrue(CodeCoverageLog(), 'the session is recording');
        Work(2);
        Assert.IsFalse(CodeCoverageLog(false, false), 'CODECOVERAGELOG(FALSE) returns FALSE');
        Assert.IsFalse(CodeCoverageLog(), 'the session stopped recording');
    end;

    [Test]
    procedure MultiSessionRecording_DoesNotRefuse()
    begin
        Assert.IsTrue(CodeCoverageLog(true, true), 'CODECOVERAGELOG(TRUE, TRUE) returns TRUE');
        Assert.IsTrue(CodeCoverageLog(), 'the session is recording');
        Work(2);
        CodeCoverageLog(false, true);
        Assert.IsFalse(CodeCoverageLog(), 'the session stopped recording');
    end;

    [Test]
    procedure EmptiedTable_ReadsEmpty_WithoutRefusing()
    var
        CodeCoverage: Record "Code Coverage";
    begin
        RecordSomething();

        // DeleteAll with no filters clears BC's provider in memory; nothing is left to project
        // from source, so the read neither refuses nor invents rows.
        CodeCoverage.DeleteAll();

        Assert.IsTrue(CodeCoverage.IsEmpty(), 'Code Coverage is empty after DeleteAll');
    end;

    [TryFunction]
    local procedure TryReadLineRow()
    var
        CodeCoverage: Record "Code Coverage";
    begin
        CodeCoverage.FindFirst();
    end;
}
