// PrecompiledDeclaredEditableTests — a field control's DECLARED Editable/Enabled must be
// honoured on a page that ships PRECOMPILED in a dependency .app (issue #3504).
//
// THE BUG
//   DependencyPageMetadataXml reconstructs no control tree for such a page — correct for a
//   control's VALUE BINDING, which lives in the .app's IL and no XML here could rebuild — so
//   RunnerPageInstance.ControlDefinition(id) answered null for EVERY control on EVERY
//   precompiled page. EvaluateProperty's first arm reads null as "this element publishes no
//   such property at all", whose answer is the AL default of true. Two different conditions —
//   "declares nothing" and "there is no definition to ask" — had one answer.
//
//   Measured on Base Application 28.1.49838.53910 (2,610 pages / 37,185 field controls):
//   Editable is declared on 5,920 controls, Visible on 11,505, Enabled on 797. All 18,222
//   answered true. Page 46 "Sales Order Subform" — the issue's own surface — declares Editable
//   on 19 of its 102 field controls, nine of them the compile-time literal "false".
//
// THE FIX
//   The declared string IS stated per control by the dependency's own SymbolReference.json,
//   the same slice GetPageControlFieldMap and the "Page Control Field" virtual table already
//   read. RunnerPageInstance.DeclaredControlProperty falls back to it — and only when the
//   runtime tree has no definition, so a page the runner compiled itself is untouched.
//
// WHY THIS IS A RUNNER TEST AND NOT A CORPUS TEST
//   The claim is about the runner's precompiled-dependency metadata path. A corpus test
//   compiles its page FROM SOURCE, so it takes the source-parsed branch and never reaches the
//   synthesizer at all — the condition under test cannot be constructed upstream. Real BC's
//   answer for "a control declaring Editable = false reports Editable() = false" is not in
//   doubt and is not what this pins; what it pins is that the runner stops discarding the
//   declaration.
//
// THE EXPRESSION ARM IS NOT HERE
//   The issue's own control declares `Editable = InvDiscAmountEditable` — a PAGE VARIABLE.
//   Driving that end-to-end needs a precompiled dependency whose page both declares a variable
//   AND registers it through its own compiled RegisterSourceExpression IL, which the committed
//   fixture (reused from testpage-precompiled-dep-control, whose page has no variables) does
//   not. That arm is pinned directly against the resolver in
//   AlRunner.Tests/DependencyControlDeclaredPropertyTests.cs, where the symbol shape is
//   modelled attribute-for-attribute on page 46's real declaration. See README.md.
codeunit 65921 "TPDE Declared Editable Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "TPDE Assert";

    local procedure Seed(var DepRow: Record "TPCD Dep Table"; Id: Integer)
    begin
        DepRow.Init();
        DepRow.ID := Id;
        DepRow.Message := 'Message ' + Format(Id);
        DepRow."Additional Information" := 'Info ' + Format(Id);
        DepRow.Insert();
    end;

    [Test]
    procedure DeclaredEditableFalse_IsHonoured()
    var
        DepRow: Record "TPCD Dep Table";
        DepPage: TestPage "TPCD Dep Page";
    begin
        // [GIVEN] A row on the precompiled dependency page's source table.
        Seed(DepRow, 1);

        // [WHEN] The page is opened and positioned on it.
        DepPage.OpenView();
        DepPage.GotoRecord(DepRow);

        // [THEN] The "Description" control, whose symbol file declares Editable = false,
        // reports false. This is the whole RED: before the fix it reported true, because the
        // synthesized metadata carries no definition for the control to declare anything on.
        Assert.IsFalse(DepPage.Description.Editable(),
          'Description declares Editable = false in the dependency symbol file');
    end;

    [Test]
    procedure DeclaredEnabledFalse_IsHonoured()
    var
        DepRow: Record "TPCD Dep Table";
        DepPage: TestPage "TPCD Dep Page";
    begin
        // Enabled travels the same seam as Editable and had the same wrong answer, so it is
        // asserted independently rather than assumed to follow — the fix reads the property
        // NAME rather than hardcoding one of the three.
        Seed(DepRow, 2);

        DepPage.OpenView();
        DepPage.GotoRecord(DepRow);

        Assert.IsFalse(DepPage.Description.Enabled(),
          'Description declares Enabled = false in the dependency symbol file');
    end;

    [Test]
    procedure ControlDeclaringNothing_StaysEditable()
    var
        DepRow: Record "TPCD Dep Table";
        DepPage: TestPage "TPCD Dep Page";
    begin
        // The load-bearing NEGATIVE, and the reason the fix cannot simply flip the default.
        // "Additional Information" declares no Editable at all, and AL's default for an
        // undeclared Editable is TRUE. A fix that answered false for a missing definition
        // would turn one wrong answer into the opposite wrong answer and this test is what
        // stops it: null must keep meaning "declares none", never "declares false".
        Seed(DepRow, 3);

        DepPage.OpenView();
        DepPage.GotoRecord(DepRow);

        Assert.IsTrue(DepPage."Additional Information".Editable(),
          '"Additional Information" declares no Editable, so AL''s default of true stands');
        Assert.IsTrue(DepPage."Additional Information".Enabled(),
          '"Additional Information" declares no Enabled, so AL''s default of true stands');
    end;

    [Test]
    procedure ADeclaredNonEditableControl_StillReadsItsValue()
    var
        DepRow: Record "TPCD Dep Table";
        DepPage: TestPage "TPCD Dep Page";
    begin
        // A control being non-editable must not make it unreachable. Editable() answers a
        // property; only the compile-time LITERAL Visible = false eliminates a control from
        // the runtime page (RunnerPageInstance.ControlIsCompileTimeEliminated), and this fix
        // deliberately does not extend elimination to precompiled pages — that would change
        // reachability, not an answer. Without this assertion the suite could pass with the
        // control resolving to nothing at all.
        Seed(DepRow, 4);

        DepPage.OpenView();
        DepPage.GotoRecord(DepRow);

        Assert.AreEqual('Message 4', DepPage.Description.Value(),
          'a control declaring Editable = false is still readable');
    end;
}
