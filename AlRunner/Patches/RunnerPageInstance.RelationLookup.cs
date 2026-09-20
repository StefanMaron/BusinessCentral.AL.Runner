// RunnerPageInstance.RelationLookup — the THIRD place AL puts a lookup (issue #3518).
//
// A page control can declare `trigger OnLookup(var Text: Text): Boolean`; a source table field
// can declare the unrelated parameterless `trigger OnLookup()`; and a field can declare neither
// and instead carry a TableRelation, which is what the overwhelming majority of Base
// Application fields do. #2549 implemented the first two and deliberately left the third
// refusing, because standing up the related table's list page looked like it needed a client.
//
// It does not, and almost none of this file is new logic. Both halves ship in Ncl.dll:
//
//   Microsoft.Dynamics.Nav.Runtime.Autofill.AutofillHelper
//       .GetRelationForField  — which table the relation points at, and which field of it
//       .ApplyRelationFilters — the relation's own const()/field()/filter() arms, as filters
//   NavForm.RunModalAsync(isInLookupTrigger, isLookup, formId, record, fieldNo)
//       — LookupMode, the modal dispatch, and the conditional write-back
//
// What the runner adds is the join: build a record of the related table, let Microsoft's
// helper filter it, run Microsoft's lookup-mode RunModal on it, and copy the picked key into
// the host field. Every decision that could differ from BC — which arm applies, which filters,
// whether OK writes back — stays in Microsoft's code.
//
// See docs/testpage-lookup.md#tablerelation-lookups for how the three routes relate, what each
// refusal means, and the measurement behind the change.
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

internal sealed partial class RunnerPageInstance
{
    private const string AutofillSurface = "TestPage lookup from a TableRelation";

    /// <summary>
    /// BC's <c>Microsoft.Dynamics.Nav.Runtime.Autofill.AutofillHelper</c>, resolved off the
    /// assembly <c>NavRecord</c> lives in so no assembly name is hardcoded.
    ///
    /// <para>Refuses rather than answering null: this route cannot resolve a TableRelation at
    /// all without the helper, and a null here would surface as a NullReferenceException
    /// naming no member (guards-need-a-third-state.md — a lookup that could not be performed
    /// must not be reported as a lookup that found nothing).</para>
    /// </summary>
    private static Type AutofillHelperType()
        => typeof(NavRecord).Assembly.GetType(
               "Microsoft.Dynamics.Nav.Runtime.Autofill.AutofillHelper", throwOnError: false)
           ?? throw new BcShapeGapException(
               AutofillSurface, "Microsoft.Dynamics.Nav.Runtime.Autofill.AutofillHelper",
               "type not found on this BC build, so the runner cannot ask BC which table a "
               + "field's TableRelation points at or which rows that relation allows");

