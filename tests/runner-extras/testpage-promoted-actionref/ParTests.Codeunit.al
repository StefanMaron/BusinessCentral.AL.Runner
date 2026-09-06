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
///   - the effect-less arms                     already pass, and must keep passing
///
/// GREEN: every arm logs its own tag and only its own tag, and the effect-less arms still
/// refuse loudly.
///
/// #2931 changed what "triggerless" means here. An action carries EITHER an OnAction trigger or
/// a RunObject, and the runner now PERFORMS a RunObject that names a page, so the two arms that
/// used to assert a runner refusal for `RunObject = page ...` would have kept a fixed defect
/// pinned as correct. They now assert the opposite -- that no runner refusal is raised and BC's
/// own unhandled-UI error comes through instead -- and the refusal claim moved onto the shapes
/// the runner genuinely still declines: a RunObject naming a REPORT, and an action declaring no
/// effect at all. #2942 moved the RunPageLink arm across the same way, for the same reason.
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

    // Runner-specific, #2931: a RunObject naming a PAGE is now PERFORMED, so the runner must
    // stop raising a refusal of its own here. The BC-behaviour half — which handler answers the
    // opened page, and that an unattended open is refused — is adjudicated upstream
    // (handlers/TestPageActionRunObject_Tests.al); what is runner-specific and belongs here is
    // that the failure the test sees is BC's, not ours. Asserting the ABSENCE of the
    // out-of-scope prefix is the whole point: a regression that reinstated the refusal would
    // still fail loudly and would still be an asserterror, so only this distinguishes them.
    [Test]
    procedure RunObjectPageActionIsPerformedAndNoLongerRefusedByTheRunner()
    var
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        asserterror HostPage.TriggerlessAction.Invoke();

        Assert.Contains(GetLastErrorText(), 'Unhandled UI',
            'a RunObject page action must be performed, and with no handler declared BC''s own unhandled-UI error is what surfaces');
        Assert.NotContains(GetLastErrorText(), 'out-of-scope',
            'the runner must no longer raise a refusal of its own for a RunObject that names a page');
    end;

    // Same, through the reference: an actionref delegates, so it must reach the same RunObject
    // as its target and not fall back to the refusal.
    [Test]
    procedure ActionrefToARunObjectPageActionIsPerformedToo()
    var
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        asserterror HostPage.TriggerlessRef.Invoke();

        Assert.Contains(GetLastErrorText(), 'Unhandled UI',
            'an actionref must reach its target''s RunObject, not be refused as declaring no effect');
        Assert.NotContains(GetLastErrorText(), 'out-of-scope',
            'the runner must no longer raise a refusal of its own for an actionref to a RunObject page action');
    end;

    // #2942 changed what this arm proves, the same way #2931 changed the two above it. The
    // runner used to refuse a RunObject action carrying a RunPageLink, because opening the page
    // without its link filters would have shown a different rowset than real BC. It now APPLIES
    // the link, so this arm asserts the opposite: no runner refusal of its own, and BC's own
    // unhandled-UI error comes through exactly as it does for the unlinked TriggerlessAction.
    //
    // What the link SELECTS is plain BC behaviour, so it is proven upstream rather than here --
    // StefanMaron/BusinessCentral.AL.Language.Tests, handlers/TestPageActionRunPageLink.al,
    // six arms over field / const / filter links, an unlinked control and an empty rowset. The
    // claim that stays runner-specific is this one: that the runner raises nothing itself.
    [Test]
    procedure RunObjectWithRunPageLinkIsPerformedWithoutARunnerRefusal()
    var
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        asserterror HostPage.LinkedPageAction.Invoke();

        Assert.Contains(GetLastErrorText(), 'Unhandled UI',
            'a RunObject action carrying a RunPageLink must reach BC''s own page dispatch');
        Assert.NotContains(GetLastErrorText(), 'out-of-scope',
            'the runner must no longer raise a refusal of its own for an action''s RunPageLink');
        Assert.NotContains(GetLastErrorText(), 'not-yet-implemented',
            'the RunPageLink gap is closed, so nothing may still be anchored as one');
    end;

    // Runner-specific, #2931: only a RunObject naming a PAGE is performed so far. A report
    // target must refuse with the same gap anchor rather than open nothing quietly.
    [Test]
    procedure RunObjectNamingAReportRefusesAsANotYetImplementedGap()
    var
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        asserterror HostPage.ReportRunObjectAction.Invoke();

        Assert.Contains(GetLastErrorText(), 'not-yet-implemented',
            'a RunObject naming a report must be refused with a gap anchor');
        Assert.Contains(GetLastErrorText(), 'Report',
            'the refusal must name the object KIND it declined, so the reader knows which gap this is');
        Assert.Contains(GetLastErrorText(), '2943',
            'the refusal must cite the OPEN issue tracking the gap, not the one whose fix closed');
    end;

    // Runner-specific: an action with neither a trigger nor a RunObject genuinely has nothing to
    // run. Invoking it must refuse loudly rather than do nothing — doing nothing is what made an
    // unrun action surface one step later as an assertion about its missing effect.
    [Test]
    procedure ActionWithNoEffectAtAllStillRefusesLoudly()
    var
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        asserterror HostPage.NoEffectAction.Invoke();

        Assert.Contains(GetLastErrorText(), 'not-yet-implemented',
            'an action with neither a trigger nor a RunObject must still raise the loud refusal');
    end;

    // Same claim through the reference, plus the message contract: the refusal must name the
    // TARGET action. The pre-#2113 message blamed the actionref for "declaring no OnAction
    // trigger", which is true of every actionref by construction and told the reader nothing.
    [Test]
    procedure ActionrefToAnEffectlessActionRefusesNamingItsTarget()
    var
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        asserterror HostPage.NoEffectRef.Invoke();

        Assert.Contains(GetLastErrorText(), 'not-yet-implemented',
            'an actionref pointing at an effect-less action must still raise the loud refusal');
        Assert.Contains(GetLastErrorText(), 'NoEffectAction',
            'the refusal must name the actionref''s TARGET, not blame the actionref for carrying no trigger');
    end;

    // Runner-specific message contract, #2931: RunnerOutOfScopeException always appends its own
    // " — see docs/scope.md" link, so a throw site that also wrote "See docs/scope.md" into its
    // reason rendered "… See docs/scope.md — see docs/scope.md". 47 throw sites did that. The
    // fix normalises it in the exception itself, so any refusal is a witness for all of them.
    [Test]
    procedure ARefusalNamesDocsScopeExactlyOnce()
    var
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        asserterror HostPage.NoEffectAction.Invoke();

        Assert.AreEqual(1, CountOccurrences(GetLastErrorText(), 'docs/scope.md'),
            'a refusal must point at docs/scope.md exactly once');
    end;

    local procedure CountOccurrences(Haystack: Text; Needle: Text) Count: Integer
    var
        Position: Integer;
    begin
        if Needle = '' then
            exit(0);
        Position := StrPos(Haystack, Needle);
        while Position > 0 do begin
            Count += 1;
            Haystack := CopyStr(Haystack, Position + StrLen(Needle));
            Position := StrPos(Haystack, Needle);
        end;
    end;
}
