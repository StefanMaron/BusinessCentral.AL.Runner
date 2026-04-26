/// Tests proving that Report := Report assignment compiles and runs correctly.
/// Covers issue #1328: MockReportHandle was missing ALAssign(),
/// causing CS1061 at Roslyn compile time.
codeunit 307202 "RAS Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RAS Src";

    [Test]
    procedure ReportAssign_DoesNotThrow()
    begin
        // [GIVEN] Two report variables
        // [WHEN]  Rep1 := Rep2 is executed (compiler emits Rep1.ALAssign(Rep2))
        // [THEN]  No error is raised and the procedure returns true
        Assert.IsTrue(Src.AssignAndRun(), 'Report := Report assignment must not throw');
    end;

    [Test]
    procedure ReportAssignTwice_DoesNotThrow()
    begin
        // [GIVEN] Three report variables
        // [WHEN]  Rep1 is assigned twice from different sources
        // [THEN]  No error is raised
        Assert.IsTrue(Src.AssignTwice(), 'Double Report := Report assignment must not throw');
    end;

    [Test]
    procedure ReportAssignInline_DoesNotThrow()
    var
        Rep1: Report "RAS Report";
        Rep2: Report "RAS Report";
    begin
        // [GIVEN] Two report variables declared inline in the test
        // [WHEN]  Assignment is done directly in test body
        Rep1 := Rep2;
        // [THEN]  No error
        Assert.IsTrue(true, 'Inline Report := Report must not throw');
    end;
}
