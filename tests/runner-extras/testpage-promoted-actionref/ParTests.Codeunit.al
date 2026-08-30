/// Regression suite for #2113: TestPage Invoke() of an actionref never followed the reference
/// to its target action, so every promoted `Invoke()` was refused with
/// "testpage-action — the page declares no OnAction trigger for this action" against an action
/// that plainly declares one.
///
/// RED (without the fix), measured:
///   - DirectInvokeRunsTheTargetsTrigger        passes (control arm, unaffected)
///   - FlatPromotedActionrefRunsTargetsTrigger  throws the testpage-action refusal
///   - GroupedPromotedActionrefRunsTargetsTrigger              same
///   - ExtensionActionrefToExtensionActionRunsTargetsTrigger   same
///   - ExtensionActionrefToBasePageActionRunsTargetsTrigger    same
///   - PrecompiledBasePageExtensionActionrefRunsTargetsTrigger  same, on page 7500
///   - the two triggerless arms                 already pass, and must keep passing
///
/// GREEN: every arm logs its own tag and only its own tag, and the two triggerless arms still
/// refuse loudly.
///
/// The plain-BC half of the first eight arms is covered upstream in the al-language corpus:
/// StefanMaron/BusinessCentral.AL.Language.Tests#79, commit c98be548, green on real BC 27.5
/// and 28.3 (handlers/TestPagePromotedActionref*.al). This suite keeps them anyway because it
/// gates the same shapes on every BC version in this repo's matrix rather than the corpus's
/// two, and because the last test's claim is runner-specific and has no upstream home.
codeunit 64545 "Par Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Par Assert";

    local procedure Initialize()
    var
        Row: Record "Par Row";
    begin
        Row.DeleteAll();
    end;

    // Control arm: invoking the target action DIRECTLY. If this ever fails, every arm below
    // is measuring broken plumbing rather than #2113.
    [Test]
    procedure DirectInvokeRunsTheTargetsTrigger()
    var
        Row: Record "Par Row";
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        HostPage.FlatTarget.Invoke();
        HostPage.Close();

        Assert.IsTrue(Row.Get('FLAT'), 'invoking the action directly must run its OnAction trigger');
    end;

    // The reported shape: an actionref sitting directly in the page's own area(Promoted).
    [Test]
    procedure FlatPromotedActionrefRunsTargetsTrigger()
    var
        Row: Record "Par Row";
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        HostPage.FlatRef.Invoke();
        HostPage.Close();

        Assert.IsTrue(Row.Get('FLAT'),
            'invoking a promoted actionref must run the OnAction trigger of the action it points at');
    end;

    // The same reference one level down, inside a promoted category group — the layout every
    // real promoted-action page actually uses.
    [Test]
    procedure GroupedPromotedActionrefRunsTargetsTrigger()
    var
        Row: Record "Par Row";
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        HostPage.GroupedRef.Invoke();
        HostPage.Close();

        Assert.IsTrue(Row.Get('GROUPED'),
            'an actionref inside a promoted group must run its target''s OnAction trigger');
    end;

    // Isolation: the resolution must land on the actionref's OWN target, not on whichever
    // trigger the scan happens to reach first. A fix that fell back to "the page's first
    // OnAction method" would pass both positives above and fail here.
    [Test]
    procedure PromotedActionrefRunsOnlyItsOwnTargetsTrigger()
    var
        Row: Record "Par Row";
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        HostPage.GroupedRef.Invoke();
        HostPage.Close();

        Assert.IsTrue(Row.Get('GROUPED'), 'the invoked actionref''s target must have run');
        Assert.IsFalse(Row.Get('FLAT'),
            'invoking GroupedRef must not have run FlatTarget''s trigger');
        Assert.IsFalse(Row.Get('NEVER'),
            'invoking GroupedRef must not have run an unrelated action''s trigger');
    end;

    // Cross-id-space arm 1: the actionref and its target are both declared by the
    // PAGEEXTENSION, so the target's member id hashes from the extension's object id (64542).
    [Test]
    procedure ExtensionActionrefToExtensionActionRunsTargetsTrigger()
    var
        Row: Record "Par Row";
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        HostPage.ExtRefToExtTarget.Invoke();
        HostPage.Close();

        Assert.IsTrue(Row.Get('EXT'),
            'a pageextension''s promoted actionref must run its own extension action''s OnAction trigger');
    end;

    // Cross-id-space arm 2, the one that pins the "follow by NAME, re-derive the id per
    // candidate object" rule: the actionref is declared by the pageextension (id space 64542)
    // but its target is declared by the BASE PAGE (id space 64541). Resolving the target id in
    // the declaring object's id space alone finds nothing here.
    [Test]
    procedure ExtensionActionrefToBasePageActionRunsTargetsTrigger()
    var
        Row: Record "Par Row";
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        HostPage.ExtRefToBaseTarget.Invoke();
        HostPage.Close();

        Assert.IsTrue(Row.Get('BASE-VIA-EXT'),
            'a pageextension''s promoted actionref pointing at a BASE PAGE action must run that action''s trigger');
    end;

    // The base page ships PRECOMPILED in Base Application, so this arm proves the resolution
    // does not quietly depend on the base page having been compiled from source in this
    // bundle: the actionref, its target and the trigger are all contributed by the extension,
    // and the page they hang off is not ours. Measured RED: the same testpage-action refusal,
    // reported against page 7500.
    [Test]
    procedure PrecompiledBasePageExtensionActionrefRunsTargetsTrigger()
    var
        Row: Record "Par Row";
        ItemAttrPage: TestPage "Item Attributes";
    begin
        Initialize();

        ItemAttrPage.OpenEdit();
        ItemAttrPage.BaseAppExtRef.Invoke();
        ItemAttrPage.Close();

        Assert.IsTrue(Row.Get('BASEAPP-EXT'),
            'a promoted actionref added to a PRECOMPILED base page must run its extension target''s OnAction trigger');
    end;

    // Runner-specific negative, and the one claim that could not go upstream at all: on real BC
    // a RunObject action's Invoke() opens the page, so no service tier can adjudicate this
    // refusal. An action whose whole effect is RunObject genuinely declares no OnAction, and
    // opening another page is a surface the runner refuses. That refusal must survive the fix — a
    // resolution that answered "nothing to run, so run nothing" would make every arm above
    // green while removing the loud failure loud-failures.md requires.
    [Test]
    procedure TriggerlessActionStillRefusesLoudly()
    var
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        asserterror HostPage.TriggerlessAction.Invoke();

        Assert.Contains(GetLastErrorText(), 'testpage-action',
            'a RunObject action with no OnAction must still raise the named testpage-action refusal');
    end;

    // Same claim through the reference, plus the message contract: the refusal must now name
    // the TARGET action. The pre-fix message blamed the actionref for "declaring no OnAction
    // trigger", which is true of every actionref by construction and told the reader nothing.
    [Test]
    procedure ActionrefToTriggerlessActionRefusesNamingItsTarget()
    var
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        asserterror HostPage.TriggerlessRef.Invoke();

        Assert.Contains(GetLastErrorText(), 'testpage-action',
            'an actionref pointing at a triggerless action must still raise the testpage-action refusal');
        Assert.Contains(GetLastErrorText(), 'TriggerlessAction',
            'the refusal must name the actionref''s TARGET, not blame the actionref for carrying no trigger');
    end;
}