    /// <summary>
    /// Serve a lookup whose only source is the field's <c>TableRelation</c>: resolve the
    /// related table, open its lookup page modally through BC's own machinery, and copy the
    /// picked row's key back into the host field.
    ///
    /// <para>Returns null on every route, including the served one — the same contract the
    /// table-field <c>OnLookup</c> fallback uses. The selection is written into the host RECORD
    /// rather than handed back as a text, so the caller reads the field's value off the record
    /// where it has already been put.</para>
    ///
    /// <para>ONE shape still throws <see cref="RunnerOutOfScopeException"/>, and it is not the
    /// one this method was first written with — see the two comments inside, which record what
    /// a service tier measured about each.</para>
    /// </summary>
    private NavText? RaiseTableRelationLookup(int controlId, NavRecord sourceRecord, int sourceFieldNo)
    {
        var relation = ResolveRelation(controlId, sourceRecord, sourceFieldNo);

        // Shape 1: NO RELATION AT ALL — and BC does NOTHING here, so neither does the runner.
        //
        // This arm refused when it was first written, and that was wrong. Corpus codeunit 60569
        // "TRL Tests" put the claim in front of a real service tier and all EIGHT cloud legs
        // answered "An error was expected inside an ASSERTERROR statement" — 27.0, 27.3, 27.5,
        // 28.0, 28.1, 28.2, 28.3 and 28.4, run 35445556865. BC raises nothing and opens nothing
        // for a field with neither trigger nor relation; the field keeps its value.
        //
        // So returning null IS the faithful answer, not a silent default: there is no BC
        // behaviour being skipped, which is the test loud-failures.md sets. The
        // Lookup_NoTriggerAndNoTableRelation_DoesNothing arm of that codeunit pins it, and the
        // runner-side twin is in tests/runner-extras/testpage-lookup-tablerelation-served.
        if (relation == null) return null;

        var (relatedTable, relatedFieldId, metaFieldRelation) = relation.Value;
        var lookupPageId = LookupPageIdFor(relatedTable);

        // Shape 2: the relation RESOLVES and its target table declares neither LookupPageId nor
        // DrillDownPageId. This one still refuses, and the distinction from shape 1 is the
        // point: there a lookup has nothing to resolve and BC's answer is documented silence;
        // here the AL genuinely names a related table and BC's client has a page-picking rule
        // for it that NO corpus test has measured. Guessing "silence" for both would make the
        // runner's answer independent of a difference BC's client does act on.
        //
        // MEASURED, and this refusal's stated premise is now known to be FALSE: real BC opens
        // a modal page here. Corpus codeunit 60569 (fixture "TRL Pageless" 60570, corpus PR
        // 391) answered "Unhandled UI: ModalPage" on all eight cloud legs, run 35493508143 —
        // raised from inside NavTestExecution.ShowLookupForm, which registers the form before
        // it looks for a handler, so BC had already chosen a page.
        //
        // It is NOT yet replaced, and the follow-up measurement sharpened why: BC decides to
        // open a page and then FAILS TO PRODUCE ONE. A handler-bound probe (corpus 391 head
        // af4b986b) NREs inside ShowLookupForm, which only happens when GetRegisteredForm
        // answers null — FindHandler's page-id check sits inside `if (appObject != null)`, so
        // a null form skips it and .ObjectId then dereferences null. No [ModalPageHandler]
        // probe can name the page for that reason.
        //
        // So silence would be a second unmeasured inference, and one the measurement
        // contradicts: silence is what BC does for shape 1, not for this shape. What is not
        // established is which page BC intended; #4403 tracks it.
        // See docs/testpage-lookup.md#why-the-refusal-has-not-yet-been-replaced.
        if (lookupPageId <= 0)
            throw new RunnerOutOfScopeException(
                $"TestPage lookup on control {controlId} (page {_pageId})",
                $"testpage-lookup — the lookup on field {sourceFieldNo} comes from its "
                + $"TableRelation to table {relatedTable.TableId}, and that table declares no "
                + "LookupPageId or DrillDownPageId, so there is no page to open. "
                + "See docs/scope.md");

        RunRelationLookupPage(
            lookupPageId, sourceRecord, sourceFieldNo, relatedTable, relatedFieldId, metaFieldRelation);
        return null;
    }

    /// <summary>
    /// Which table the field's <c>TableRelation</c> points at, and which of its fields the
    /// lookup selects — asked through BC's own
    /// <c>Autofill.AutofillHelper.GetRelationForField</c>, which is the method BC's client calls
    /// to answer exactly this question for exactly this purpose.
    ///
    /// <para>Not re-derived from the runner's parsed metadata, and not open-coded from
    /// <c>EvaluateRelation</c> either. That helper does three things this would otherwise have
    /// to repeat and could get subtly wrong: it picks the applicable arm through
    /// <c>EvaluateRelation</c> (so a conditional <c>where()</c> decides), it reads the related
    /// table off the arm's own <c>SourceField.Parent</c> rather than by id lookup, and it
    /// defaults <c>relatedFieldId</c> to the related table's FIRST PRIMARY KEY FIELD when the
    /// arm names none — the ordinary <c>TableRelation = "Customer"</c> shape.</para>
    ///
    /// <para><c>EvaluateRelation</c> underneath is also the seam the runner already guards for
    /// an unresolved relation (<c>RecordPatches.RecordImpl_UnresolvedRelationGuardForEvaluate</c>,
    /// #3306), so a TableRelation naming something the runner could not resolve raises there
    /// rather than reaching this file as a silent "no relation".</para>
    ///
    /// <para>Returns null when the helper answers false — no arm applies — which the caller
    /// refuses by name.</para>
    /// </summary>
    private (NCLMetaTable Table, int FieldId, object? MetaFieldRelation)? ResolveRelation(
        int controlId, NavRecord sourceRecord, int sourceFieldNo)
    {
        var getRelation = BcShape.RequiredMethod(
            AutofillHelperType(), "GetRelationForField",
            BindingFlags.Public | BindingFlags.Static,
            AutofillSurface, "Autofill.AutofillHelper.GetRelationForField",
            "the runner cannot resolve which table a field's TableRelation points at without it");

        if (!sourceRecord.MetaTable.TryGetFieldByNo(sourceFieldNo, out var metaField))
            throw new BcShapeGapException(
                $"TestPage lookup on control {controlId} (page {_pageId})",
                "NCLMetaTable.TryGetFieldByNo",
                $"field {sourceFieldNo} is not on the source table's metatable, so the runner "
                + "cannot ask BC which table that field's TableRelation points at");

        // control: null — this route is reached only when the control declares no OnLookup, and
        // the runner has no ControlDefinition to hand over for a page it compiled itself. The
        // helper's control arm is its fallback for a control-level TableRelation; the field arm
        // is the one this issue is about, and it is tried first regardless.
        var args = new object?[] { null, sourceRecord, metaField, null, null, null, null };

        bool found;
        try
        {
            found = getRelation.Invoke(null, args) is true;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }

        if (!found) return null;

        // args[5] / args[6] are the helper's `out NCLMetaTable relatedTable` and
        // `out int relatedFieldId`; args[4] is `out NCLMetaFieldRelation tableRelation`, which
        // ApplyRelationFilters needs to know it is filtering a FIELD relation rather than a
        // control one.
        if (args[5] is not NCLMetaTable relatedTable || args[6] is not int relatedFieldId)
            throw new BcShapeGapException(
                $"TestPage lookup on control {controlId} (page {_pageId})",
                "Autofill.AutofillHelper.GetRelationForField out-parameters",
                "BC reported that field " + sourceFieldNo + " has an applicable TableRelation "
                + "arm but did not hand back a related table and field, so the runner cannot "
                + "tell which table's lookup page to open");

        return (relatedTable, relatedFieldId, args[4]);
    }

