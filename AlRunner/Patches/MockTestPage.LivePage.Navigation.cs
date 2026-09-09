// LiveNavTestPage: row navigation — First/Last/Next/Previous, the implicit new-row line,
// and what a row becoming current runs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Data;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner;
internal partial class LiveNavTestPage
{
    // Every cursor move leaves the in-progress new row, so it must be persisted first —
    // otherwise navigating away from a New() silently discards it. Parts flush too: moving
    // the parent re-links every part to a different row, so a row started in a part must be
    // persisted while the link that stamped its key is still the current one.
    //
    // An empty result still lands on the implicit new-row line as a SIDE EFFECT, mirroring
    // MoveNext() past the last data row (see EnterNewRowLine). The RETURN VALUE stays false —
    // corpus CU60743 EmptyEditableList_FirstReturnsFalse pins that an explicit First() call on
    // an empty editable, insert-allowed page must still report false, so this only changes
    // internal cursor state, never what First() answers. What it fixes is issue #2392: BC's own
    // ApprovalCommentsHandler opens such a page and writes a field directly, with no New() or
    // First() of its own — the page-construction sites that position a page at open time (see
    // RunnerTestClientSession.GetPage, RunnerTestPageState.MarkOpened) call this so that write
    // has a row to land on instead of silently targeting nothing (corpus CU60743
    // EmptyEditableList_SetValueWithoutNewOrFirst_InsertsARow, validated against a real service
    // tier on all 8 supported BC versions).
    public override bool MoveFirst()
    {
        var record = RequireRecord("MoveFirst()");
        FlushParts(); FlushRow();

        // Whether the cursor was ALREADY on the draft line, read before LeaveNewRowLine clears
        // it. A First() over a rowset that is still empty does not move anywhere: the draft line
        // was the only row before the call and is the only row after it, so the row it stands
        // for is the same row and must not be started a second time (#3029). Without this, the
        // open-time reload entered the draft line and the test's own First() entered it again,
        // raising the page's OnNewRecord twice before anything was typed.
        var wasOnNewRowLine = _onNewRowLine;

        LeaveNewRowLine();
        var found = _page?.RaiseOnFindRecord("-")
                    ?? record.ALFindFirstAsync(DataError.TrapError).GetAwaiter().GetResult();
        if (!found)
        {
            // Same draft line as before the call: restore the latch LeaveNewRowLine just
            // cleared, so EnterNewRowLine takes its already-started branch. A First() that DID
            // move — from a data row, or onto one — leaves it clear and the next draft line
            // gets its own new-record step, which is what keeps this from becoming
            // "once per page".
            if (wasOnNewRowLine) _newRowLineRecordStarted = true;
            EnterNewRowLine(record);
        }
        return Loaded(found);
    }

