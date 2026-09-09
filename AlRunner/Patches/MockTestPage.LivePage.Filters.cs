// LiveNavTestPage: row addressing — bookmarks, find-by-value, filters and keys.
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
    /// <summary>
    /// Take the page's before-image of the current row — what the table's <c>OnModify</c> reads
    /// as <c>xRec</c> when the edit is driven from a page.
    ///
    /// This is the tail of BC's own <c>NavForm.AfterGetRecordAsync</c> AND of
    /// <c>NavForm.AfterGetCurrRecordAsync</c> — both end with
    /// <c>OldRecord.ALAssign(SourceTable)</c>, and <c>NavForm.OldRecord</c> is literally
    /// <c>SafeSourceTable.OldRecord</c>, so the target is this record's own xRec slot. Those two
    /// are exactly the pair of triggers RaiseOnAfterGetRecord above fires, which is why a row
    /// becoming the current row is one of the moments BC takes it.
    ///
    /// <para>It is not the only one, and this method now has FOUR callers — issue #3440. BC also
    /// retakes the before-image after every successful page-driven WRITE, so a second write in
    /// one page session sees the first write's row as its xRec: <c>NavForm.InsertAsync</c> does
    /// it inline (mirrored in <see cref="FlushPendingNewRow"/>), and <c>SaveRecordAsync</c>
    /// leaves it to the client, which re-reads and lands in <c>AfterGetCurrRecordAsync</c>'s own
    /// tail — mirrored in <see cref="FlushPendingModify"/> for the runner's own write path and in
    /// <c>RunnerPageInstance.RefreshBeforeImageAfterSave</c> for <c>CurrPage.SaveRecord()</c> /
    /// <c>Update(true)</c>. Removing any one of the four puts the stale before-image back on
    /// that path. What still holds is the OTHER half of the old sentence: nothing overwrites it
    /// BETWEEN the write's start and its trigger, so OnModify sees the row as fetched.</para>
    ///
    /// Without this the page had no before-image at all: <c>ALModifyAsync</c>'s own
    /// <c>OldRecord.ALAssign(this)</c> was the only thing that ever populated xRec, which is
    /// what made a page-driven Modify report the NEW value as the old one.
    /// </summary>
    // Non-null: only ever called from Loaded(true), which every MoveXxx/GoToBookmark caller
    // reaches through RequireRecord first.
    private void SnapshotBeforeImage() => _record!.OldRecord.ALAssign(_record);

    // useCaptions: false — NavRecord.ALGetPosition()'s default (useCaptions: true) encodes
    // the position string using field CAPTIONS, and ALSetPosition decodes it through the
    // same SETVIEW-style filter parser TableViewParser.ParseTableFilters uses for AL filter
    // views, which resolves each token by caption. On a table with two fields sharing a
    // caption (legal AL) that decode throws BC's own NavNCLFieldNotFoundException
    // ("... is ambiguous between multiple fields ...") instead of positioning — real BC
    // does not throw here (issue #2515). Positioning by field NUMBER, exactly like every
    // other cursor move in this class (ALSetPosition/GetFieldValue take field numbers, never
    // captions), sidesteps the ambiguous caption lookup entirely. Both overloads are real
    // BC's own public API on NavRecord; this only picks the one that matches how the rest of
    // the runner already talks to a record.
    public override object? GetBookmark() => RequireRecord("GetBookmark()").ALGetPosition(useCaptions: false);

    public override bool GoToBookmark(object bookmark)
    {
        if (bookmark is not string position || string.IsNullOrEmpty(position)) return false;
        // Jumping to a bookmark is a cursor move like any other, so it steps off the blank
        // line first — otherwise the flag would survive onto a real row and the NEXT
        // MoveNext() would end the walk early.
        LeaveNewRowLine();
        RequireRecord("GoToBookmark()").ALSetPosition(position);
        return Loaded(true);
    }

    public override object[] GetTableFieldValues(int[] fieldIds)
        => fieldIds.Select(fieldNo => ReadClientObject(fieldNo) ?? string.Empty).ToArray();

    /// <summary>
    /// The only ITestPage entry point that genuinely receives a CONTROL id — and, unlike
    /// <see cref="FindRowFromTableFieldValues"/>, the one whose caller has ALREADY positioned
    /// the cursor where the search must begin. That is why it does not simply forward.
    ///
    /// <para>BC's <c>NavTestPageBase.InternalFindRowFromControlFieldValue</c> drives all three
    /// of FindFirstField/FindNextField/FindPreviousField, and it makes the initial move
    /// itself before calling in here:</para>
    /// <code>
    /// switch (initialMove) {
    ///   case InitialMove.First:    TestPage.MoveFirst(); break;
    ///   case InitialMove.Next:     if (!TestPage.MoveNext())     return false; break;
    ///   case InitialMove.Previous: if (!TestPage.MovePrevious()) return false; break;
    /// }
    /// return TestPage.FindRowFromControlFieldValue(fieldNo, value, initialMove != InitialMove.Previous);
    /// </code>
    /// <para>So the position on entry IS the argument: for FindNextField it is one row past
    /// the last match, for FindPreviousField one row before it. Re-seeking to the first (or
    /// last) row here discards it, and both members then answer the row FindFirstField
    /// already returned — FindNextField never advances and FindPreviousField never goes back
    /// (issue #3312).</para>
    ///
    /// <para>The sibling path is genuinely different and stays as it was:
    /// <c>InternalFindRowFromTableFieldValues</c> — which is what GoToKey and GoToRecord
    /// reach — calls <c>TestPage.MoveFirst()</c> unconditionally before its own
    /// <c>FindRowFromTableFieldValues</c>, so for THAT caller "scan the whole rowset" and
    /// "resume from the cursor" are the same answer. Verified against
    /// Microsoft.Dynamics.Nav.Ncl.dll.</para>
    /// </summary>
    public override bool FindRowFromControlFieldValue(int controlId, object value, bool forward)
        => FindRowFromFieldValues(new[] { ControlIdToTableFieldNo(controlId) }, new[] { value }, forward,
            startFromCurrentRow: true);

    public override bool FindRowFromTableFieldValues(int[] fieldNos, object[] values, bool forward)
        => FindRowFromFieldValues(fieldNos, values, forward, startFromCurrentRow: false);

    private bool FindRowFromFieldValues(int[] fieldNos, object[] values, bool forward, bool startFromCurrentRow)
    {
        if (fieldNos.Length != values.Length) return false;

        var record = RequireRecord("locating a row");

        // Capture the ORIGINAL row's own primary-key field numbers and values (not just a
        // position string) before scanning moves the cursor away from it. A not-found result
        // must restore the exact row the page was on — including every NON-key field it was
        // showing — and NavRecord.ALSetPosition (real BC engine code, unmodified) only writes
        // the primary-key columns of the record buffer, leaving non-key columns holding
        // whatever the internal scan below last read (issue #2537: GoToRecord(existing row A)
        // then GoToRecord(absent row) left the page's non-key field reading row C's value
        // under key A, because the scan's last MoveNextDataRow landed on C before failing).
        // Re-finding the original row through the SAME MoveFirst/MoveNextDataRow path the
        // search below already uses is what refreshes a row's non-key columns correctly (they
        // go through NavRecord.ALFindFirstAsync/ALNextAsync, not the key-only SetPosition), so
        // the restore reuses that exact mechanism instead of a raw position write.
        var hasCurrent = !string.IsNullOrEmpty(record.ALGetPosition(useCaptions: false));
        int[]? originalKeyFieldNos = null;
        object?[]? originalKeyValues = null;
        if (hasCurrent)
        {
            var originalPrimaryKey = record.MetaTable?.PrimaryKey;
            if (originalPrimaryKey != null && originalPrimaryKey.KeyFieldCount > 0)
            {
                originalKeyFieldNos = originalPrimaryKey.KeyFieldsList.Select(f => f.FieldNo).ToArray();
                originalKeyValues = originalKeyFieldNos.Select(fieldNo => ReadClientObject(fieldNo)).ToArray();
            }
        }

        // Where the scan STARTS is the caller's decision, not the direction's.
        //
        // startFromCurrentRow: false (FindRowFromTableFieldValues — GoToKey, GoToRecord) scans
        // the WHOLE rowset from the first (or last, when searching backward) row, never from
        // wherever the page happens to be positioned. `forward` is then a direction, not
        // "resume from the cursor": BC's client locates the requested row anywhere in the
        // rowset. Starting at the current row silently failed to find any row BEHIND the
        // cursor, so navigating C -> A returned false even though A is on the page
        // (tests/runner-extras/testpage-gotorecord GoToRecord_MovesBetweenRows). BC agrees
        // for this caller by construction: InternalFindRowFromTableFieldValues calls
        // TestPage.MoveFirst() itself before reaching here.
        //
        // startFromCurrentRow: true (FindRowFromControlFieldValue — FindFirstField and
        // friends) resumes from the cursor, because BC's InternalFindRowFromControlFieldValue
        // already made the MoveNext()/MovePrevious() that says where to begin. See that
        // method's own doc comment above for the decompiled shape (issue #3312).
        //
        // Issue #2677: the scan below runs Loaded(true) — and so this page's own
        // OnAfterGetRecord — for every intermediate row it passes through before landing on
        // the target, matching BC's own measured behaviour. A linked subpage part must NOT
        // piggyback on those intermediate stops; _suppressPartRefreshDuringScan holds that
        // off, and the one explicit RefreshLinkedParts() call below — once a match is
        // confirmed, for that row only — is what a linked part actually re-fires for.
        _suppressPartRefreshDuringScan = true;
        try
        {
            // startFromCurrentRow: the caller positioned the cursor and that position is the
            // search's starting point (see FindRowFromControlFieldValue). "Current row" means
            // the row the record is actually standing on — an unpositioned record has no such
            // row, so it falls back to the end the direction starts from, which is also what
            // BC's InitialMove.First arm produces after its MoveFirst().
            var hasRow = startFromCurrentRow && hasCurrent
                ? true
                : forward ? MoveFirst() : MoveLast();

            while (hasRow)
            {
                if (Matches(fieldNos, values))
                {
                    _suppressPartRefreshDuringScan = false;
                    RefreshLinkedParts();
                    return true;
                }
                // MoveNextDataRow, not MoveNext: a search wants rows that EXIST. Walking the
                // scan onto the new-row line would let any request for an empty value "find"
                // the blank line and report a row that is not in the table.
                hasRow = forward ? MoveNextDataRow() : MovePrevious();
            }

            if (originalKeyFieldNos != null)
            {
                // Re-find the original row by its own primary key, walking forward from the
                // top exactly like the search above — this goes through a real MoveFirst/
                // MoveNextDataRow load, refreshing every field (not just the key) from the
                // row's own stored values, instead of a raw key-only ALSetPosition. Still
                // suppressed: this restores the SAME row the page (and its parts) were
                // already showing before the failed search started, so there is nothing new
                // for a linked part to refresh to.
                hasRow = MoveFirst();
                while (hasRow)
                {
                    if (Matches(originalKeyFieldNos, originalKeyValues!)) break;
                    hasRow = MoveNextDataRow();
                }
            }
            return false;
        }
        finally
        {
            _suppressPartRefreshDuringScan = false;
        }
    }

    // ITestFilter.SetFilter/GetFilter are handed a TABLE FIELD NUMBER, not a control id:
    // AL's `TestPage.Filter.SetFilter(Field, ...)` resolves the field reference itself and
    // BC passes the field number straight through. Routing these through the control map
    // was wrong in both directions — it would mistranslate a field number that happens to
    // collide with a control id, and it rejected small, perfectly valid field numbers as
    // "not a control" (Pageworks SetFilter(3, …) on PageworksPartial).
    public override void SetFilter(int fieldNo, string filterValue)
    {
        RequireRecord("SetFilter()").ALSetFilter(fieldNo, filterValue);
        RepositionAfterFilterChange();
    }

    /// <summary>
    /// A filter changes which rows the page HAS, so the cursor may no longer be on one of
    /// them. Left alone, the page keeps answering from a record the filter excludes — and
    /// that reads as a real, plausible value belonging to the wrong row, so the test fails
    /// claiming the data is wrong rather than the cursor.
    ///
    /// Real BC always repositions to the FIRST row of the new filtered set, exactly like the
    /// underlying Record.SetFilter; it does not special-case "the current row still
    /// qualifies" to leave the cursor in place (corpus CU60694
    /// SetFilter_EvenWhenCurrentRowStillQualifies_RepositionsToTheFirstMatch, validated
    /// against a real service tier). An empty result leaves the page on no row, which
    /// MoveFirst reports as false.
    /// </summary>
    private void RepositionAfterFilterChange()
    {
        // A FILTER CHANGE ENDS THE DRAFT LINE'S ROW (#3029). The blank line a page shows past
        // its data stands for a row IN the current rowset — its key fields are filled from that
        // rowset's own single-valued filters — so once the filter moves it stands for a
        // different row and owes a fresh new-record step.
        //
        // Without this, corpus codeunit 60710's OpenEdit -> SetFilter -> New() sequence took
        // MoveFirst's same-row branch: the page had parked on a draft line for the UNfiltered
        // rowset while opening, the filter then selected P2, and New() reused the row started
        // before anyone had said P2 — so the new row carried a blank ParentCode instead of the
        // filter's value. Three tests, and they are the reason this clears rather than the
        // reasoning above.
        AbandonNewRowLine();
        MoveFirst();
    }

    public override string GetFilter(int fieldNo)
        => RequireRecord("GetFilter()").ALGetFilter(fieldNo);

    // ── ITestFilter: the key and the direction the page walks (#3316) ─────────────
    //
    // The same argument SetFilter above makes. A page's key and sort direction are properties
    // of the rowset, so they belong on the NavRecord the page walks — every navigation member
    // of this class goes through ALFindFirstAsync/ALNextAsync on that record, and those read
    // the record's current key and ascending flag. Held in fields on this object instead (what
    // MockITestPage does, and what this class inherited until now) they were a write-only
    // store: SetCurrentKey and Ascending were recorded and reported back, and the page went on
    // walking its primary key ascending regardless.
    //
    // Delegating also fixes CurrentKey's rendering for free rather than by a second mechanism.
    // NavRecord.ALCurrentKey resolves the key against the table's own metadata and names its
    // fields, which is what corpus codeunit 60398's Record-side assertions already pin
    // ('CurrentKey() must include primary key field Entry No.'); the field-number join this
    // class used to inherit is what produced the observed '3' and '2, 3'.

    /// <summary>
    /// Install the key the page walks. Delegates to the record, so it changes the ORDER the
    /// page walks and not merely what <see cref="CurrentKey"/> reports.
    ///
    /// <para>An empty or null field list leaves the record's key alone: BC's own
    /// NavRecord.ALSetCurrentKey builds an NCLMetaField per field and asks the record
    /// implementation to select a key from them, and a zero-length list names no key. AL
    /// cannot produce that call anyway — <c>SetCurrentKey()</c> with no argument is
    /// error AL0135 — so this only guards the interface, which is not AL-constrained.</para>
    /// </summary>
    public override void SetCurrentKeyFields(int[] fields)
    {
        if (fields == null || fields.Length == 0) return;
        RequireRecord("SetCurrentKey()").ALSetCurrentKey(fields);
        // A key change reorders the rowset, so the cursor's position within it is no longer
        // meaningful — the same reason SetFilter repositions. BC's client reopens the rowset
        // on the new key and lands on its first row, which is what the corpus asserts by
        // walking from First() after SetCurrentKey.
        RepositionAfterFilterChange();
    }

    /// <summary>
    /// The field numbers of the key the record is currently walking. Answered from the
    /// record's own current key rather than from a remembered argument list, so a page whose
    /// key was never set through this interface still reports the key it is actually on.
    /// </summary>
    public override int[] GetCurrentKeyFields()
        => _record == null ? Array.Empty<int>() : TestFilterKeyFields.Of(_record);

    /// <summary>
    /// The direction the page walks its current key. Read and written on the record, so
    /// <c>Ascending(false)</c> reverses the walk instead of only being reported back.
    /// </summary>
    public override bool Ascending
    {
        get => _record == null || _record.ALAscending;
        set
        {
            RequireRecord("Ascending()").ALAscending = value;
            // Reversing the order moves the first row, so the cursor is repositioned for the
            // same reason a key change repositions it.
            RepositionAfterFilterChange();
        }
    }

    /// <summary>
    /// The current key rendered the way BC renders it — naming the key's fields. Straight
    /// through to NavRecord.ALCurrentKey, which is what AL's own <c>Record.CurrentKey()</c>
    /// reads, so the page and the record can never disagree about the key the page is on.
    /// </summary>
    public override string CurrentKey => _record == null ? string.Empty : _record.ALCurrentKey;

    /// <summary>
    /// Resolve a CONTROL id to the source-table field it is bound to. A control bound to a
    /// page variable is not in the rowset and cannot be used to locate a row, so this
    /// refuses rather than passing the control id through as a field number — which is
    /// what produced "field number '&lt;hash&gt;' cannot be found", blaming the table for
    /// the runner's own inability to resolve the control.
    /// </summary>
    private int ControlIdToTableFieldNo(int controlId)
    {
        if (_controlIdToFieldNo.TryGetValue(controlId, out var fieldNo)) return fieldNo;
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            $"TestPage control {controlId} used to locate a row",
            "testpage-control-binding — this control is not bound to a field of the page's "
            + $"source table ({_record?.MetaTable?.TableName ?? "?"}), so it cannot be used to "
            + "locate a row. See docs/scope.md");
    }

    private bool Matches(int[] fieldNos, object[] values)
    {
        for (var i = 0; i < fieldNos.Length; i++)
            if (!ValuesEqual(ReadClientObject(fieldNos[i]), Unwrap(values[i])))
                return false;
        return true;
    }

    private object? ReadClientObject(int fieldNo) => Unwrap(RequireRecord("field access").GetFieldValue(fieldNo));

    internal static object? Unwrap(object? value)
        => value is NavValue navValue ? navValue.ClientObject : value;

    private static bool ValuesEqual(object? left, object? right)
    {
        left = Unwrap(left);
        right = Unwrap(right);
        return Equals(left, right);
    }
}