    /// <summary>
    /// The page a lookup on <paramref name="relatedTable"/> opens — its <c>LookupPageId</c>,
    /// falling back to its <c>DrillDownPageId</c>.
    ///
    /// <para>That precedence and that fallback are BC's, read off
    /// <c>NavRecord.GetPageToOpen</c>'s own body
    /// (<c>metaTable.LookupFormId &gt; 0 ? metaTable.LookupFormId : metaTable.DrillDownPageId</c>),
    /// which the runner already reproduces in <c>RecordPatches.NavRecord_GetPageToOpen</c> for
    /// the TestField navigate action.</para>
    ///
    /// <para>Deliberately NOT calling that method: its CardFormID follow-through is right for a
    /// "navigate to the related record" action, which wants the card for one row, and wrong for
    /// a lookup, which wants the LIST to pick a row from. Sharing it would open a card where BC
    /// opens a list.</para>
    ///
    /// <para>Returns 0 when the table declares neither, which the caller refuses by name. The
    /// metatable is the one BC's own helper handed back, so there is no id lookup to fail.</para>
    /// </summary>
    private static int LookupPageIdFor(NCLMetaTable relatedTable)
        => relatedTable.LookupFormId > 0 ? relatedTable.LookupFormId : relatedTable.DrillDownPageId;

