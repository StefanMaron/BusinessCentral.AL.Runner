// PmnTests - proves TestPage trigger dispatch reaches members of a PRECOMPILED Base
// Application page whose AL name needed mangling (issues #2723 and #2517), and that the
// fix leaves the RunObject refusal exactly as loud as it was.
//
// Every page below ships precompiled in the Base Application, so none of them is ever in
// RecordPatches._parsedPages, and before the fix every mangled-name member on them fell to
// FindTriggerOnTarget's lossy backward scan. Each positive arm asserts an effect only the
// trigger's own AL body can produce - a Label the trigger raises, a row the trigger inserts,
// a Message the trigger shows - never merely "did not throw".
//
// The pages were chosen for triggers whose outcome is deterministic with NO test data:
//   - 1262 "Certificate List", action "Change User"          (space)      Error(AssignUserScopeErr) when Scope = Company
//   - 5098 "Task Card", action "A&ttendee Scheduling"        ('&')        Error(CannotSelectAttendeesErr) when Type <> Meeting
//   - 790  "G/L Account Categories", action New              (keyword)    SetRow(Rec.InsertRow()) - one more category row
//   - 790  "G/L Account Categories", actionref New_Promoted  (ref->keyword) same effect through the promoted reference
//   - 9875 "Permission Set Assignments", control "Company Name" (space, OnValidate) Error(EmptyUserNameErr) on a new row
//   - 710  "Activity Log" + precompiled pageextension 711 "Activity Log Extension", action OpenRelatedRecord
//                                                            (precompiled pageextension) Message(NoRelatedRecordMsg) on a blank Record ID
//   - 5098 "Task Card", action "Co&mment"                    (RunObject only) target AND its
//                                                            three-entry RunPageLink resolved from
//                                                            the symbol file, then applied
//   - 31   "Item List", action "Item Substitutions"          (RunObject naming an AMBIGUOUS name)
codeunit 64571 "PMN Precompiled Member Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "PMN Assert";
        CapturedMessage: Text;
        CapturedComments: Text;

    [Test]
    procedure SpacedActionName_OnPrecompiledBasePage_RunsItsOnAction()
    var
        IsolatedCertificate: Record "Isolated Certificate";
        CertificateList: TestPage "Certificate List";
    begin
        // [GIVEN] A certificate whose Scope is Company - the branch of "Change User"'s
        // OnAction that raises AssignUserScopeErr. Insert(false): the table's OnInsert
        // reaches into Isolated Storage, which is beside the point here.
        IsolatedCertificate.Init();
        IsolatedCertificate.Code := 'PMN-COMPANY';
        IsolatedCertificate.Scope := IsolatedCertificate.Scope::Company;
        IsolatedCertificate.Insert(false);

        CertificateList.OpenEdit();
        CertificateList.GotoRecord(IsolatedCertificate);

        // [WHEN] The action whose AL name carries a space is invoked. BC emits its trigger
        // as Change_User_a45_OnAction; the runner used to un-mangle that to Change_User,
        // hash it, and never meet the id BC asks for.
        asserterror CertificateList."Change User".Invoke();

        // [THEN] The trigger's OWN error - not the runner's no-effect refusal.
        Assert.ExpectedError('This certificate is available to everyone in the company');
    end;

    [Test]
    procedure AmpersandActionName_OnPrecompiledBasePage_RunsItsOnAction()
    var
        TaskCard: TestPage "Task Card";
    begin
        // [GIVEN] The Task Card on a blank task (InsertAllowed = false, no rows): Type is
        // not Meeting, which is the branch that raises CannotSelectAttendeesErr.
        TaskCard.OpenEdit();

        // [WHEN] An action whose name carries '&' - emitted as Aa38ttendee_Scheduling_a45_OnAction.
        asserterror TaskCard."A&ttendee Scheduling".Invoke();

        // [THEN] The trigger's own error text.
        Assert.ExpectedError('You cannot select attendees for a task of the');
    end;

    [Test]
    procedure KeywordActionName_OnPrecompiledBasePage_RunsItsOnAction()
    var
        GLAccountCategory: Record "G/L Account Category";
        GLAccountCategories: TestPage "G/L Account Categories";
        CountBefore: Integer;
    begin
        // [GIVEN] The page open (its OnOpenPage seeds the default category set on an
        // empty table), and the resulting row count.
        GLAccountCategories.OpenEdit();
        CountBefore := GLAccountCategory.Count();

        // [WHEN] Action New is invoked. "New" is a C# reserved keyword, so BC's emitter
        // names the trigger _New_a45_OnAction - a leading underscore that neither the
        // backward scan nor the pre-fix forward mangle ever produced.
        GLAccountCategories.New.Invoke();

        // [THEN] Exactly one category row was inserted by the trigger's SetRow(Rec.InsertRow()).
        Assert.AreEqual(CountBefore + 1, GLAccountCategory.Count(),
            'action New must run its OnAction, which inserts one G/L Account Category row');
    end;

    [Test]
    procedure ActionRefToKeywordAction_OnPrecompiledBasePage_RunsTheTargetsOnAction()
    var
        GLAccountCategory: Record "G/L Account Category";
        GLAccountCategories: TestPage "G/L Account Categories";
        CountBefore: Integer;
    begin
        GLAccountCategories.OpenEdit();
        CountBefore := GLAccountCategory.Count();

        // [WHEN] The PROMOTED actionref is invoked. An actionref carries no trigger; the
        // runner must follow it to its target by name (#2113) - and for a precompiled page
        // that target name now comes from the dependency's SymbolReference.json.
        GLAccountCategories.New_Promoted.Invoke();

        // [THEN] Same effect as invoking New directly.
        Assert.AreEqual(CountBefore + 1, GLAccountCategory.Count(),
            'actionref New_Promoted must run its target''s OnAction');
    end;

    [Test]
    procedure SpacedControlName_OnPrecompiledBasePage_RunsItsPageOnValidate()
    var
        PermissionSetAssignments: TestPage "Permission Set Assignments";
    begin
        // [GIVEN] A new, empty row - the page global UserName is blank, which is the branch
        // of the control's page-level OnValidate that raises EmptyUserNameErr.
        PermissionSetAssignments.OpenEdit();
        PermissionSetAssignments.New();

        // [WHEN] A control whose AL name carries a space is set. The page declares
        // field("Company Name"; Rec."Company Name") { trigger OnValidate() ... } - emitted as
        // Company_Name_a45_OnValidate. RaiseOnValidate treats "no trigger found" as benign,
        // so before the fix this was a silent skip, not a refusal (#2517).
        asserterror PermissionSetAssignments."Company Name".SetValue(CompanyName());

        // [THEN] The page trigger's own error.
        Assert.ExpectedError('The User Name field must be filled in.');
    end;

    [Test]
    [HandlerFunctions('CaptureMessage')]
    procedure ActionOnPrecompiledPageExtension_OnPrecompiledBasePage_RunsItsOnAction()
    var
        ActivityLog: TestPage "Activity Log";
    begin
        // [GIVEN] The Activity Log with no rows, so Rec."Record ID" is blank and
        // PageManagement.PageRun answers false - the branch that shows NoRelatedRecordMsg.
        CapturedMessage := '';
        ActivityLog.OpenView();

        // [WHEN] OpenRelatedRecord is invoked. The action is declared NOT by page 710 but by
        // the precompiled pageextension 711 "Activity Log Extension"; its trigger lives on
        // PageExtension711 and its member id hashes from 711. GetPageExtensionIdsForPage
        // used to enumerate only source-parsed pageextensions, so 711 was never searched.
        ActivityLog.OpenRelatedRecord.Invoke();

        // [THEN] The extension trigger's own Message reached the handler.
        Assert.AreEqual('There are no related records to display.', CapturedMessage,
            'the pageextension''s OnAction must run and show NoRelatedRecordMsg');
    end;

    // #2931 resolved a precompiled page action's RunObject target out of the dependency .app's
    // SymbolReference.json, and refused the action anyway because it also carries a RunPageLink
    // the runner could not apply. #2942 applies it, and this arm is what proves the SYMBOLS
    // route of that work — the corpus cannot, because a corpus page is source-compiled and
    // therefore takes the compiled-metadata route instead.
    //
    // Page 5098 "Task Card" action "Co&mment" is a genuinely three-entry link, and each entry
    // is a different kind:
    //
    //     RunObject   = Page "Rlshp. Mgt. Comment Sheet"     (5072, over table 5061)
    //     RunPageLink = "Table Name" = const("To-do"),       an ENUM const, quoted in the AL
    //                   "No."        = field("Organizer To-do No."),   the HOST's field
    //                   "Sub No."    = const(0)              an integer const
    //
    // The symbol file states all of that as raw AL TEXT with no field numbers, so every name
    // here has to be resolved against two different tables — the target's (5061) for the
    // left-hand sides, the host's (5080) for the field(...) right-hand side. Four rows below
    // are seeded to fail exactly one entry each, so no single entry can be dropped without the
    // assertion changing.
    [Test]
    [HandlerFunctions('CommentSheetPageHandler')]
    procedure RunObjectActionWithRunPageLink_OnPrecompiledBasePage_OpensTheTargetFiltered()
    var
        Task: Record "To-do";
        TaskCard: TestPage "Task Card";
    begin
        // [GIVEN] Two comment lines the link selects, and three that fail one entry each.
        InsertComment("Rlshp. Mgt. Comment Line Table Name"::"To-do", 'PMN-TASK', 0, 10000, 'MATCH-A');
        InsertComment("Rlshp. Mgt. Comment Line Table Name"::"To-do", 'PMN-TASK', 0, 20000, 'MATCH-B');
        // Fails "No." = field("Organizer To-do No.") only.
        InsertComment("Rlshp. Mgt. Comment Line Table Name"::"To-do", 'PMN-OTHER', 0, 10000, 'WRONG-NO');
        // Fails "Sub No." = const(0) only.
        InsertComment("Rlshp. Mgt. Comment Line Table Name"::"To-do", 'PMN-TASK', 1, 10000, 'WRONG-SUBNO');
        // Fails "Table Name" = const("To-do") only.
        InsertComment("Rlshp. Mgt. Comment Line Table Name"::Contact, 'PMN-TASK', 0, 10000, 'WRONG-TABLE');

        // [GIVEN] A task whose "Organizer To-do No." is what the field(...) entry reads.
        Task.Init();
        Task."No." := 'PMN-TASK';
        Task."Organizer To-do No." := 'PMN-TASK';
        // The card's OnAfterGetRecord calls EnableFields -> GetEndDateTime, which builds a
        // DateTime out of these four and raises "The date is not valid." on a blank Date.
        Task.Date := Today();
        Task."Ending Date" := Today();
        Task."Start Time" := 080000T;
        Task."Ending Time" := 090000T;
        Task.Insert(false);

        TaskCard.OpenEdit();
        TaskCard.GotoRecord(Task);

        // [WHEN] The RunObject action is invoked. It has no OnAction trigger at all, so every
        // observable effect below is the platform's, not AL's.
        TaskCard."Co&mment".Invoke();
        TaskCard.Close();

        // [THEN] The target opened — and it opened on the LINKED rowset. The three near-miss
        // rows are the whole point: an implementation that opened page 5072 unfiltered would
        // report all five, one that dropped the field(...) entry would include WRONG-NO, one
        // that dropped either const(...) entry would include its row.
        Assert.AreEqual('MATCH-A,MATCH-B', CapturedComments,
            'the action''s RunPageLink must filter the target page to the rows it selects');
    end;

    local procedure InsertComment(TableName: Enum "Rlshp. Mgt. Comment Line Table Name"; No: Code[20]; SubNo: Integer; LineNo: Integer; CommentText: Text[80])
    var
        Comment: Record "Rlshp. Mgt. Comment Line";
    begin
        Comment.Init();
        Comment."Table Name" := TableName;
        Comment."No." := No;
        Comment."Sub No." := SubNo;
        Comment."Line No." := LineNo;
        Comment.Comment := CommentText;
        Comment.Insert(false);
    end;

    // #2931, and the reason the resolution above cannot simply trust a name. A precompiled
    // page's SymbolReference.json states RunObject as a bare NAME with no object type, and
    // 73 names in Base Application 28.1 are shared between a page and a report / codeunit /
    // xmlport / query. Page 31's action is one of 326 such actions: its AL says
    // `RunObject = Report "Item Substitutions"` (report 5701), and there is ALSO a page 5720
    // of that exact name. Resolving the name to a page and opening it would run the wrong
    // object and report nothing wrong — the silent-default failure loud-failures.md exists to
    // prevent — so the runner refuses and names the ambiguity.
    [Test]
    procedure AmbiguousRunObjectName_OnPrecompiledBasePage_RefusesRatherThanGuessing()
    var
        ItemList: TestPage "Item List";
    begin
        // [GIVEN] Page 31 "Item List", which ships precompiled.
        ItemList.OpenView();

        // [WHEN] The action whose RunObject names "Item Substitutions" is invoked.
        asserterror ItemList."Item Substitutions".Invoke();

        // [THEN] A loud refusal, anchored as a GAP so an expectations entry can track it.
        Assert.ExpectedError('out-of-scope: TestPage action');
        Assert.ExpectedError('not-yet-implemented');

        // [AND] It says WHY it will not act: the name is ambiguous, and it names the other
        // kind it collides with. Asserting the word "report" is what separates "refused
        // because ambiguous" from the unrelated refusals in this same method.
        Assert.ExpectedError('Item Substitutions');
        Assert.ExpectedError('report');
    end;

    [MessageHandler]
    procedure CaptureMessage(Msg: Text[1024])
    begin
        CapturedMessage := Msg;
    end;

    // Records every non-blank Comment the target showed, in order. Blank rows are skipped
    // rather than counted: page 5072 is an editable List, so its TestPage carries the implicit
    // new-row line, which is not part of the rowset the link selected and would otherwise put
    // a trailing empty entry into every expected value.
    [PageHandler]
    procedure CommentSheetPageHandler(var CommentSheet: TestPage "Rlshp. Mgt. Comment Sheet")
    var
        Value: Text;
    begin
        CapturedComments := '';
        if CommentSheet.First() then
            repeat
                Value := CommentSheet.Comment.Value();
                if Value <> '' then begin
                    if CapturedComments <> '' then
                        CapturedComments += ',';
                    CapturedComments += Value;
                end;
            until not CommentSheet.Next();
    end;
}
