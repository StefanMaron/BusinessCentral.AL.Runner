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
    Subtype = Test;

    local procedure Initialize()
    var
        Row: Record "Pecm Row";
    begin
        Row.DeleteAll();
    end;

    // Positive: RunModal materialises the page at all (before the fix, this line alone threw),
    // the [ModalPageHandler] runs, and the handler's SetValue reaches the page's own OnValidate
    // trigger — proof the enum-bound control is a live, functioning part of the page, not just
    // a construction that happens to not crash.
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

    [ModalPageHandler]
    procedure KindHandler(var Modal: TestPage "Pecm Modal")
    begin
        Modal.KindSelector.SetValue('Block');
        Modal.OK().Invoke();
    end;
}