    /// <summary>
    /// Open the lookup page on a record of the RELATED table, through BC's own static
    /// <c>NavForm.RunModalAsync(isInLookupTrigger, isLookup, formId, record, fieldNo)</c>, and
    /// copy the picked row's selected field into the host field.
    ///
    /// <para>The record handed to <c>RunModalAsync</c> is a fresh instance of the RELATED
    /// table, not the host record. That is what the parameter means on this overload — BC's
    /// body binds the page to it (<c>CreateObjectInstance(record)</c>) and assigns the picked
    /// row into it (<c>record.Assign(form.SourceTable)</c>) — and passing the host record
    /// instead bound the lookup page to the host's own table, so the handler saw the host's
    /// rows. Measured on this bundle before the fix: the modal page's first row read
    /// <c>H1</c>, the host's key, rather than a related-table row.</para>
    ///
    /// <para>BC's body is the whole operation and none of it is reimplemented:</para>
    ///
    /// <code>
    /// form.LookupMode = isLookup;
    /// FormResult formResult = await form.RunModalAsync(record, fieldNo);
    /// if (record != null &amp;&amp; isLookup &amp;&amp; formResult != FormResult.Cancel
    ///     &amp;&amp; (!isInLookupTrigger || formResult == FormResult.LookupOK))
    ///     record.Assign(form.SourceTable);
    /// </code>
    ///
    /// <para>So the modal dispatch to a declared <c>[ModalPageHandler]</c>, the lookup-mode
    /// flag, and the OK/Cancel contract are Microsoft's. The runner's existing Cecil redirect of
    /// the client callback (<see cref="RunnerModalDispatch"/>) is what makes that dispatch work
    /// with no client present, exactly as it already does for <c>Page.RunModal</c>.</para>
    ///
    /// <para><c>isInLookupTrigger: true</c> is load-bearing, and the opposite of what the
    /// parameter's name suggests for a route that has no OnLookup trigger. It is what makes
    /// the second clause of the guard above the one that decides, requiring
    /// <c>FormResult.LookupOK</c> — and that is necessary because a lookup-mode page reports a
    /// cancel as <c>LookupCancel</c> rather than <c>Cancel</c>, so the first clause does not
    /// fire for it. With <c>false</c>, measured on this bundle, a cancelled lookup wrote the
    /// row the handler had moved to.</para>
    ///
    /// <para>Whether the host field is written is decided by BC's <c>FormResult</c>, not by
    /// inspecting the record afterwards — see the comment at the check itself.</para>
    /// </summary>
    private void RunRelationLookupPage(
        int lookupPageId, NavRecord sourceRecord, int sourceFieldNo,
        NCLMetaTable relatedTable, int relatedFieldId, object? metaFieldRelation)
    {
        var relatedRecord = relatedTable.CreateObjectInstance(NavCurrentThread.Session);

        // The relation's own arms, applied by BC's helper: `TableRelation = Item."No." where(
        // Blocked = const(false))` must show the same rows in the lookup as it validates
        // against. Skipping this would open the page unfiltered and let a test select a row
        // real BC never offers.
        ApplyRelationFilters(sourceRecord, relatedRecord, metaFieldRelation);

        // Bracketed like every other trigger this class raises: a lookup page's OnOpenPage, and
        // any AL its handler runs, may call CurrPage.Update on the HOST page, and that refresh
        // belongs to the call that armed it rather than leaking into the next one.
        BeginTrigger();
        var completed = false;
        FormResult result;
        try
        {
            try
            {
                result = NavForm.RunModalAsync(
                    isInLookupTrigger: true,
                    isLookup: true,
                    formId: lookupPageId,
                    record: relatedRecord,
                    fieldNo: relatedFieldId)
                    .AsTask().GetAwaiter().GetResult();
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                // An Error() raised by the lookup page's own AL — its OnOpenPage, or the
                // handler the test declared — is a real test failure and must reach the test
                // wearing its own type, not a reflection wrapper.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw; // unreachable
            }
            completed = true;
        }
        finally { EndTrigger(completed); }

        // LookupOK is the ONLY selecting outcome, and that is MEASURED rather than read off
        // BC's guard. A lookup-mode page reports its outcome with the lookup-specific members
        // of FormResult, not the plain ones: on this bundle an OK().Invoke() in the
        // [ModalPageHandler] returns LookupOK (3) and a Cancel().Invoke() returns
        // **LookupCancel (4)** -- NOT FormResult.Cancel.
        //
        // That matters because BC's own guard inside RunModalAsync reads
        //
        //     formResult != FormResult.Cancel && (!isInLookupTrigger || formResult == FormResult.LookupOK)
        //
        // whose FIRST clause does not fire for LookupCancel. So the write-back is carried
        // entirely by the second clause, which requires isInLookupTrigger TRUE. Measured: with
        // it false, a cancelled lookup wrote the row the handler had moved to
        // (`Expected  but got REL-B`) -- the silent wrong answer this check exists to stop.
        //
        // Reading the related record's state instead would have to guess whether a blank
        // selected field meant "cancelled" or "selected a row whose key is blank", which are
        // different and both real.
        if (result != FormResult.LookupOK) return;

        // ALValidate, not SetFieldValue: selecting a value in a lookup runs the host field's
        // OnValidate on real BC, and that trigger is frequently where the AL under test does
        // its work (`"No." := ...; Validate("No.")` is the shape half of Base Application's
        // document lines use). Writing the value without validating would land the same text
        // and skip everything the selection was supposed to cause.
        //
        // callerRecord: sourceRecord — its own record, which is what BC passes when the
        // validation originates on the record itself rather than from a related one.
        sourceRecord.ALValidate(sourceFieldNo, relatedRecord.GetFieldValue(relatedFieldId), sourceRecord);
    }

    /// <summary>
    /// Apply the relation's own filter arms to the related record, through BC's
    /// <c>Autofill.AutofillHelper.ApplyRelationFilters</c>.
    ///
    /// <para>Microsoft's, because the arms are a small language of their own —
    /// <c>NCLMetaFilterType.Const</c>, <c>Expression</c> and <c>Field</c>, the last resolving a
    /// value out of the HOST record — and a lookup that filtered differently from the
    /// <c>Validate</c> on the same relation would offer rows that then fail validation.</para>
    /// </summary>
    private static void ApplyRelationFilters(
        NavRecord sourceRecord, NavRecord relatedRecord, object? metaFieldRelation)
    {
        var apply = BcShape.RequiredMethod(
            AutofillHelperType(), "ApplyRelationFilters",
            BindingFlags.Public | BindingFlags.Static,
            AutofillSurface, "Autofill.AutofillHelper.ApplyRelationFilters",
            "the runner cannot narrow a lookup page to the rows the relation allows without it");

        try
        {
            // controlRelation: null — see ResolveRelation, this route has no ControlDefinition.
            apply.Invoke(null, new object?[] { sourceRecord, relatedRecord, metaFieldRelation, null });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
    }

}