    /// <summary>
    /// Go to the last row of the page's rowset.
    ///
    /// The empty case falls onto the implicit new-row line exactly as <see cref="MoveFirst"/>
    /// does, and for the same reason: on an editable, insert-allowed page a client that finds
    /// no matching row still renders one row — the blank line — and a subsequent write has to
    /// have somewhere to land. Without it, `Last()` on such a page left the cursor on nothing
    /// and `TP.SomeField.SetValue('X')` afterwards wrote into a record nothing had positioned
    /// (issue #2964; the same gap #2392 fixed for First() and #2923 for a linked part).
    ///
    /// The RETURN VALUE stays false, so this changes internal cursor state only, never what
    /// Last() answers. That matters for the one internal caller,
    /// FindRowFromTableFieldValues's backward scan, which starts at MoveLast() and enters its
    /// `while (hasRow)` loop only on true — the identical guarantee the MoveFirst() arm of
    /// that same line already relies on.
    ///
    /// WHERE Last() LANDS on a page that DOES have rows is the last DATA row, not the blank
    /// line past it — so this is a fallback for the empty case only, never a step onto the
    /// draft line from a rowset that has data. Both halves are measured on a real service
    /// tier, corpus codeunit 60757 "Test Page Last New Row Line"
    /// (StefanMaron/BusinessCentral.AL.Language.Tests#231), over the same fixture family
    /// codeunit 60743 uses:
    ///
    ///   * EditableInsertableList_Last_LandsOnTheLastDataRow — three seeded rows, Last() reads
    ///     'CHARLIE', so "last data row" and "new-row line" are two distinct positions no
    ///     off-by-one can conflate;
    ///   * EditableInsertableList_NextAfterLast_ReachesTheNewRowLine — the blank line is still
    ///     there, one Next() past where Last() stopped;
    ///   * EmptyEditableList_LastReturnsFalse — asserted in the same procedure as First(), so
    ///     an implementation aliasing the two cannot satisfy both;
    ///   * EmptyEditableList_SetValueAfterLast_InsertsARow and its linked-part twin
    ///     ModalHostPart_EmptyPart_SetValueAfterLast_InsertsARow — the arms this fallback
    ///     exists for, and the only two of the twelve that failed before it.
    /// </summary>
    public override bool MoveLast()
    {
        var record = RequireRecord("MoveLast()");
        FlushParts(); FlushRow(); LeaveNewRowLine();
        var found = _page?.RaiseOnFindRecord("+")
                    ?? record.ALFindLastAsync(DataError.TrapError).GetAwaiter().GetResult();
        if (!found) EnterNewRowLine(record);
        return Loaded(found);
    }

    /// <summary>
    /// Advance to the next row the CLIENT has, which past the last data row of an editable,
    /// insert-allowed repeater is the implicit new-row line — see EnterNewRowLine.
    /// </summary>
    public override bool MoveNext()
    {
        var record = RequireRecord("MoveNext()");
        FlushParts(); FlushRow();

        // Already parked on the new-row line: it is the LAST row of the rowset, so this is
        // where the walk ends. Restore the cursor to the data row it came from first, so a
        // page left at the end is still positioned on a real record rather than on the
        // blank buffer EnterNewRowLine installed.
        if (_onNewRowLine) { LeaveNewRowLine(); return false; }

        if (StepRow(record, 1) != 0) return Loaded(true);
        return EnterNewRowLine(record);
    }

    public override bool MovePrevious()
    {
        var record = RequireRecord("MovePrevious()");
        FlushParts(); FlushRow();

        // Stepping back off the new-row line lands on the last data row — the row the cursor
        // was on when it walked onto the blank line. It is restored rather than re-sought
        // because ALNextAsync(-1) has nothing to step back FROM: the record buffer holds an
        // Init()ed row that is not in the table.
        if (_onNewRowLine) { LeaveNewRowLine(); return Loaded(true); }

        return Loaded(StepRow(record, -1) != 0);
    }

    /// <summary>
    /// Advance to the next DATA row only, never onto the new-row line.
    ///
    /// The blank line belongs to the client's presentation of the rowset, so it is what
    /// TestPage.Next() must walk onto — but it is not a record, and every INTERNAL scan
    /// wants rows that exist. Sharing MoveNext() for both would let a search match the blank
    /// line on any field the caller happened to be looking for an empty value in, and report
    /// a row that is not in the table.
    /// </summary>
    private bool MoveNextDataRow()
    {
        var record = RequireRecord("MoveNext()");
        FlushParts(); FlushRow();
        if (_onNewRowLine) { LeaveNewRowLine(); return false; }
        return Loaded(StepRow(record, 1) != 0);
    }

    /// <summary>
    /// Move one row along the rowset THE PAGE presents: its own OnNextRecord when it declares
    /// one, and the platform step otherwise (issue #3439).
    ///
    /// A page that serves its rows from somewhere other than its SourceTable — a temporary
    /// buffer above all — answers here with rows the record has never held, so stepping the
    /// record directly walks a different set from the one the page shows.
    /// </summary>
    private int StepRow(NavRecord record, int steps)
        => _page?.RaiseOnNextRecord(steps)
           ?? record.ALNextAsync(steps).GetAwaiter().GetResult();

