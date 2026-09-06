// RunnerPageInstance.ActionRunObject — performing a page action whose effect is RunObject.
//
// THE GAP (issue #2931)
//   An AL action can carry an OnAction trigger, or a RunObject property, never both. The
//   trigger half has always been dispatched here; the RunObject half was refused as
//   out-of-scope with a message pointing at docs/scope.md, which says nothing about it.
//   That was a misclassification with a cost: `expect-oos` carries no issue link, so a real
//   gap recorded that way stops being work. `loud-failures.md` puts this squarely in scope —
//   "rendering is out of scope; callback dispatch is in scope" — and the machinery to open a
//   page into the test's handler already exists and is exercised by the corpus.
//
// WHAT REAL BC DOES, AND HOW THAT WAS ESTABLISHED
//   There is no reference implementation in Ncl.dll: `ITestAction` is implemented CLIENT-side
//   (Microsoft.Dynamics.Nav.Client.TestPageClient.TestActionProxy), which forwards to the
//   UI framework's own action pipeline. Reading that pipeline (BC 28.1) settles two things
//   this file depends on:
//
//   1. WHICH OBJECT. `ActionBuilder.CreateRunObject` switches on `ActionDefinition.RunObjectType`
//      and uses `ActionDefinition.TargetID` — a resolved numeric object id, not a name — with
//      `RunPageOnRec` carried through as `NavOpenTaskPageAction.RunFormOnRecordField`. When it
//      is set, `NavOpenTaskPageAction.CreateForm` stamps the HOST page's current row onto the
//      target's form state (`SetBookmark(formState, parentBindingManager.CurrentRow.Bookmark)`).
//
//   2. MODAL OR NOT — and this is the part that is easy to get wrong. It does NOT follow from
//      the action. `NavOpenTaskPageAction.ShowForm` branches on `FormState.RunModal`, which
//      defaults to false and is set in exactly two places in the whole builder assembly:
//      `NavigatePageBuilder` and `StandardDialogBuilder`. So the TARGET page's PageType decides
//      it: PageType NavigatePage or StandardDialog opens as a dialog, everything else opens
//      as an ordinary form. In a test session those two land in different places —
//      `UISession.ShowForm` -> `NavTestExecution.ShowForm` -> `FindHandler(NavHandlerType.Page)`
//      = [PageHandler]; `LogicalForm.ShowDialog` -> `NavTestExecution.ShowDialog` ->
//      `FindHandler(NavHandlerType.ModalPage)` = [ModalPageHandler]. `FindHandler` matches the
//      handler type EXACTLY, so the two are not interchangeable.
//
//   Microsoft's own tests corroborate the split independently. Base Application 26.0 deprecated
//   page 52's `Statistics` action (an OnAction trigger calling `Rec.OpenDocumentStatistics()`,
//   which does an AL `Page.RunModal`) in favour of `PurchaseStatistics`
//   (`RunObject = Page "Purchase Statistics"; RunPageOnRec = true;`), and Tests-ERM codeunit
//   134394 carries BOTH a `[ModalPageHandler] PurchaseStatisticsModalHandler` guarded by
//   `#if not CLEAN26` for the old trigger route and a plain `[PageHandler]
//   PurchaseStatisticsPageHandler` for the new RunObject route. Page 161 "Purchase Statistics"
//   is `PageType = ListPlus`, so by the rule above the RunObject route is non-modal — which is
//   exactly why Microsoft had to add the second handler.
//
// WHAT THIS FILE DOES
//   Resolves the action's RunObject target, then hands the work to BC's OWN front door —
//   `NavForm.RunAsync` or `NavForm.RunModalAsync`. Nothing about handler lookup, trapping, or
//   the "Unhandled UI" refusal is reimplemented: those are BC's, reached exactly as an AL
//   `Page.Run` / `Page.RunModal` reaches them, so a RunObject action and the equivalent AL call
//   cannot drift apart.
//
// RUNPAGELINK (issue #2942)
//   A third of the Base Application's RunObject actions carry one: measured over all 5,668
//   action `RunObject` declarations in Base Application 28.1's SymbolReference.json, 1,819 also
//   declare `RunPageLink` — and ZERO of them declare `RunPageOnRec` alongside it.
//
//   BC applies the link as ordinary table filters on the TARGET's rowset, not as anything
//   action-specific. `ActionBuilder.GetApplicationFilters` copies every
//   `ActionDefinition.RunFormLink.TableFilters` entry into the action's
//   `ApplicationActionFilterContext`, and `NavOpenTaskPageAction.CreateForm` calls
//   `FilterContext.SetFilterContext(formState, parentBindingManager)` — which turns each
//   `FilterDefinition` into a `NavFilter` through `NavFilterHelper.AddFilter`, resolving a FIELD
//   entry against the HOST's current row — and only THEN, separately and unconditionally, stamps
//   `RunFormOnRecordField`'s bookmark. So the two are independent steps that both run, which is
//   why this file applies the link and the record together rather than treating them as
//   alternatives.
//
//   Here the same thing is expressed as AL would: the target's own record cursor, filtered, handed
//   to `NavForm.RunAsync` / `RunModalAsync` exactly as `Rec.SetRange(...); Page.Run(id, Rec)`
//   does. FIELD reads the host's field value, CONST filters to a literal, FILTER hands BC's own
//   filter parser the compiled expression — the same three kinds, applied the same way, as
//   `LiveNavTestPart.ApplyLink` already applies a part's SubPageLink, and the CONST quoting rule
//   is literally shared with it (`LiveNavTestPart.ConstFilterExpression`).
//
// NO HANDLER BOUND — THE TARGET STILL OPENS (issue #2975)
//   The AL routes that open a page (`Page.Run` / `Page.RunModal`) are REFUSED when the test
//   binds nothing to answer them: NavTestExecution.TestHandleForm asks FindHandler with its
//   `throwIfNotFound = true` default and the resulting NavNCLMissingUIHandlerException
//   ("Unhandled UI: Page N") surfaces to AL while NavForm.RunAsync is still on its call stack.
//   Company.RegisterForm runs only AFTER that lookup succeeds, so on the AL route a
//   handler-less page is never registered, never opens, and never raises its OnOpenPage.
//
//   The RunObject route does NOT behave that way, and eight real service tiers say so — corpus
//   codeunit 60285 "TPARONH Tests", green on 27.0, 27.3, 27.5 and 28.0-28.4. Microsoft's
//   client-services layer builds, registers and OPENS the target first and only then asks
//   NavTestExecution.ShowForm for a handler, so the target's OnOpenPage has already run by the
//   time BC finds nobody bound; and the throw is then off AL's call stack, where
//   NavOpenTaskPageAction.ShowForm (Microsoft.Dynamics.Nav.Client.UI.dll, 28.1) catches it:
//
//       catch (NavTestBaseException) { throw; }                     // reaches AL
//       catch (NavBaseException ex3) {                              // does NOT reach AL
//           if (!ex3.SuppressMessage) ...MessageHelper.ShowError(ex3, showModal: true);
//           childForm.Close(FormClose.ForceClose);
//       }
//
//   NavNCLMissingUIHandlerException derives from NavNCLException -> NavException ->
//   NavBaseException and is NOT a NavTestBaseException, so it lands in the second arm: the
//   error is shown to a user who is not there, the form is force-closed, and Invoke() returns
//   normally. The two corpus arms measure exactly both halves — the target's OnOpenPage row is
//   written, on the HOST's current row (RunPageOnRec), and AL sees no error.
//
//   So the non-modal RunObject route below asks BC's own FindHandler whether anything is bound
//   BEFORE running the page, and when nothing is, opens the target through BC's own
//   NavForm.OpenForm and force-closes it the way Microsoft's catch does. Nothing about handler
//   matching, trapping or OnOpenPage is reimplemented — see RunTargetPage.
//
// WHAT IS STILL REFUSED, LOUDLY
//   RunObject targeting a Report / Codeunit / XmlPort / Query. It raises with a
//   `not-yet-implemented` reason anchor, which `docs/expectations.md` lets a manifest track as
//   `expect-fail-known-gap` against an OPEN issue — the classification the old `testpage-action`
//   anchor made impossible. Answering it by opening something else would be a silent wrong
//   answer, which is what `loud-failures.md` exists to prevent. The same applies to a link whose
//   fields this run cannot resolve to numbers: a link that cannot be applied is refused by name,
//   never dropped, because a dropped link shows the target's WHOLE table.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types.Exceptions;
using MetaTypes = Microsoft.Dynamics.Nav.Types.Metadata;

