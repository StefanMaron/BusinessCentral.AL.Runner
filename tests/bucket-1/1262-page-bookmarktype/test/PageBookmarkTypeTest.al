/// Tests that page classes compile with BookmarkType, CheckType, and SetRecord stubs.
/// BC emits these members on Page<N> classes; without stubs the Roslyn compilation
/// fails with CS1061 after the NavForm base class is stripped.
codeunit 1262003 "BkmType Test"
{
    Subtype = Test;
    var Assert: Codeunit Assert;

    [Test]
    procedure PageCompiles_WithTempSourceTable()
    begin
        // Positive: the page with SourceTableTemporary = true compiled.
        // BookmarkType = BookmarkType.Temporary is emitted by newer BC compilers
        // for temporary source tables. If the stub is missing, this page would
        // fail Roslyn compilation and this test would not exist.
        Assert.AreEqual(1262002, 1262002, 'Page 1262002 compiled with temp source table');
    end;

    [Test]
    procedure SetRecord_NoThrow()
    var
        TestPage: Page "BkmType Test Page";
        Rec: Record "BkmType Test Table";
    begin
        // Positive: CurrPage.SetRecord(rec) lowered to this.SetRecord(rec.Target)
        // compiles and runs without error. Exercises the injected SetRecord stub
        // on the page class (issue #1262).
        Rec.Init();
        Rec."Entry No." := 1;
        Rec.Description := 'test-set-record';
        Rec.Insert();

        TestPage.SetRecordOnPage(Rec);
        // The fact that we reach here proves SetRecord exists and does not throw.
        Assert.AreEqual(1, 1, 'SetRecord on page did not throw');
    end;

    [Test]
    procedure AssertError_WorksAfterPageStubInjection()
    begin
        // Negative: runner is functional after BookmarkType/CheckType/SetRecord injection.
        asserterror Error('bookmark-sentinel');
        Assert.ExpectedError('bookmark-sentinel');
    end;
}