    /// <summary>
    /// Whether this page shows the implicit new-row line: the trailing blank row an editable,
    /// insert-allowed repeater always carries past its data, which is what a user types into
    /// to create a record.
    ///
    /// BC's client appends it in <c>DraftLinePattern.MakeDraftLines</c> — the same trailing
    /// draft row CaptureInsertPosition already has to account for when it computes an
    /// AutoSplitKey. It is part of the rowset the client hands the test framework, so
    /// <c>TestPage.Next()</c> walks onto it and answers true; the controls there read blank
    /// because the line is an Init()ed buffer, not a record.
    ///
    /// The gating is BOTH conditions, and each one was measured on a real service tier
    /// (corpus CU60743): a page opened with OpenView, a page with Editable = false, and a
    /// page with InsertAllowed = false all answer false to that last Next(). _staticEditable
    /// already combines the open mode with the page's declared Editable (see MarkOpened), and
    /// _creatable is the page's declared InsertAllowed — so the two flags the client gates
    /// the draft line on are exactly the two this class already tracks.
    /// </summary>
    private bool ShowsNewRowLine => TestPageNewRowLineRule.ShowsNewRowLine(_staticEditable, _creatable);

    // Set while the cursor sits on the new-row line, with the position of the data row it
    // walked on from — the blank line is a buffer, so the real cursor has to be remembered
    // somewhere in order to be restored when the walk steps off it.
    private bool _onNewRowLine;
    private string? _newRowLineReturnPosition;

    // ONE NEW-RECORD STEP PER DRAFT-LINE ROW (issue #3029). Set the moment the platform's
    // new-record step has run for the draft line the cursor is on, and cleared whenever that
    // line stops being the current one.
    //
    // The invariant it holds is that starting a record is a ONE-TIME event for a row, while
    // the two things that reach it are not: EnterNewRowLine is re-entered by page plumbing
    // that made no cursor move the test asked for, and PromoteNewRowLineForWrite runs on a
    // line EnterNewRowLine has already started. Both used to raise OnNewRecord unconditionally,
    // so one draft-line row cost FIVE firings where BC charges one — measured, see the PR body.
    //
    // A latch and not a counter: the question at both call sites is "has this row been started
    // already", which is a boolean. A count would also have to be reset on exactly the same
    // events, and would invite reading it as "how many times BC would have fired", which is not
    // what it would hold.
    private bool _newRowLineRecordStarted;

    // Set while FindRowFromTableFieldValues (GoToRecord's underlying mechanism) is scanning
    // candidate rows one at a time via repeated MoveFirst/MoveNextDataRow calls — issue
    // #2677. Each intermediate stop DOES run this page's own OnAfterGetRecord (matching real
    // BC, measured: a GoToRecord that has to search fires the host's OnAfterGetCurrRecord for
    // every row the scan lands on before the target). A linked subpage part's refresh must
    // NOT piggyback on every one of those intermediate stops the same way — measured
    // (corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#141): the part re-fires ONLY
    // for the row the scan actually SETTLES on, never for a row merely passed through while
    // searching. See Loaded's own guard and FindRowFromTableFieldValues's explicit refresh
    // once a match is confirmed.
    private bool _suppressPartRefreshDuringScan;