namespace AlRunner.Patches;

internal sealed partial class RunnerPageInstance
{
    /// <summary>
    /// One action's RunObject declaration, however it was recovered.
    /// <paramref name="ObjectId"/> is 0 when only a NAME was available (a page shipped
    /// precompiled in a dependency .app states <c>RunObject</c> as a name, with no object type
    /// alongside it) and the name could not be resolved against this run's page inventory.
    /// </summary>
    private readonly record struct ActionRunTarget(
        MetaTypes.RunObjectType Kind,
        int ObjectId,
        string? ObjectName,
        bool RunPageOnRec,
        IReadOnlyList<ActionRunLink> Links);

    /// <summary>
    /// One entry of an action's <c>RunPageLink</c>, resolved to the three things applying it
    /// needs. Deliberately the same triple <c>MockTestPage.SubPageLinkEntry</c> carries for a
    /// part's <c>SubPageLink</c>, because BC represents both as a <c>FilterDefinition</c> list
    /// and the runner applies both the same way.
    ///
    /// <para><paramref name="HostFieldNo"/> is meaningful only for <c>FIELD</c> (it names a
    /// field on the HOST's source table, whose current value becomes the filter);
    /// <paramref name="Value"/> only for <c>CONST</c> and <c>FILTER</c>.</para>
    /// </summary>
    private readonly record struct ActionRunLink(
        int TargetFieldNo,
        MetaTypes.FilterType Kind,
        int HostFieldNo,
        string Value);

