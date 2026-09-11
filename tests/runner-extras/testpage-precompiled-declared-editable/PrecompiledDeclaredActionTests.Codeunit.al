// PrecompiledDeclaredActionTests — an ACTION's declared Enabled/Visible on a page that ships
// PRECOMPILED in a dependency .app (issue #2460).
//
// WHY THIS FILE EXISTS
//   #3819 wired the action arm as a two-line mirror of the control arm it landed beside, and
//   said plainly in its own PR body that the action half had no end-to-end AL test: the
//   committed fixture's page declared no actions, so nothing drove RunnerPageInstance
//   .DeclaredActionProperty through a real TestPage. Everything that DID reference it asserted
//   against RecordPatches.TryGetDependencyActionDeclaredProperty — the string resolver one
//   layer below — which is the shape .claude/rules/tdd.md calls "a test that names the thing is
//   not a test that drives it". These four close that, through TestPage.
//
// WHAT #2460 ASKED FOR, AND WHY THE ANSWER IS NOT AN ACTION TREE
//   The issue proposed emitting <ActionContainers>/<Actions> into the synthesized metadata so
//   BC's own TryGetCommonActionDefinitionById could find a definition. Measured against BC's own
//   compiler output, that reconstruction buys no answer this path does not already have:
//
//     Base Application 28.1.49838.53910 — 25,308 actions over 2,610 pages
//       Enabled absent (AL declared none -> default true)   24,179
//       Enabled expression-bound                             1,116   <- #3825, refuses today
//       Enabled literal true                                    10
//       Enabled literal false                                    3   <- pages 36, 189, 190
//
//   Reproduced on 27.3 (a different Ncl.dll generation): 23,990 / 1,101 / 10 / 6.
//
//   Only the literal changes an answer, and the flat id-keyed symbol lookup #3819 landed
//   already resolves it without any tree — which is what these tests pin. A tree would compute
//   "true" for the 24,179 that already answer true.
//
// THE DECLARATION IS READ FROM THE SYMBOL FILE, NOT FROM A TREE
//   RunnerPageInstance.DeclaredActionProperty asks the runtime tree first and falls back to
//   RecordPatches.TryGetDependencyActionDeclaredProperty, which reads the declaring dependency's
//   own SymbolReference.json keyed by member id. The actions below sit inside a group() node for
//   that reason: a collector reading only the top level of "Actions" would find none of them,
//   and every one of these tests would go green against a resolver that had found nothing.
//
// WHY THIS IS A RUNNER TEST AND NOT A CORPUS TEST
//   Same reason as the control suite beside it: a corpus test compiles its page FROM SOURCE and
//   takes the source-parsed branch, so it never reaches the dependency-metadata synthesizer.
//   The condition under test cannot be constructed upstream. What real BC answers for "an action
//   declaring Enabled = false reports Enabled() = false" is not in doubt and is not what this
//   pins — corpus codeunit 60583 "TPAR Tests" already measures that on a service tier. What
//   this pins is that the RUNNER stops discarding the declaration on the precompiled path.
codeunit 65922 "TPDE Declared Action Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "TPDE Assert";

    local procedure SeedAndOpen(var DepRow: Record "TPCD Dep Table"; Id: Integer; var DepPage: TestPage "TPCD Dep Page")
    begin
        DepRow.Init();
        DepRow.ID := Id;
        DepRow.Message := 'Message ' + Format(Id);
        DepRow.Insert();

        DepPage.OpenView();
        DepPage.GotoRecord(DepRow);
    end;

    [Test]
    procedure DeclaredActionEnabledFalse_IsHonoured()
    var
        DepRow: Record "TPCD Dep Table";
        DepPage: TestPage "TPCD Dep Page";
    begin
        // [GIVEN] A row on the precompiled dependency page's source table.
        SeedAndOpen(DepRow, 1, DepPage);

        // [THEN] DisabledAction declares the literal Enabled = false in the dependency's symbol
        // file, and reports false. Before #3819 this reported true: ActionDefinition(id)
        // resolved through BC's TryGetCommonActionDefinitionById over an empty action tree,
        // answered null, and EvaluateProperty's "raw is null => the AL declared none" arm
        // returned the AL default. Mutating the fallback to `return null` turns exactly this
        // test red, which is what proves it drives the declaration and not the default.
        Assert.IsFalse(DepPage.DisabledAction.Enabled(),
          'DisabledAction declares Enabled = false in the dependency symbol file');
    end;

    [Test]
    procedure DeclaredActionVisibleFalse_IsHonoured()
    var
        DepRow: Record "TPCD Dep Table";
        DepPage: TestPage "TPCD Dep Page";
    begin
        // [GIVEN] A row on the precompiled dependency page's source table.
        SeedAndOpen(DepRow, 2, DepPage);

        // [THEN] The other of the two properties an action can declare, resolved independently:
        // HiddenAction declares only Visible, so an implementation answering one property for
        // the other would show up here rather than passing quietly.
        Assert.IsFalse(DepPage.HiddenAction.Visible(),
          'HiddenAction declares Visible = false in the dependency symbol file');

        // ...and it is still ENABLED, because it declares no Enabled. A hidden action is not a
        // disabled one, and collapsing the two would be a new wrong answer in the other
        // direction.
        Assert.IsTrue(DepPage.HiddenAction.Enabled(),
          'HiddenAction declares no Enabled, so AL default true');
    end;

    [Test]
    procedure ActionDeclaringNothing_StaysEnabledAndVisible()
    var
        DepRow: Record "TPCD Dep Table";
        DepPage: TestPage "TPCD Dep Page";
    begin
        // [GIVEN] A row on the precompiled dependency page's source table.
        SeedAndOpen(DepRow, 3, DepPage);

        // [THEN] The negative that stops the fix becoming the opposite wrong answer. This is
        // the 24,179-of-25,308 population in Base Application 28.1 — an action really is
        // enabled and visible unless its AL says otherwise, and a resolver that answered false
        // for "declares none" would disable almost every action in the product.
        Assert.IsTrue(DepPage.PlainAction.Enabled(),
          'PlainAction declares no Enabled, so AL default true');
        Assert.IsTrue(DepPage.PlainAction.Visible(),
          'PlainAction declares no Visible, so AL default true');
    end;

    [Test]
    procedure ExpressionBoundActionEnabled_RefusesLoudlyRatherThanGuessing()
    var
        DepRow: Record "TPCD Dep Table";
        DepPage: TestPage "TPCD Dep Page";
        Ignored: Boolean;
    begin
        // [GIVEN] A row on the precompiled dependency page's source table.
        SeedAndOpen(DepRow, 4, DepPage);

        // [WHEN] Reading an Enabled bound to a page global — #2460's own shape, name for name:
        // Base Application 977 "Time Sheet Setup Wizard" declares Enabled = BackActionEnabled on
        // its Back action, and all six failures the issue reports are on exactly that kind.
        //
        // [THEN] The runner REFUSES, naming the expression. This is the boundary between what
        // #3819 fixed and what #3825 still owns: the binding key the live page registers is
        // p977p977BackActionEnabled, and the symbol file states only BackActionEnabled — the
        // Name/SourceExpression pair that joins them exists solely in metadata the AL compiler
        // writes, which a shipped .app does not carry.
        //
        // Asserting the REFUSAL rather than a value is the point. Answering true here would be
        // this issue's exact reported symptom (a silent wrong answer that makes
        // Assert.IsTrue(action.Enabled()) pass vacuously) reproduced on a smaller population.
        asserterror Ignored := DepPage.WizardAction.Enabled();
        Assert.ExpectedError('bound to expression ''BackActionEnabled''');
    end;
}