    /// <summary>
    /// Park the cursor on the new-row line: blank the record buffer so every control reads
    /// empty, having first saved the position of the data row being left.
    ///
    /// Deliberately NOT Loaded(): no row was fetched, so there is no OnAfterGetRecord to
    /// raise and no before-image to snapshot. Deliberately NOT _pendingNewRow either — the
    /// client only turns the draft line into a record once someone types into it, so merely
    /// walking a page must not insert a blank row (corpus CU60743
    /// NewRowLine_LeftUntouched_InsertsNothing, and CU60996 for the linked-part case).
    /// <see cref="PromoteNewRowLineForWrite"/> is where typing promotes it — BEFORE the
    /// write's own validate, so the row the trigger sees is the one BC's NewRecord would
    /// have handed it, link values and all (#2923).
    /// </summary>
    private protected bool EnterNewRowLine(NavRecord record)
    {
        if (!ShowsNewRowLine) return false;

        _newRowLineReturnPosition = record.ALGetPosition(useCaptions: false);

        // The rows either side of the insertion point decide the AutoSplitKey number, and
        // ALInit is about to wipe the row the cursor is on — so the position is captured
        // now, exactly as InsertEmptyRow does, in case a SetValue promotes this line into a
        // real insert later.
        CaptureInsertPosition();

        // BC'S NavForm.NewRecord, MINUS THE SAVE. Measured on all 8 BC legs, corpus codeunit
        // 60996 (runs 33995429394 and 33997895349), that the draft line of a linked part:
        //
        //   * reads the SubPageLink's value in the linked PRIMARY-KEY column, not blank
        //     (LinkedPart_DraftLine_ReadsTheLinkValueInTheLinkedKeyColumn — the first run
        //     answered 'H1' where this file had asserted blank);
        //   * has ALREADY run the page's OnNewRecord before anyone types
        //     (LinkedPart_DraftLine_HasRunTheOnNewRecordTrigger — the second run answered
        //     'NEWREC' where this file had asserted blank);
        //   * still reads 0 in the AutoSplitKey column
        //     (LinkedPart_DraftLine_ReadsZeroInTheAutoSplitKeyColumn), and writes nothing while
        //     nobody types (LinkedPart_DraftLineLeftUntouched_InsertsNothing).
        //
        // Those four together are exactly NewRecord and nothing after it: ALInit, copy the
        // page's single-valued filters onto the primary key
        // (RecordImplementation.InitRecordFromFilters), raise OnNewRecord — while SplitKey,
        // OnInsertRecord and the Insert all belong to NavForm.SaveRecord, which is where
        // FlushPendingNewRow does them. So the client starts the record when the blank line
        // becomes current; it just never saves it.
        //
        // This is the SAME call InsertEmptyRow makes for New(). The runner used to do a subset
        // of it by hand here — ALInit, then clear every primary-key field — which left a linked
        // part's key column blank and its OnNewRecord unrun.
        //
        // Deliberately NOT _pendingNewRow (that is what makes walking a page insert nothing)
        // and deliberately NO ALValidateAsync of what the filter copy wrote. The validate step
        // is NavForm.NewRecordAsync's second half, which the promotion path
        // (LiveNavTestPart.InsertEmptyRow -> ValidateStampedFields) runs when a write actually
        // starts the row.
        //
        // ONCE PER ROW (#3029). _newRowLineRecordStarted is what stops a re-entry from raising
        // OnNewRecord a second time for the SAME draft line. It has to be checked HERE, around
        // the new-record step, rather than at the top of the method: the caller's other work is
        // still owed on a re-entry — the return position and the insert position are re-read
        // above because the parent row may have moved under the part, and _onNewRowLine must
        // end up set whichever branch ran. Guarding the whole method would have been the naive
        // placement and is wrong for exactly that reason; it is mutation-tested in the PR body.
        if (_newRowLineRecordStarted)
        {
            // The buffer is already the started row's. Re-blanking it would discard whatever
            // the page's own OnNewRecord put there, which is the damage this guard exists to
            // avoid as much as the duplicate firing is.
            _onNewRowLine = true;
            return true;
        }

        _newRowLineRecordStarted = true;

        if (!(_page?.TryNewRecord(belowXRec: true) ?? false))
        {
            // Record-only mode: no page to ask, so BC's filter step never runs. Do the two
            // halves by hand — ALInit is AL's Init(), which deliberately PRESERVES the primary
            // key, so without the clear the draft line reported the key of the row just walked
            // off; without the copy back it reads blank where the page's filter says otherwise.
            //
            // ClearFieldValue per key field rather than NavRecord.Clear(): Clear() is AL's
            // Clear(Rec), which also drops filters and the current key — and the page's filters
            // are what make the rowset the page's own (a part's SubPageLink above all).
            // Blanking the buffer must not silently widen what the page is showing.
            record.ALInit();
            var primaryKey = record.MetaTable?.PrimaryKey;
            if (primaryKey != null)
                for (var i = 0; i < primaryKey.KeyFieldCount; i++)
                {
                    var keyFieldNo = primaryKey.KeyFieldsList[i].FieldNo;
                    record.ClearFieldValue(keyFieldNo);
                    if (TryGetSingleFilterValue(record, keyFieldNo, out var fromFilter))
                        record.SetFieldValue(keyFieldNo, fromFilter);
                }
        }

        _onNewRowLine = true;
        return true;
    }