    private static readonly IReadOnlyList<ActionRunLink> NoLinks = Array.Empty<ActionRunLink>();

    /// <summary>
    /// Perform <paramref name="actionId"/>'s RunObject, if it declares one.
    ///
    /// <para>Returns <c>false</c> — and does nothing — ONLY when the action declares no
    /// RunObject at all, which is the caller's cue to refuse. A RunObject the runner cannot
    /// yet perform faithfully throws from here instead of returning, so the two cases stay
    /// distinguishable in the message the developer reads.</para>
    /// </summary>
    private bool TryRunActionRunObject(int actionId)
    {
        if (ResolveActionRunTarget(actionId) is not { } target) return false;

        if (target.Kind != MetaTypes.RunObjectType.Page)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage action {actionId} on page {_pageId}",
                $"not-yet-implemented — the action declares RunObject = {target.Kind} "
                + $"{Describe(target)}, and the runner only performs a RunObject that names a "
                + "PAGE so far. Opening a report, codeunit, xmlport or query from an action is "
                + "tracked by issue #2943");

        if (target.ObjectId <= 0)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage action {actionId} on page {_pageId}",
                $"not-yet-implemented — the action declares RunObject = {Describe(target)}, but "
                + "that name does not resolve to a page this run knows about. It is either a "
                + "report / codeunit / xmlport / query (which the symbol file states by name "
                + "only, with no object type) or a page that is not loaded, and the runner will "
                + "not guess which; tracked by issue #2943");

        RunTargetPage(actionId, target);
        return true;
    }

    private static string Describe(ActionRunTarget target)
        => target.ObjectName is { Length: > 0 } name
            ? (target.ObjectId > 0 ? $"'{name}' ({target.ObjectId})" : $"'{name}'")
            : target.ObjectId.ToString();

    /// <summary>
    /// Open <paramref name="pageId"/> through BC's own page-run entry points, so handler
    /// lookup, <c>TestPage.Trap()</c> and the "Unhandled UI" refusal are BC's and not a second
    /// implementation of them.
    ///
    /// <para><c>RunPageOnRec</c> is AL's: true hands the target the host page's CURRENT record —
    /// the runner's equivalent of the bookmark BC stamps onto the target's form state — false
    /// opens the page on its own rowset. A <c>RunPageLink</c> is applied on top of either, as
    /// filters on the TARGET's own cursor, which is what BC's <c>CreateForm</c> does with the
    /// two in that order (see this file's header).</para>
    /// </summary>
    private void RunTargetPage(int actionId, ActionRunTarget target)
    {
        var pageId = target.ObjectId;
        var runPageOnRec = target.RunPageOnRec;

        var record = target.Links.Count > 0
            ? BuildLinkedTargetRecord(actionId, target)
            : (runPageOnRec ? _record : null);

        if (TargetPageOpensModally(pageId))
        {
            // isInLookupTrigger / isLookup both false — the shape the AL compiler emits for a
            // plain `Page.RunModal(id, Rec)`, and the one RunnerModalDispatch already serves.
            //
            // Deliberately NOT given the unattended-open treatment below. BC parts here too —
            // NavTestExecution.ShowDialog asks FindHandler with throwIfNotFound: FALSE and then
            // raises NavTestPageInvokedWithoutHandlerException, which IS a NavTestBaseException
            // and therefore IS rethrown into AL by NavOpenTaskPageAction.ShowForm's first catch
            // arm. So a dialog target with nothing bound raises in real BC, where a non-modal
            // one does not. No service tier has measured that arm — corpus codeunit 60285's
            // targets are both PageType = Card — so the modal route keeps refusing loudly
            // rather than being changed on a reading. Tracked by issue #3223.
            if (record != null)
                NavForm.RunModalAsync(false, false, pageId, record).AsTask().GetAwaiter().GetResult();
            else
                NavForm.RunModalAsync(false, false, pageId).AsTask().GetAwaiter().GetResult();
            return;
        }

        // BC's own static NavForm.RunAsync(formId, record, fieldNo), spelled out so the form
        // instance is in reach before anything runs — the unattended open below has to register
        // and open THIS instance, and NavForm's constructor is where a RunPageOnRec record
        // becomes the target's rowset (`if (record != null) { SetSourceTable(record, clone:
        // true); if (bookmarkType != BookmarkType.Record) SyncTempTableWithSourceTableAsync(...) }`).
        var session = NavCurrentThread.Session;
        using var handle = new NavFormHandle(
            session,
            NavGlobal.NCLMetadata.GetMetaFormById(pageId, requireCompiled: true).CreateObjectInstance(record));
        var form = handle.Target;

        // Ask BC, before running anything, whether this page is answered at all — the same
        // question TestHandleForm asks and in the same order (a TestPage.Trap() short-circuits
        // the handler lookup there, so it must short-circuit here too, or the probe would fire
        // FindHandler's RemoveHandlerName side effect that BC would not have fired).
        if (HasTrapForPage(session, form) || FindPageHandler(session, form) != null)
        {
            form.RunAsync(record, 0).AsTask().GetAwaiter().GetResult();
            return;
        }

        OpenTargetUnattended(session, form);
    }

    /// <summary>
    /// Open <paramref name="form"/> with nobody bound to answer it, then force-close it — the
    /// shape Microsoft's own <c>NavOpenTaskPageAction.ShowForm</c> is left in when its
    /// <c>catch (NavBaseException)</c> arm fires (see this file's header). Registering first is
    /// what BC's client layer does before it looks a handler up, and <c>NavForm.OpenForm</c> is
    /// the only thing that raises <c>OnOpenPage</c>.
    ///
    /// <para><c>ForceClose</c>, not <c>CloseForm</c>, and deliberately: Microsoft closes with
    /// <c>FormClose.ForceClose</c> on this path, and <c>NavForm.ForceClose</c> unregisters
    /// without raising <c>OnClosePage</c> / <c>OnQueryClosePage</c>. Nothing has measured
    /// whether real BC raises those here, so this raises neither rather than inventing one.</para>
    ///
    /// <para>An <c>Error()</c> raised by the target's own OnOpenPage is NOT absorbed — it is AL,
    /// and a real test failure.</para>
    ///
    /// <para>Reached by ASKING first rather than by catching BC's refusal, and that ordering is
    /// load-bearing rather than stylistic. Letting <c>NavForm.RunAsync</c> raise and then opening
    /// the page in a catch does produce the same OnOpenPage — measured — but the writes that
    /// trigger makes are then made AFTER an error, and the runner's error handling rolls the
    /// database back to the last commit point (<c>SessionTransactionExtensions.Rollback</c> ->
    /// <c>RecordPatches.RollbackToCommitPoint</c>). Measured on the runner-extras arm, which has
    /// no <c>Commit()</c>: the target's OnOpenPage ran and its row was gone. Real BC never raises
    /// on this route at all, so neither does this.</para>
    /// </summary>
    private static void OpenTargetUnattended(NavSession session, NavForm form)
    {
        session.Company.RegisterForm(form);
        try
        {
            form.OpenForm();
        }
        finally
        {
            if (form.IsOpen) form.ForceClose();
            else session.Company.UnregisterForm(form);
        }
    }

    /// <summary>
    /// Whether the test has an outstanding <c>TestPage.Trap()</c> for this form's page, asked
    /// through BC's own <c>NavTestExecution.HasTrap</c> — the same question, on the same state,
    /// that <see cref="RunnerModalDispatch"/> asks on the dispatch side.
    /// </summary>
    private static bool HasTrapForPage(NavSession session, NavForm form)
    {
        var hasTrap = session.TestExecution.GetType().GetMethod(
            "HasTrap",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: new[] { typeof(int) }, modifiers: null)
            ?? throw new InvalidOperationException(
                "NavTestExecution.HasTrap(int) not found — Ncl shape changed; do not commit");

        return hasTrap.Invoke(session.TestExecution, new object?[] { form.ObjectId.ObjectNumber }) is true;
    }

    /// <summary>
    /// The <c>[PageHandler]</c> BC would dispatch this form to, or null when the test bound
    /// none — BC's OWN <c>NavTestExecution.FindHandler</c>, called with
    /// <c>throwIfNotFound: false</c> so asking does not raise. Nothing about handler matching is
    /// reimplemented: the <c>[HandlerFunctions]</c> split, the handler-type check and the
    /// <c>[NavObjectId]</c> match against THIS page all stay Microsoft's.
    ///
    /// <para>Asking is idempotent. The only state <c>FindHandler</c> mutates on a hit is
    /// <c>executingHandlers</c>, through <c>RemoveHandlerName</c>, which removes the first
    /// occurrence of the name and does nothing when the name is already gone — so the lookup
    /// TestHandleForm makes moments later leaves the run in exactly the state one lookup
    /// would have.</para>
    /// </summary>
    private static MethodInfo? FindPageHandler(NavSession session, NavForm form)
    {
        var testExecution = session.TestExecution;
        var findHandler = testExecution.GetType().GetMethod(
            "FindHandler",
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(NavHandlerType), typeof(NavApplicationObjectBase), typeof(bool), typeof(string) },
            modifiers: null)
            ?? throw new InvalidOperationException(
                "NavTestExecution.FindHandler(NavHandlerType, NavApplicationObjectBase, bool, string) "
                + "not found — Ncl shape changed; do not commit");

        try
        {
            return findHandler.Invoke(
                testExecution,
                new object?[] { NavHandlerType.Page, form, false, null }) as MethodInfo;
        }
        catch (TargetInvocationException ex)
        {
            throw ex.GetBaseException();
        }
    }

    /// <summary>
    /// The TARGET page's own record cursor with the action's <c>RunPageLink</c> applied to it,
    /// which is what makes the target open on the linked rowset instead of its whole table.
    ///
    /// <para>A fresh cursor, never the host's own: the host's <c>_record</c> is the live cursor
    /// the TestPage under test is sitting on, and calling <c>SetRange</c> on it would move the
    /// host's rowset out from under the test — the same reason
    /// <c>LiveNavTestPart</c> keeps the part's cursor and the parent's separate.</para>
    ///
    /// <para>With <c>RunPageOnRec = true</c> the host's current POSITION is carried onto that
    /// fresh cursor before the filters go on, mirroring the order BC's
    /// <c>NavOpenTaskPageAction.CreateForm</c> uses (<c>SetFilterContext</c>, then
    /// <c>SetBookmark</c> from the parent binding manager's current row). That combination is
    /// only expressible when the two pages share a source table — <c>RunPageOnRec</c> hands the
    /// target the host's record — so a target on a different table is refused by name rather
    /// than opened on a position that means nothing there.</para>
    /// </summary>
    private NavRecord BuildLinkedTargetRecord(int actionId, ActionRunTarget target)
    {
        var pageId = target.ObjectId;
        var tableId = RecordPatches.ResolveSourceTableIdForAnyPage(pageId);
        if (tableId == 0)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage action {actionId} on page {_pageId}",
                $"not-yet-implemented — the action declares RunObject = Page {Describe(target)} "
                + "with a RunPageLink, but the target page has no SourceTable this run can "
                + "resolve, so there is no rowset for the link to filter. Opening it without the "
                + "link would show a different rowset than real BC");

        var isTemporary = RecordPatches.ResolveSourceTableTemporaryForAnyPage(pageId);
        var record = TestPageFactory.TryBuildBlankRecord(_owner, tableId, isTemporary, out var why);
        if (record == null)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage action {actionId} on page {_pageId}",
                $"not-yet-implemented — the action declares RunObject = Page {Describe(target)} "
                + $"with a RunPageLink, and the runner could not build a cursor for its "
                + $"SourceTable {tableId} to apply the link to ({why})");

        if (target.RunPageOnRec)
        {
            if (_record == null || _record.TableID != tableId)
                throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                    $"TestPage action {actionId} on page {_pageId}",
                    $"not-yet-implemented — the action declares RunObject = Page {Describe(target)} "
                    + "with both RunPageOnRec and a RunPageLink, but the target's SourceTable "
                    + $"({tableId}) is not the host's ({_record?.TableID.ToString() ?? "none"}), "
                    + "so there is no host row to open the target on");

            // useCaptions: false for the same reason LiveNavTestPage.GetBookmark uses it — the
            // captioned encoding round-trips through field captions, which is a display concern
            // and not the identity of a row.
            var position = _record.ALGetPosition(useCaptions: false);
            if (!string.IsNullOrEmpty(position)) record.ALSetPosition(position);
        }

        foreach (var link in target.Links)
            ApplyActionRunLink(actionId, target, record, link);

        return record;
    }

    /// <summary>
    /// Apply one resolved <c>RunPageLink</c> entry to the target's cursor. The three kinds are
    /// AL's three, applied exactly as <c>LiveNavTestPart.ApplyLink</c> applies a part's
    /// <c>SubPageLink</c> — a FIELD entry to the host's current value, a CONST entry to its
    /// literal through the shared quoting rule, a FILTER entry to its expression in BC's own
    /// filter grammar.
    ///
    /// <para>Every unresolvable case throws rather than skipping the entry. A skipped link is
    /// not "a bit less filtering": it opens the target on its WHOLE table, which is the silent
    /// wrong answer <c>loud-failures.md</c> exists to prevent.</para>
    /// </summary>
    private void ApplyActionRunLink(int actionId, ActionRunTarget target, NavRecord record, ActionRunLink link)
    {
        if (link.TargetFieldNo <= 0)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage action {actionId} on page {_pageId}",
                $"not-yet-implemented — the action declares RunObject = Page {Describe(target)} "
                + "with a RunPageLink naming a field of the target's table that this run cannot "
                + "resolve to a field number, so the link cannot be applied and the target would "
                + "otherwise open on its whole table");

        switch (link.Kind)
        {
            case MetaTypes.FilterType.FIELD:
                if (_record == null || link.HostFieldNo <= 0)
                    throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                        $"TestPage action {actionId} on page {_pageId}",
                        $"not-yet-implemented — the action declares RunObject = Page {Describe(target)} "
                        + $"with RunPageLink field {link.TargetFieldNo} = field(...), which reads a "
                        + "field of the HOST page's row, and this run has "
                        + (_record == null
                            ? "no host record to read it from"
                            : $"no field number for it (got {link.HostFieldNo})"));
                record.ALSetRange(link.TargetFieldNo, _record.GetFieldValue(link.HostFieldNo));
                break;

            case MetaTypes.FilterType.CONST:
                record.ALSetFilter(link.TargetFieldNo,
                    LiveNavTestPart.ConstFilterExpression(record, link.TargetFieldNo, link.Value));
                break;

            case MetaTypes.FilterType.FILTER:
                // Already in BC's filter grammar — the compiler wrote option members as ordinals,
                // and the symbols route re-quoted AL identifiers through FilterValueText. BC's own
                // parser reads it, and a malformed expression raises BC's own
                // NavInvalidFilterExpressionException naming the text.
                record.ALSetFilter(link.TargetFieldNo, link.Value);
                break;

            default:
                throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                    $"TestPage action {actionId} on page {_pageId}",
                    $"not-yet-implemented — the action declares RunObject = Page {Describe(target)} "
                    + $"with a RunPageLink entry of kind {link.Kind}, which is not one of AL's "
                    + "three link kinds (field / const / filter)");
        }
    }

    /// <summary>
    /// Whether opening <paramref name="pageId"/> from an action shows it as a DIALOG — the
    /// distinction that decides whether a [ModalPageHandler] or a [PageHandler] answers it.
    /// Measured on BC 28.1's own UI builder: <c>FormState.RunModal</c> is assigned in exactly
    /// two builders, <c>NavigatePageBuilder</c> and <c>StandardDialogBuilder</c>, and defaults
    /// to false everywhere else. See this file's header.
    /// </summary>
    private static bool TargetPageOpensModally(int pageId)
    {
        var pageType = RecordPatches.TryGetAnyPageType(pageId);
        return string.Equals(pageType, "StandardDialog", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pageType, "NavigatePage", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The RunObject <paramref name="actionId"/> declares, or null when it declares none.
    ///
    /// <para>Two sources, in this order. BC's own compiled <c>ActionDefinition</c> first: for a
    /// page the runner compiled itself the AL compiler has already resolved the target to a
    /// numeric id and an object KIND, so nothing is re-derived or guessed here. Then, for a page
    /// that ships precompiled in a dependency .app — where the runner has no compiled action
    /// metadata (that is issue #2460) — the .app's SymbolReference.json, which states
    /// <c>RunObject</c> as a bare NAME with no kind alongside it and therefore needs the
    /// resolution step <see cref="ResolveRunTargetFromSymbols"/> documents.</para>
    ///
    /// <para>An <c>actionref</c> carries no properties of its own, exactly as it carries no
    /// trigger (#2113), so every candidate id its target could hash to is tried too — the same
    /// walk <see cref="FindTriggerThroughActionRef"/> does for the trigger half.</para>
    /// </summary>
    private ActionRunTarget? ResolveActionRunTarget(int actionId)
    {
        foreach (var candidate in ActionIdsForRunObject(actionId))
        {
            if (ResolveRunTargetFromMetadata(candidate) is { } fromMetadata) return fromMetadata;
            if (ResolveRunTargetFromSymbols(candidate) is { } fromSymbols) return fromSymbols;
        }
        return null;
    }

    /// <summary>
    /// The action's own id, then — when it is an <c>actionref</c> — every id its target could
    /// hash to, following a ref chain with a visited set so a malformed cycle terminates.
    /// Mirrors <see cref="FindTriggerThroughActionRef"/>; an actionref and its target need not
    /// be declared by the same object, so the id has to be re-derived per candidate.
    /// </summary>
    private IEnumerable<int> ActionIdsForRunObject(int actionId)
    {
        yield return actionId;

        var visited = new HashSet<(int, int)>();
        var current = actionId;
        var resolved = TryResolveActionRef(current);
        while (resolved is { } step && visited.Add((step.DeclaringObjectId, current)))
        {
            foreach (var objectId in CandidateDeclaringObjectIds(step.DeclaringObjectId))
                yield return MemberId(objectId, step.TargetName);

            current = MemberId(step.DeclaringObjectId, step.TargetName);
            resolved = TryResolveActionRef(current);
        }
    }

    /// <summary>
    /// The RunObject BC's own compiled page metadata states for this action, or null when the
    /// metadata has no entry for the id or the entry declares no RunObject.
    /// </summary>
    private ActionRunTarget? ResolveRunTargetFromMetadata(int actionId)
    {
        if (_form is not NavForm form) return null;
        if (!form.MetadataHelper.TryGetCommonActionDefinitionById(actionId, out var common)) return null;
        if (common is not MetaTypes.ActionDefinition action) return null;
        if (!action.RunObjectTypeSpecified || action.RunObjectType == MetaTypes.RunObjectType.None)
            return null;

        return new ActionRunTarget(
            action.RunObjectType,
            action.TargetID,
            RecordPatches.TryGetAnyPageName(action.TargetID),
            action.RunPageOnRec,
            LinksFromMetadata(action));
    }

    /// <summary>
    /// The action's <c>RunPageLink</c> as BC's own compiled metadata already carries it: a
    /// <c>FilterDefinition</c> per entry, with the target's field NUMBER in <c>FieldID</c>, the
    /// kind in <c>FilterType</c>, and in <c>FilterValue</c> either the HOST's field number (for
    /// <c>FIELD</c>) or the compiled literal / expression. Nothing is re-derived from AL text on
    /// this route — the compiler resolved all of it.
    ///
    /// <para>A <c>FIELD</c> entry whose <c>FilterValue</c> is not a number is passed through with
    /// <c>HostFieldNo</c> 0, which <see cref="ApplyActionRunLink"/> refuses by name. Same
    /// convention as <c>MockTestPage.SubPageLinks</c>, and for the same reason: an entry that
    /// cannot be applied must be loud, not dropped.</para>
    /// </summary>
    private static IReadOnlyList<ActionRunLink> LinksFromMetadata(MetaTypes.ActionDefinition action)
    {
        var filters = action.RunFormLink?.TableFilters;
        if (filters is not { Count: > 0 }) return NoLinks;

        var links = new List<ActionRunLink>(filters.Count);
        foreach (var f in filters)
        {
            var hostFieldNo = 0;
            if (f.FilterType == MetaTypes.FilterType.FIELD)
                int.TryParse(f.FilterValue, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out hostFieldNo);
            links.Add(new ActionRunLink(f.FieldID, f.FilterType, hostFieldNo, f.FilterValue ?? string.Empty));
        }
        return links;
    }

    /// <summary>
    /// The RunObject a PRECOMPILED dependency page states for this action, read from its
    /// SymbolReference.json.
    ///
    /// <para>The symbol file states <c>RunObject</c> as a bare object NAME — measured across
    /// Base Application 28.1's 5,455 action RunObject properties, not one carries an object
    /// type — so the kind cannot be read off it. It is inferred from the page inventory: a name
    /// that resolves to a page IS a page. That is the same by-name resolution
    /// <c>CardPageId</c> / <c>LookupPageId</c> / <c>DrillDownPageId</c> already use, and an
    /// unresolvable name is reported loudly by the caller rather than answered with a
    /// default.</para>
    /// </summary>
    private ActionRunTarget? ResolveRunTargetFromSymbols(int actionId)
    {
        var declared = RecordPatches.TryGetActionRunObject(_pageId, actionId, isExtension: false);
        if (declared == null)
            foreach (var extensionId in RecordPatches.GetPageExtensionIdsForPage(_pageId))
            {
                declared = RecordPatches.TryGetActionRunObject(extensionId, actionId, isExtension: true);
                if (declared != null) break;
            }
        if (declared is not { } spec) return null;

        var (pageId, otherKinds) = RecordPatches.ResolveObjectNameAsPage(spec.ObjectName);

        // Ambiguous: the same name also names a report / codeunit / xmlport / query. Measured
        // on Base Application 28.1, 73 names are shared that way, so this is a routine case and
        // not a corner one — "Chart of Accounts" is both a page and a report. Opening the page
        // for an action whose AL said `RunObject = Report "Chart of Accounts"` would be a
        // silent wrong answer, so ObjectId 0 sends it to the caller's loud refusal.
        if (pageId > 0 && otherKinds.Count > 0)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage action {actionId} on page {_pageId}",
                $"not-yet-implemented — the action declares RunObject = '{spec.ObjectName}', and this "
                + "page ships precompiled, so its symbol file states the target by NAME with no "
                + $"object type. That name is also a {string.Join("/", otherKinds)} in this run, so "
                + "the runner cannot tell which object the AL named and will not guess; tracked "
                + "by issue #2943");

        return new ActionRunTarget(
            // A name this run's page inventory answers, and nothing else answers, IS a page. A
            // name it does not answer at all is reported by ObjectId 0, and the caller refuses
            // it by name rather than guessing which kind the symbol file meant.
            MetaTypes.RunObjectType.Page,
            pageId,
            spec.ObjectName,
            spec.RunPageOnRec,
            LinksFromSymbols(actionId, spec, pageId));
    }

    /// <summary>
    /// The action's <c>RunPageLink</c> for a PRECOMPILED page, resolved from the AL property
    /// text SymbolReference.json states to the field NUMBERS applying it needs.
    ///
    /// <para>The left-hand side of every entry names a field of the TARGET's source table, and a
    /// <c>field(...)</c> right-hand side names one of the HOST's — the same two-table resolution
    /// <c>DependencyPageMetadataXml.EmitSubFormLinkXml</c> already does for a part's
    /// <c>SubPageLink</c>, through the same
    /// <c>RecordPatches.TryResolveDependencyFieldId</c>. <c>const(...)</c> and
    /// <c>filter(...)</c> values go through the same two normalisers as well, so a link means
    /// the same thing whichever route recovered it.</para>
    ///
    /// <para>Refuses rather than returning a partial link, in both directions: a target page
    /// with no resolvable source table has no rowset to filter, and an entry the parser dropped
    /// would leave the target showing MORE rows than BC does.</para>
    /// </summary>
    private IReadOnlyList<ActionRunLink> LinksFromSymbols(
        int actionId, BcAppSymbolCache.ActionRunObjectSymbol spec, int targetPageId)
    {
        if (!spec.HasRunPageLink) return NoLinks;

        var parsed = spec.RunPageLink;
        if (parsed == null || parsed.Count != spec.DeclaredRunPageLinkEntries)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage action {actionId} on page {_pageId}",
                $"not-yet-implemented — the action declares RunObject = '{spec.ObjectName}' with a "
                + $"RunPageLink of {spec.DeclaredRunPageLinkEntries} entr(ies), and this run "
                + $"could read {parsed?.Count ?? 0} of them out of the symbol file. Applying the "
                + "rest alone would show MORE rows than real BC, so it is refused instead");

        var targetTableId = RecordPatches.ResolveSourceTableIdForAnyPage(targetPageId);
        var hostTableId = RecordPatches.ResolveSourceTableIdForAnyPage(_pageId);

        var links = new List<ActionRunLink>(parsed.Count);
        foreach (var entry in parsed)
        {
            var targetFieldNo = RecordPatches.TryResolveDependencyFieldId(targetTableId, entry.PartFieldName) ?? 0;

            if (string.Equals(entry.Kind, "field", StringComparison.OrdinalIgnoreCase))
            {
                var hostFieldName = entry.Value.Trim().Trim('"');
                var hostFieldNo = RecordPatches.TryResolveDependencyFieldId(hostTableId, hostFieldName) ?? 0;
                links.Add(new ActionRunLink(targetFieldNo, MetaTypes.FilterType.FIELD, hostFieldNo, hostFieldName));
            }
            else if (string.Equals(entry.Kind, "const", StringComparison.OrdinalIgnoreCase))
            {
                links.Add(new ActionRunLink(targetFieldNo, MetaTypes.FilterType.CONST, 0,
                    RecordPatches.NormalizeConstLinkValue(entry.Value)));
            }
            else
            {
                links.Add(new ActionRunLink(targetFieldNo, MetaTypes.FilterType.FILTER, 0,
                    RecordPatches.FilterValueText(entry.Value)));
            }
        }
        return links;
    }
}
