codeunit 64513 "Pecm Tests"
{
    // Regression for issue #1896: Page.RunModal() on a page whose layout binds a control to a
    // page-GLOBAL variable of type Enum threw
    //
    //   NavALException: You tried to invoke the Enum object with the ID <id> from the object
    //   <the CALLING codeunit's own name>. An object with that ID does not exist in the
    //   current application compiled with emit version <N>.
    //
    // at form materialisation — NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions
    // calls NCLMetadata.TryGetMetaApplicationObject(ObjectType.Enum, ...), which the runner
    // never populated for Enum objects (AL enums were only ever served through the SEPARATE
    // NCLEnumMetadata.Create(int) hook, a codepath page materialisation never reaches). See
    // AlRunner/Patches/PageEnumFieldMetadataPatches.cs for the full root-cause writeup.
    //
    // The "from the object <test codeunit>" misattribution in the original error is a genuine
    // clue, not noise: no page-scoped NavMethodScope exists yet at the point the lookup fails
    // (NavForm.RunModalAsync loads metadata BEFORE pushing one), so NavMethodScope.Run()'s
    // remap reports the CALLING scope's own object name.
    //
    // Issue #1928 correction: the handler below originally called
    // Modal.KindSelector.SetValue('Block') — the enum MEMBER name — and that passed here while
    // it fails identically on real BC (StefanMaron/BusinessCentral.AL.Language.Tests#50, run
    // against a real BC service tier on two BC versions): "Your entry of 'Block' is not an
    // acceptable value for 'Kind'." Real BC resolves an Enum-typed TestPage control by its
    // declared Caption ('Blocks') and refuses the member name. This file now uses the caption
    // everywhere a value is set, and RunModal_EnumGlobalControl_SetValueRejectsTheBareMemberName
    // below pins the refusal itself so this regression cannot recur silently.
    Subtype = Test;

    local procedure Initialize()
    var
        Row: Record "Pecm Row";
    begin
        Row.DeleteAll();
    end;

    // Positive: RunModal materialises the page at all (before the #1896 fix, this line alone
    // threw), the [ModalPageHandler] runs, and the handler's SetValue(caption) reaches the
    // page's own OnValidate trigger — proof the enum-bound control is a live, functioning part
    // of the page, not just a construction that happens to not crash.
    [Test]
    [HandlerFunctions('KindHandler')]
    procedure RunModal_EnumGlobalControl_HandlerSetsValueAndOnValidateSeesIt()
    var
        Echo: Record "Pecm Row";
        Modal: Page "Pecm Modal";
    begin
        Initialize();

        Modal.RunModal();

        if not Echo.Get('KIND') then
            Error('the [ModalPageHandler] must have run and OnValidate must have fired');
    end;

    // Positive, concrete value: after RunModal returns, the page variable itself holds the
    // SPECIFIC member the handler chose (Block, ordinal 1) — not the field's zero default, and
    // not some other member. A fix that made materialisation "succeed" by falling back to a
    // blank/default enum value would pass the test above (Echo row still gets written) but
    // fail this one.
    [Test]
    [HandlerFunctions('KindHandler')]
    procedure RunModal_EnumGlobalControl_ProcedureReadsBackTheHandlerChosenValue()
    var
        Modal: Page "Pecm Modal";
    begin
        Initialize();

        Modal.RunModal();

        if Modal.GetSelectedKindOrdinal() <> 1 then
            Error('the page variable must hold the handler-set member (Block = 1), got %1 — expected the real value, not the default (Field = 0)', Modal.GetSelectedKindOrdinal());
    end;

    // Control: WITHOUT RunModal, the same page variable's procedure still works and reads the
    // declared default. This is the exact split the original report described — the enum
    // itself is always compiled and reachable; only FORM MATERIALISATION could regress.
    [Test]
    procedure GetSelectedKindOrdinal_WithoutRunModal_ReadsTheDeclaredDefault()
    var
        Modal: Page "Pecm Modal";
    begin
        Initialize();

        if Modal.GetSelectedKindOrdinal() <> 0 then
            Error('without RunModal the page variable never left its declared default (Field = 0), got %1', Modal.GetSelectedKindOrdinal());
    end;

    // Issue #1928, positive direction: TestPage.SetValue on the Enum-typed control resolves by
    // the enum's declared Caption ('Blocks'), not by the member name ('Block') and not by a
    // caption equal to the member name — "Blocks" != "Block" is the whole point, it is what
    // makes RunModal_EnumGlobalControl_ProcedureReadsBackTheHandlerChosenValue above a real
    // proof (SetValue('Blocks') resolving to the Block member, ordinal 1) rather than a
    // coincidence of identical spellings. (Reading the control back through TestPage.Value() —
    // as opposed to reading the underlying page variable via GetSelectedKindOrdinal(), which
    // is what that test already does — is a separate, PageVariableTestField-specific gap with
    // no real-BC evidence pinning its expected spelling either way; out of scope here.)
    //
    // Issue #1928, negative direction: the bare member name is NOT an acceptable
    // TestPage.SetValue spelling for an Enum-typed control — real BC refuses it (see the file
    // header). A runner that silently accepted 'Block' here (the pre-fix, and real-BC-wrong,
    // behavior) would pass this SetValue without error, so the asserterror below is the actual
    // proof, not a formality: it fails loudly if the divergence regresses. The row must NOT
    // have been written for the rejected value either — a rejected SetValue does not silently
    // fall through to some other member.
    [Test]
    [HandlerFunctions('KindHandlerRejectsMemberName')]
    procedure RunModal_EnumGlobalControl_SetValueRejectsTheBareMemberName()
    var
        Echo: Record "Pecm Row";
        Modal: Page "Pecm Modal";
    begin
        Initialize();

        Modal.RunModal();

        if Echo.Get('KIND') then
            Error('OnValidate must not have fired — the rejected SetValue never reached the control''s bound value');
    end;

    [ModalPageHandler]
    procedure KindHandler(var Modal: TestPage "Pecm Modal")
    begin
        Modal.KindSelector.SetValue('Blocks');
        Modal.OK().Invoke();
    end;

    [ModalPageHandler]
    procedure KindHandlerRejectsMemberName(var Modal: TestPage "Pecm Modal")
    begin
        asserterror Modal.KindSelector.SetValue('Block');
        if StrPos(GetLastErrorText(), 'Block') = 0 then
            Error('Expected the error to name the rejected value ''Block'', but got: %1', GetLastErrorText());
        Modal.OK().Invoke();
    end;
}