    /// <summary>
    /// Step off the new-row line, putting the record buffer back on the data row the cursor
    /// came from. Every cursor move that is not "advance onto the blank line" goes through
    /// here, so the blank buffer can never outlive the one position it is valid at.
    /// </summary>
    private void LeaveNewRowLine()
    {
        if (!_onNewRowLine) return;
        _onNewRowLine = false;
        // Stepping off the draft line ends that row (#3029) — see AbandonNewRowLine.
        _newRowLineRecordStarted = false;
        var position = _newRowLineReturnPosition;
        _newRowLineReturnPosition = null;
        if (!string.IsNullOrEmpty(position)) _record!.ALSetPosition(position);
    }

    /// <summary>
    /// Drop the new-row line WITHOUT restoring the position it saved — for the one case where
    /// that position is not valid to go back to: a linked part being re-pointed at a different
    /// parent row (<see cref="LiveNavTestPart.ReloadLinkedRow"/>). The saved position names a
    /// row of the OLD link's rowset, and the caller re-finds against the new one immediately,
    /// so restoring it would put the buffer on a row the part no longer shows.
    ///
    /// Kept distinct from <see cref="LeaveNewRowLine"/> because the flag itself must still be
    /// cleared either way: <c>Loaded()</c> does not touch it, so a part that walked onto its
    /// draft line and then had its parent move would otherwise sit on a real row while still
    /// claiming to be on the blank line — and the next write would insert instead of modify.
    /// </summary>
    private protected void AbandonNewRowLine()
    {
        _onNewRowLine = false;
        _newRowLineReturnPosition = null;
        // The row this draft line stood for is gone, so the NEXT draft line is a new row and
        // owes its own new-record step (#3029). Clearing here rather than only in Reset is what
        // keeps the latch from turning "once per row" into "once per page".
        //
        // Guarded by DraftLineAbandonedByAParentMove_MakesTheNextRowOweItsOwnFiring, and by
        // that arm alone: every other arm stays within ONE parent row, so all of them pass with
        // this reset removed. Review established that by removing it — the fixture and all four
        // corpus arms stayed green. Only moving the parent between two draft lines separates
        // "once per row" from "once per page".
        _newRowLineRecordStarted = false;
    }

    /// <summary>The one value a field's current filter selects, or false when the filter is
    /// not a single value (BC's <c>GetRangeMin</c>/<c>GetRangeMax</c> raise for a filter that
    /// is not a range; a range whose ends differ is not a single value either).
    ///
    /// On the base class rather than on <see cref="LiveNavTestPart"/> because BOTH users of
    /// BC's filter-copy rule need it: the part's New() stamping, and
    /// <see cref="EnterNewRowLine"/>'s draft line. The rule is about the record's FILTERS, not
    /// about a SubPageLink — so reading it off the filters covers const/filter/field links and
    /// a plain filtered page with one mechanism, and answers "nothing to copy" for an
    /// unfiltered page without needing a special case.</summary>
    private protected static bool TryGetSingleFilterValue(NavRecord record, int fieldNo, out NavValue value)
    {
        try
        {
            var min = record.ALGetRangeMin(fieldNo);
            var max = record.ALGetRangeMax(fieldNo);
            if (min != null && min.Equals(max)) { value = min; return true; }
        }
        catch (NavBaseException)
        {
            // Not a range: a multi-value expression (1|2), an open-ended one (>1), or a
            // wildcard. BC's own InitRecordFromFilters stamps nothing for these either.
        }
        value = null!;
        return false;
    }

