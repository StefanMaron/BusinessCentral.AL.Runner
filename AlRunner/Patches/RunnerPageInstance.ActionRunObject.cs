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
// WHAT IS STILL REFUSED, LOUDLY
//   RunObject targeting a Report / Codeunit / XmlPort / Query, and `RunPageLink` (a page target
//   whose rowset the platform filters from the host's fields). Both raise with a
//   `not-yet-implemented` reason anchor, which `docs/expectations.md` lets a manifest track as
//   `expect-fail-known-gap` against an OPEN issue — the classification the old `testpage-action`
//   anchor made impossible. Answering either by opening the page unfiltered would be a silent
//   wrong answer, which is what `loud-failures.md` exists to prevent.
using Microsoft.Dynamics.Nav.Runtime;
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
        bool HasRunPageLink);

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
                + "tracked separately; see issue #2931");

        if (target.HasRunPageLink)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage action {actionId} on page {_pageId}",
                $"not-yet-implemented — the action declares RunObject = Page {Describe(target)} "
                + "together with RunPageLink, and the runner does not yet apply an action's "
                + "RunPageLink filters. Opening the page WITHOUT them would show a different "
                + "rowset than real BC, so it is refused instead; see issue #2931");

        if (target.ObjectId <= 0)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage action {actionId} on page {_pageId}",
                $"not-yet-implemented — the action declares RunObject = {Describe(target)}, but "
                + "that name does not resolve to a page this run knows about. It is either a "
                + "report / codeunit / xmlport / query (which the symbol file states by name "
                + "only, with no object type) or a page that is not loaded, and the runner will "
                + "not guess which; see issue #2931");

        RunTargetPage(target.ObjectId, target.RunPageOnRec);
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
    /// <para><paramref name="runPageOnRec"/> is AL's <c>RunPageOnRec</c>: true hands the target
    /// the host page's CURRENT record — the runner's equivalent of the bookmark BC stamps onto
    /// the target's form state — false opens the page on its own rowset.</para>
    /// </summary>
    private void RunTargetPage(int pageId, bool runPageOnRec)
    {
        var record = runPageOnRec ? _record : null;

        if (TargetPageOpensModally(pageId))
        {
            // isInLookupTrigger / isLookup both false — the shape the AL compiler emits for a
            // plain `Page.RunModal(id, Rec)`, and the one RunnerModalDispatch already serves.
            if (record != null)
                NavForm.RunModalAsync(false, false, pageId, record).AsTask().GetAwaiter().GetResult();
            else
                NavForm.RunModalAsync(false, false, pageId).AsTask().GetAwaiter().GetResult();
            return;
        }

        if (record != null)
            NavForm.RunAsync(pageId, record).AsTask().GetAwaiter().GetResult();
        else
            NavForm.RunAsync(pageId).AsTask().GetAwaiter().GetResult();
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
            action.RunFormLink?.TableFilters is { Count: > 0 });
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

        var pageId = RecordPatches.TryResolvePageIdByName(spec.ObjectName);
        return new ActionRunTarget(
            // A name this run's page inventory answers IS a page. A name it does not answer is
            // reported by ObjectId 0, and the caller refuses it by name rather than guessing
            // which of report / codeunit / xmlport / query the symbol file meant.
            MetaTypes.RunObjectType.Page,
            pageId,
            spec.ObjectName,
            spec.RunPageOnRec,
            spec.HasRunPageLink);
    }
}
