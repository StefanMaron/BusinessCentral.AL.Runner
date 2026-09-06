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
/// pinned as correct. The refusal claim moved onto the two shapes the runner genuinely still
/// declines: a RunObject naming a REPORT, and an action declaring no effect at all. #2942 moved
/// the RunPageLink arm across the same way, for the same reason.
///
/// #2975 then corrected the replacement. Those arms had been rewritten to assert that BC's own
/// "Unhandled UI" error surfaces when no [PageHandler] is bound -- a claim about BC, made in a
/// runner-local test, and eight real service tiers have since falsified it (corpus codeunit
/// 60285 "TPARONH Tests": an unattended RunObject OPENS its target and raises nothing). They now
/// assert the runner-specific half only: no refusal of the runner's own, and -- through a target
/// page that records its own opening -- that the dispatch really reaches the page instead of
/// quietly doing nothing.
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
        OpenLog: Record "Par Open Log";
        Row: Record "Par Row";
    begin
        Row.DeleteAll();
        OpenLog.DeleteAll();
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

    // Runner-specific, #2931 and #2975: a RunObject naming a PAGE is PERFORMED, and it is
    // performed even with no [PageHandler] bound. The BC-behaviour half — that real BC opens the
    // target unattended and raises nothing AL can see — is adjudicated upstream on eight service
    // tiers (handlers/TestPageActionRunObjectNoHandler_Tests.al, codeunit 60285). What is
    // runner-specific and belongs here is that the runner raises no refusal of its OWN, and that
    // the dispatch really reaches the page rather than quietly doing nothing.
    //
    // Both halves are asserted, and both are needed. Before #2975 this arm asserted that
    // 'Unhandled UI' surfaced — a BC claim that eight tiers have since falsified — and after the
    // fix nothing is raised at all, so "the statement completed" on its own would also pass
    // against a runner that performed no RunObject whatsoever. The target page's OnOpenPage tag
    // is what rules that out.
    [Test]
    procedure RunObjectPageActionIsPerformedAndNoLongerRefusedByTheRunner()
    var
        OpenLog: Record "Par Open Log";
        Row: Record "Par Row";
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        HostPage.TriggerlessAction.Invoke();

        Assert.IsTrue(OpenLog.Get('RUNOBJ-OPENED'),
            'a RunObject page action must be performed and its target opened, even with no handler bound');
        Assert.IsFalse(OpenLog.Get('RUNOBJ-LINKED'),
            'this action declares no RunPageLink, so the target must not have opened filtered');
        Assert.IsFalse(Row.Get('FLAT'),
            'invoking TriggerlessAction must not have run an unrelated action''s trigger');
    end;

    // Same, through the reference: an actionref delegates, so it must reach the same RunObject
    // as its target and not fall back to the refusal.
    [Test]
    procedure ActionrefToARunObjectPageActionIsPerformedToo()
    var
        OpenLog: Record "Par Open Log";
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        HostPage.TriggerlessRef.Invoke();

        Assert.IsTrue(OpenLog.Get('RUNOBJ-OPENED'),
            'an actionref must reach its target''s RunObject and open the same page, not be refused as declaring no effect');
    end;

    // #2942 changed what this arm proves, the same way #2931 changed the two above it, and
    // #2975 changed it again. The runner used to REFUSE a RunObject action carrying a
    // RunPageLink; #2942 made it apply the link, and this arm then asserted that BC's own
    // unhandled-UI error came through instead of a runner refusal. #2975 falsified that second
    // half too: with nothing bound the target opens unattended and AL is told nothing, so there
    // is no error left to read.
    //
    // What stays runner-specific, and is asserted here: the runner raises no refusal of its own
    // (a refusal would fail this test on the un-trapped Invoke, which is why there is no
    // asserterror), the linked RunObject really reaches the target page, and the target opens
    // FILTERED rather than on its whole table. Without that last assertion this arm and
    // RunObjectPageActionIsPerformedAndNoLongerRefusedByTheRunner would assert the same thing.
    //
    // What the link SELECTS is plain BC behaviour, so it is proven upstream rather than here --
    // StefanMaron/BusinessCentral.AL.Language.Tests, handlers/TestPageActionRunPageLink.al,
    // six arms over field / const / filter links, an unlinked control and an empty rowset.
    [Test]
    procedure RunObjectWithRunPageLinkIsPerformedWithoutARunnerRefusal()
    var
        OpenLog: Record "Par Open Log";
        Row: Record "Par Row";
        HostPage: TestPage "Par Host Page";
    begin
        Initialize();

        // The host needs a row for `field("No.")` to read: an empty host rowset would make the
        // link resolve to a blank filter, which is indistinguishable from no filter at all.
        Row.Init();
        Row."No." := 'LINKSRC';
        Row.Insert();

        HostPage.OpenEdit();
        HostPage.LinkedPageAction.Invoke();

        Assert.IsTrue(OpenLog.Get('RUNOBJ-OPENED'),
            'a RunObject action carrying a RunPageLink must be performed and its target opened');
        Assert.IsTrue(OpenLog.Get('RUNOBJ-LINKED'),
            'the action''s RunPageLink must be applied to the target''s rowset, not dropped');
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