    /// <summary>
    /// A row just became the page's current row — run the page's OnAfterGetRecord, exactly
    /// as BC does after every load. That trigger is where a page derives its per-row state
    /// (the variable behind <c>Editable = …</c>, <c>CurrPage.Editable(…)</c>), so skipping it
    /// froze every page at whatever state its first row left behind.
    ///
    /// <c>protected</c> (not <c>private</c>) so <see cref="LiveNavTestPart"/> can drive its
    /// own SubPageLink-matched row through the identical path a top-level page's
    /// MoveFirst/MoveNext/GoToBookmark already use — see issue #2677's
    /// <c>ReloadLinkedRow</c>.
    /// </summary>
    protected bool Loaded(bool found)
    {
        if (found)
        {
            try
            {
                _page?.RaiseOnAfterGetRecord();
            }
            // NavBaseException only -- matches real BC's own teardown, NstDataAccess.Abort
            // (NavBaseException exception), which only wraps a genuine AL-catchable error
            // (Error(), TestField, a table trigger's own refusal, ...). A RunnerOutOfScopeException
            // (plain System.Exception, never NavBaseException -- see NavDotNetPatches.cs) or a
            // genuine runner NRE must NOT be relabelled as "The TestPage is not open.": that
            // would hide an OOS surface's real reason, or a runner bug, behind a fake BC message
            // (.claude/rules/loud-failures.md).
            catch (NavBaseException ex)
            {
                // See _suppressTeardownOnLoad: the page-construction-time initial position is
                // not a teardown-worthy call. Let the original exception propagate unmodified,
                // exactly as it did before this fix (into a blanket `catch {}` at the call site).
                if (_suppressTeardownOnLoad) throw;

                // Real BC (measured 27.5/28.3/28.4, issue #2656): an unhandled AL error here
                // tears the TestPage down. The original error's own text never reaches the AL
                // caller -- what propagates out of this call (and every later one on the same
                // variable) is BC's own "The TestPage is not open." The original is kept as
                // diagnostic data (see MakeTestPageNotOpenException); it is not AL-visible
                // (asserterror / GetLastErrorText only see the outer message), matching what
                // real BC surfaces.
                _tornDown = true;
                throw MakeTestPageNotOpenException(ex);
            }
            SnapshotBeforeImage();
            // Issue #2677: NOT during a FindRowFromTableFieldValues scan — see
            // _suppressPartRefreshDuringScan's doc comment and that method's own explicit
            // refresh once a match is confirmed.
            if (!_suppressPartRefreshDuringScan)
                RefreshLinkedParts();
        }
        return found;
    }

    /// <summary>
    /// Refresh every linked subpage part to THIS page's current row — issue #2677, measured
    /// on real BC (corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#141): a linked
    /// subpage part (FactBox-style, SubPageLink to this page's key) refreshes to the NEW
    /// current row every time this page's own row changes — GoToRecord on the host re-fires
    /// the part's OnAfterGetRecord/OnAfterGetCurrRecord for the row just arrived at, and does
    /// NOT re-fire it for the row just left. Only linked parts refresh here: an unlinked part
    /// shows its own table's full rowset, independent of this page's current row, and BC's
    /// own re-sync behaviour for that shape is unmeasured — see LiveNavTestPart.HasLinks.
    /// </summary>
    private void RefreshLinkedParts()
    {
        foreach (var part in _parts.Values)
            if (part is LiveNavTestPart { HasLinks: true } linkedPart)
                linkedPart.ReloadLinkedRow();
    }
}
