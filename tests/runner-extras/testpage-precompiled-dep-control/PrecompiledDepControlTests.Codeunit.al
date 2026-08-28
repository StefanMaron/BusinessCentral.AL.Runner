// PrecompiledDepControlTests — proves a TestPage field control read resolves on a page
// that ships PRECOMPILED in a dependency .app (issue #2088), identically to a page compiled
// from AL source in this same bundle.
//
// THE BUG
//   RecordPatches.GetPageControlFieldMap answered from _parsedPages only -- pages the
//   runner AL-source-PARSED itself. A page shipping precompiled in a dependency .app (this
//   suite's "TPCD Dep Page", or Base Application's page 700 "Error Messages" in the field)
//   is never in _parsedPages, so the control map came back empty and EVERY field control
//   read on such a page threw RunnerOutOfScopeException("testpage-control-binding"),
//   regardless of whether the control name matched its bound field.
//
// THE FIX
//   GetPageControlFieldMap now falls back to the same dependency SymbolReference.json
//   already read for the "Page Control Field" virtual table (#1779) -- reusing
//   RecordPatches.ResolveDependencyControlField, unchanged, so both consumers stay in sync.
//
// NEGATIVE DIRECTION LIVES IN AlRunner.Tests, NOT HERE
//   AL cannot exercise "a control on this page whose declared field the runner cannot
//   resolve" as an end-to-end TestPage read: BC's own AL compiler only emits a GetField(id)
//   dispatch for a field control whose SourceExpression it can already validate as a plain
//   Rec.Field against the SAME symbol data the runner reads -- a plain Rec.Field control
//   that fails to resolve at the compiler's OWN level could never have compiled into a real
//   dependency .app in the first place (real BC would have rejected it at publish time). A
//   control bound to anything else (an unresolvable name, a compound expression) compiles
//   to different generated code that never reaches LiveNavTestPage.GetField at all, so it
//   cannot exercise this fix's throw path either. RecordPatchesGetPageControlFieldMapDependencyTests
//   (AlRunner.Tests) pins the negative side directly against the fixed method instead: a
//   symbol-declared control whose SourceExpression names a field absent from the resolved
//   table must NOT appear in the returned map, proving the fallback does not fabricate a
//   binding no real compiled dependency could ever have shipped.
codeunit 65201 "TPCD Precompiled Ctrl Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "TPCD Assert";

    [Test]
    procedure ControlNameDiffersFromFieldName_Resolves()
    var
        DepRow: Record "TPCD Dep Table";
        DepPage: TestPage "TPCD Dep Page";
    begin
        // [GIVEN] A row on the precompiled dependency page's source table.
        DepRow.Init();
        DepRow.ID := 1;
        DepRow.Message := 'Repro message';
        DepRow."Additional Information" := 'Repro info';
        DepRow.Insert();

        // [WHEN] The row is read through the TestPage's "Description" control, which is
        // bound to Rec.Message -- name deliberately differs from the field it binds to
        // (the exact shape Base Application page 700's Description -> Rec."Message" takes).
        DepPage.OpenView();
        DepPage.GotoRecord(DepRow);

        // [THEN] The control resolves to the real field value, not an out-of-scope refusal.
        Assert.AreEqual('Repro message', DepPage.Description.Value(),
            'Description control (bound to Rec.Message) should read the row''s Message field');
    end;

    [Test]
    procedure ControlNameMatchesFieldName_Resolves()
    var
        DepRow: Record "TPCD Dep Table";
        DepPage: TestPage "TPCD Dep Page";
    begin
        // [GIVEN] A row on the precompiled dependency page's source table.
        DepRow.Init();
        DepRow.ID := 2;
        DepRow.Message := 'Repro message 2';
        DepRow."Additional Information" := 'Repro info 2';
        DepRow.Insert();

        // [WHEN] The row is read through the TestPage's "Additional Information" control,
        // whose name matches the field it binds to -- proves the fix does not depend on a
        // name mismatch to exercise the resolution path.
        DepPage.OpenView();
        DepPage.GotoRecord(DepRow);

        // [THEN] The control resolves to the real field value.
        Assert.AreEqual('Repro info 2', DepPage."Additional Information".Value(),
            '"Additional Information" control should read the row''s own field');
    end;
}
