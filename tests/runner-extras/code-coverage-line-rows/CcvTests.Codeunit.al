namespace AlRunner.Extras.CodeCoverageLineRows;

using System.Tooling;
using System.Utilities;

// Issue #4572 — Code Coverage (2000000049) line rows, and where the runner still refuses.
//
// BC builds the line rows from each covered object's AL source text, which it reads from the
// application database (table 2000000207 "User AL Code"). The runner serves that text from the
// .al files it compiled, so an object compiled from source in this run gets BC's own rows. An
// object it did NOT compile from source — a precompiled dependency — has no text here, and
// reading its rows refuses by name.
//
// The namespace and using lines above are deliberate: BC numbers an object's lines with the
// file's preamble in front, and the line numbers asserted below are the FILE's line numbers,
// which only come out right if that preamble is counted. What real BC answers for line rows in
// general is corpus codeunit 60341 "Test Code Coverage Table".
codeunit 65651 "Ccv Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Ccv Assert";

    local procedure Work(x: Integer): Integer
    begin
        if x > 1 then
            exit(x * 2);   // file line 28: runs for x = 3
        exit(x);           // file line 29: does not run for x = 3
    end;

    local procedure RecordSomething()
    var
        CodeCoverage: Record "Code Coverage";
    begin
        CodeCoverage.DeleteAll();
        CodeCoverageLog(true, false);
        Work(3);
        CodeCoverageLog(false, false);
    end;

    [Test]
    procedure LineRows_BundleObject_ServedAtItsFileLines()
    var
        CodeCoverage: Record "Code Coverage";
    begin
        RecordSomething();

        Assert.IsTrue(CodeCoverage.Get(CodeCoverage."Object Type"::Codeunit, Codeunit::"Ccv Tests", 28),
            'a line row for the executed line exit(x * 2) at file line 28');
        Assert.AreEqual(Format(CodeCoverage."Line Type"::Code), Format(CodeCoverage."Line Type"), 'line 28 is a Code line');
        Assert.AreEqual(1, CodeCoverage."No. of Hits", 'line 28 ran once');
        Assert.AreEqual('            exit(x * 2);   // file line 28: runs for x = 3', CodeCoverage.Line,
            'line 28 carries that line''s own source text');

        Assert.IsTrue(CodeCoverage.Get(CodeCoverage."Object Type"::Codeunit, Codeunit::"Ccv Tests", 29),
            'a line row for the line exit(x) at file line 29');
        Assert.AreEqual(Format(CodeCoverage."Line Type"::Code), Format(CodeCoverage."Line Type"), 'line 29 is a Code line');
        Assert.AreEqual(0, CodeCoverage."No. of Hits", 'line 29 did not run');

        Assert.IsTrue(CodeCoverage.Get(CodeCoverage."Object Type"::Codeunit, Codeunit::"Ccv Tests", 25),
            'a line row for the procedure Work at file line 25');
        Assert.AreEqual(Format(CodeCoverage."Line Type"::"Trigger/Function"), Format(CodeCoverage."Line Type"),
            'line 25 is the Trigger/Function line');
    end;

    [Test]
    procedure LineRows_BundleObject_ServedWhileADependencyWasCoveredToo()
    var
        CodeCoverage: Record "Code Coverage";
        Math: Codeunit Math;
    begin
        CodeCoverage.DeleteAll();
        CodeCoverageLog(true, false);
        Work(3);
        Assert.AreEqual(2, Math.Abs(-2), 'the dependency call ran');
        CodeCoverageLog(false, false);

        // The refusal is per covered object: this codeunit's rows do not depend on the
        // dependency's.
        CodeCoverage.SetRange("Object Type", CodeCoverage."Object Type"::Codeunit);
        CodeCoverage.SetRange("Object ID", Codeunit::"Ccv Tests");
        CodeCoverage.SetRange("Line No.", 28);
        Assert.IsTrue(CodeCoverage.FindFirst(), 'this codeunit''s line 28 is served');
        Assert.AreEqual(1, CodeCoverage."No. of Hits", 'line 28 ran once');
    end;

    [Test]
    procedure LineRows_PrecompiledDependency_RefusedByName()
    var
        CodeCoverage: Record "Code Coverage";
    begin
        RecordDependencyCall();

        CodeCoverage.SetRange("Object Type", CodeCoverage."Object Type"::Codeunit);
        CodeCoverage.SetRange("Object ID", Codeunit::Math);
        asserterror CodeCoverage.FindFirst();

        Assert.ExpectedError('out-of-scope: ALCodeEnvironment.GetSourceCodeLines');
        Assert.ExpectedError('not-yet-implemented');
        Assert.ExpectedError('710 was not compiled from AL source in this run');
        Assert.ExpectedError('docs/limitations.md#code-coverage-virtual-tables');
        Assert.ErrorContainsExactlyOnce('see docs/limitations.md');
        // BC's own failure on this path names the wrong cause: that the covered object does
        // not exist in the application.
        Assert.NotExpectedError('does not exist');
        // Nor may an empty store answer: that is "nothing was covered", a lie.
        Assert.NotExpectedError('There is no Code Coverage within the filter');
    end;

    [Test]
    procedure LineRows_PrecompiledDependency_RefusalTearsThroughTryFunction()
    begin
        RecordDependencyCall();

        // not-yet-implemented is the anchor ApplicationObjectBasePatches.IsPermanentOutOfScope
        // lets through a [TryFunction], so the gap cannot be absorbed into a quiet `false`.
        asserterror if TryReadDependencyRow() then;

        Assert.ExpectedError('out-of-scope: ALCodeEnvironment.GetSourceCodeLines');
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
        RecordDependencyCall();

        // DeleteAll with no filters clears BC's provider in memory; nothing is left to project
        // from source, so the read neither refuses nor invents rows.
        CodeCoverage.DeleteAll();

        Assert.IsTrue(CodeCoverage.IsEmpty(), 'Code Coverage is empty after DeleteAll');
    end;

    local procedure RecordDependencyCall()
    var
        CodeCoverage: Record "Code Coverage";
        Math: Codeunit Math;
    begin
        CodeCoverage.DeleteAll();
        CodeCoverageLog(true, false);
        Assert.AreEqual(2, Math.Abs(-2), 'the dependency call ran');
        CodeCoverageLog(false, false);
    end;

    [TryFunction]
    local procedure TryReadDependencyRow()
    var
        CodeCoverage: Record "Code Coverage";
    begin
        CodeCoverage.SetRange("Object Type", CodeCoverage."Object Type"::Codeunit);
        CodeCoverage.SetRange("Object ID", Codeunit::Math);
        CodeCoverage.FindFirst();
    end;
}
