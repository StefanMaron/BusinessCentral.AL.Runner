// LiveNavTestPage: the client write buffer — pending insert, AutoSplitKey, pending modify,
// and the flushes that persist them (see docs/testpage-write-buffer.md).
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
    // TestPage.New() reaches ITestPage.InsertEmptyRow. BC's client model is "start a blank
    // row now, persist it once the cursor leaves it (or the page closes)" — the SetValue
    // calls in between write into the record buffer. The base mock no-ops, which silently
    // dropped every insert made through a TestPage; a LIVE page has a real record, so it
    // must initialise the buffer and remember to flush it.
    private bool _pendingNewRow;

    /// <summary>
    /// Turn the current position into a pending insert.
    ///
    /// <para>SKIPS THE PLATFORM'S NEW-RECORD STEP WHEN THE ROW IS ALREADY STARTED (#3029). Two
    /// callers arrive on a draft line <see cref="EnterNewRowLine"/> has already started: a
    /// write promoting it (<see cref="PromoteNewRowLineForWrite"/>) and a <c>New()</c> on a
    /// part that opened over an empty rowset. Neither creates a SECOND row — both commit to
    /// the one the blank line already stands for — so re-running the step would raise the
    /// page's OnNewRecord twice for one row AND re-blank the buffer, discarding what that
    /// trigger wrote.</para>
    ///
    /// <para>Read off <c>_newRowLineRecordStarted</c> rather than passed in by each caller.
    /// Both spellings were built and mutation-tested; the parameter turned out to be dead,
    /// because the state it duplicated is exactly the state the callers would have had to
    /// consult in order to set it. One source of truth is what stops the two from disagreeing.
    /// Everything else the entry point does is still owed on these paths — the flush of a
    /// previous pending row, the insert-position capture that feeds AutoSplitKey, a part's
    /// SubPageLink stamping and its validate step — so only the one call is skipped.</para>
    /// </summary>
    public override void InsertEmptyRow(bool beforeCurrent)
    {
        // A page with no SourceTable has no rowset to insert into at all — refuse by name
        // before touching any of the state below, rather than NRE-ing inside CaptureInsertPosition.
        RequireRecord("New()");

        // New() from the new-row line starts the row explicitly; the draft bookkeeping is
        // superseded by the CaptureInsertPosition below, and its saved return position must
        // not survive to drag the cursor back off the row being created.
        //
        // NEW() ON A STARTED DRAFT LINE IS THE SAME ROW (#3029). A part that opened over an
        // empty rowset is already parked on its draft line, and the platform has already run
        // its new-record step for it. New() there does not create a SECOND row — it commits to
        // the one the blank line already stands for, exactly as typing into it does. So the
        // same fact the promotion passes explicitly is also true when the caller did not say
        // so, and is read off the latch rather than demanded of every caller.
        var alreadyStarted = _onNewRowLine && _newRowLineRecordStarted;

        _onNewRowLine = false;
        _newRowLineReturnPosition = null;
        // The draft line is being consumed either way, so whatever it started is now this row's
        // — the NEXT draft line owes its own new-record step (#3029).
        _newRowLineRecordStarted = false;

        FlushPendingNewRow();   // starting a second row persists the first

        // The rows around the insert decide the new row's AutoSplitKey number, and the row
        // the cursor sits on is about to be wiped by NewRecord's ALInit — so the position is
        // read NOW and the number computed from it at flush time (ProposeAutoSplitKey).
        CaptureInsertPosition();

        // Ask the page to start the row, exactly as it would for a user: BC's NavForm.NewRecord
        // does ALInit, fills the linking fields in from the page's own filters, and raises
        // OnNewRecord. A filtered page is showing one parent's rows, so a row created on it
        // belongs to that parent — that is what makes Lines.New() on a subpage produce a line
        // already attached to its header.
        //
        // The runner used to do the first and last of those steps by hand and skip the middle,
        // so the row arrived with blank keys and the damage surfaced one step later: an
        // OnValidate looking its parent up found nothing, and the test failed naming a derived
        // field rather than the key that was never set.
        // alreadyStarted: the draft line being promoted already ran this exact step when the
        // cursor landed on it, so running it again would raise the page's OnNewRecord a second
        // time for one row AND re-blank the buffer, discarding what that trigger wrote (#3029).
        if (!alreadyStarted && !(_page?.TryNewRecord(!beforeCurrent) ?? false))
        {
            // Record-only mode: no page to ask, so no filters and no trigger to run either.
            // Non-null: guaranteed by the RequireRecord guard at the top of this method.
            _record!.ALInit();
            // The tail of NavForm.NewRecordAsync is `OldRecord.ALAssign(SourceTable)`, and
            // TryNewRecord runs it on the page path. Record-only mode never reaches BC's
            // method at all, so the snapshot RowValuesChangedSinceLoad compares against has to
            // be taken here or the gate below would measure this row against some earlier one.
            _record!.OldRecord.ALAssign(_record);
        }

        _pendingNewRow = true;
    }

    /// <summary>
    /// BC's own "is this row worth saving" gate, lifted from <c>NavForm.SaveRecordAsync</c>:
    /// <c>!SafeSourceTable.CompareAllNormalFields(SafeSourceTable.OldRecord, null)</c>. When it
    /// answers false, SaveRecordAsync falls straight through to its UpdateRequest and writes
    /// NOTHING — no SplitKey, no OnInsertRecord, no Insert.
    ///
    /// The comparison works because <c>NewRecordAsync</c> ends with
    /// <c>OldRecord.ALAssign(SourceTable)</c>, taken AFTER <c>InitializeFieldsFromFilters</c>
    /// and AFTER <c>OnNewRecord</c>. So the baseline is the row exactly as <c>New()</c> left it,
    /// and only a write the test itself made can move it — which is what makes a row nobody
    /// filled in disappear when the card closes, while <c>New()</c> + <c>SetValue</c> + close
    /// still writes a row.
    ///
    /// <para><c>fieldsInitializedFromFilters</c> is passed as null deliberately, and it is not a
    /// simplification: in <c>CompareAllNormalFields</c> that set FORCES a difference rather than
    /// excluding one, so passing it would report every filter-stamped row as changed and save
    /// it. Which of the two SaveRecordAsync overloads the close path behaves like is settled by
    /// measurement, not by reading: corpus CU60648
    /// <c>New_NothingTouched_IsDiscardedWhenTheCardCloses</c> does <c>New()</c> on a part whose
    /// linked field IS in the primary key — so the stamp definitely happened — and real BC
    /// 27.0 through 28.4 still reports the row gone. That is only possible with
    /// <c>detectChangeFromFieldsInitializedFromFilters: false</c>, which is what the no-argument
    /// <c>SaveRecordAsync()</c> (the one <c>NavForm.UpdateCoreAsync</c> uses) passes.</para>
    ///
    /// <para>Applied to <see cref="FlushPendingModify"/> too, since issue #3055 — one gate, both
    /// halves, which is also SaveRecordAsync's own shape. The open question recorded here used
    /// to be whether the OR's second arm (<c>calledFromALCode &amp;&amp;
    /// RecordImplementation.HasChangedFields</c>) let a same-value write through, since
    /// <c>_pendingModify</c> is set by <see cref="MarkEdited"/> on any assignment. It does not:
    /// that arm reaches <c>MutableRecordBuffer.HasActualChangedValues()</c>, which returns
    /// <c>false</c> unless some modified field fails <c>IsChangedValueSameAsOriginalValue</c>.
    /// Both arms are value comparisons, so neither writes a row that did not move.
    /// <c>docs/testpage-write-gate.md</c> has the decompiled bodies and what measured them.</para>
    /// </summary>
    private bool RowValuesChangedSinceLoad()
    {
        // Non-null: only reached from FlushPendingNewRow, gated by _pendingNewRow, which is
        // only set after InsertEmptyRow's RequireRecord guard (or by MarkEdited, which is only
        // wired to a Rec-bound control and so implies a record too).
        var record = _record!;
        return !record.CompareAllNormalFields(record.OldRecord, null);
    }

    internal void FlushPendingNewRow()
    {
        if (!_pendingNewRow) return;
        _pendingNewRow = false;
        // A row New() started and nothing wrote to is not persisted — BC discards it rather
        // than inserting a blank line, so a subpage part that showed 2 rows still shows 2.
        // See RowValuesChangedSinceLoad for the mechanism and what measured it.
        //
        // The captured insert position is dropped with the row: it describes bounds read at
        // THIS New()'s cursor, and leaving it armed would offer them to the next insert, which
        // may be on another row or another part entirely.
        if (!RowValuesChangedSinceLoad()) { _insertPositionCaptured = false; return; }
        // AutoSplitKey, in BC's own order: SplitKey, then OnInsertRecord, then the record's
        // Insert (NavForm.SaveRecordAsync / NavForm.InsertAsync(belowXRec) both do exactly
        // this). Skipping it left the last primary-key field at its Init() default, so a page
        // whose whole numbering scheme is AutoSplitKey — every editable line grid in BC —
        // wrote its first row at line no. 0 and could not write a second one at all: the same
        // key, so the insert failed on a duplicate. It is a no-op inside BC's own guard for a
        // page that does not declare the property.
        ProposeAutoSplitKey();
        _page?.SplitKey();
        // OnInsertRecord is the page's last word before the row exists, and its RETURN VALUE
        // is a veto — a page can refuse the insert outright. Running it and discarding the
        // answer would be worse than not running it: the row lands anyway, but now it also
        // carries whatever the trigger wrote on its way to saying no.
        if (_page != null && !_page.RaiseOnInsertRecord(false)) return;
        // runApplicationTrigger: true. Inserting a row from a page runs the table's OnInsert, the
        // same as Rec.Insert(true) — that trigger is where a table assigns its number series,
        // stamps its own derived fields, and enforces what it will not accept. Passing false
        // wrote a row the table had never agreed to.
        // Non-null: _pendingNewRow is only ever set true by InsertEmptyRow, which refuses by
        // name first when the page has no record — see RequireRecord there.
        _record!.ALInsertAsync(DataError.TrapError, true, false).GetAwaiter().GetResult();
        // The row is now the page's own row, so it is also its own before-image — BC's
        // NavForm.InsertAsync does exactly this, under exactly this guard
        // (`if (SourceTable.HasBeenInserted) OldRecord.ALAssign(SourceTable)`). Without it the
        // next write on the same page instance would compare against, and report as xRec, the
        // blank row New() started (issue #3440).
        if (_record!.HasBeenInserted) SnapshotBeforeImage();
    }

    /// <summary>
    /// Abandon an in-progress new row without writing it — how Cancel closes. Clears the
    /// captured insert position for the same reason FlushPendingNewRow's discard branch does:
    /// the bounds belong to the row being thrown away, and an armed capture would be consumed
    /// by whatever inserts next.
    /// </summary>
    internal void DiscardPendingNewRow()
    { _pendingNewRow = false; _pendingModify = false; _onNewRowLine = false; _newRowLineReturnPosition = null; _insertPositionCaptured = false; _newRowLineRecordStarted = false; }

    // BC's AutoSplitKey increment. Named NavForm.AutoSplitKeyIncrement there, and the same
    // literal in the client's AutoKeyGenerator — both sides of the wire agree on 10000.
    private const int AutoSplitKeyIncrement = 10000;

    /// <summary>
    /// Do the CLIENT half of AutoSplitKey: work out the key the new row should get and offer it
    /// to BC's <c>NavForm.SplitKey()</c> as <c>AutoKeyValue</c>. SplitKey still owns the answer —
    /// it validates the proposal against the table and falls back to its own arithmetic if the
    /// key is taken — but without a proposal it has nothing to compute from.
    ///
    /// WHY THE RUNNER HAS TO DO THIS AT ALL
    ///   SplitKey's inputs are all client-supplied: <c>AutoKeyValue</c>, and the
    ///   <c>InsertLowerBoundBookmark</c> / <c>InsertUpperBoundBookmark</c> pair naming the rows
    ///   the new one is being inserted between. On a service tier those come off the repeater's
    ///   loaded rows (<c>NavRecordStateHandler.GetUpperAndLowerRowEntryBookmarks</c> and
    ///   <c>AutoKeyGenerator.GenerateKey</c>) and travel in <c>NavRecordState</c>. This class IS
    ///   the client, so all three were null on every insert and
    ///   <c>CalculateAutoSplitKeyValue(null, null)</c> answered a flat 10000 — the same constant
    ///   for every row, derived from no data at all. On an empty grid that is one interval low;
    ///   on a grid whose rows start anywhere else it puts the new row BEFORE them (a grid holding
    ///   a line at 50000 got 10000, not 60000).
    ///
    /// WHAT BC'S CLIENT COMPUTES
    ///   <c>AutoKeyGenerator.CalculateNumericKeyValue</c> is
    ///   <c>rangeStart + (draftRowsBefore + 1) * 10000</c>, where <c>rangeStart</c> is the key of
    ///   the nearest NON-draft row before the insertion point (0 when there is none) and
    ///   <c>draftRowsBefore</c> counts the unsaved rows between the two.
    ///
    /// WHY AN EMPTY GRID STARTS AT 20000 AND NOT 10000
    ///   Because <c>draftRowsBefore</c> is 1 there, not 0. An insertable repeater always carries a
    ///   trailing blank row past its data — <c>DraftLinePattern.MakeDraftLines</c> adds one as soon
    ///   as the binding manager is filled, including when it filled with nothing — and
    ///   <c>TestPageProxy.InsertEmptyRow</c> inserts the test's row AFTER the current one
    ///   (<c>InsertBehavior = RowUpdateBehavior.After</c>, whatever <c>beforeCurrent</c> says). On
    ///   an empty grid the current row is that placeholder, so the test's first row is the SECOND
    ///   draft and takes the second interval: 0 + 2 * 10000. The placeholder itself is never
    ///   persisted — nothing edits it — which is why no row at 10000 ever appears. On a grid that
    ///   already has data the current row is a real one, the placeholder sits after the new row,
    ///   and the count is 0: last + 1 * 10000. Both are measured on real BC 27.5 and 28.3 by
    ///   corpus CU60922.
    ///
    /// THE RUNNER'S INSERTION POINT
    ///   The row the cursor sits on when New() is called, read by
    ///   <see cref="CaptureInsertPosition"/> before NewRecord wipes it: <c>rangeStart</c> is
    ///   that row's key (the last row of the filtered set when the page holds no cursor),
    ///   <c>rangeEnd</c> is the next row of the same parent when the insert lands mid-grid,
    ///   and the placeholder draft is counted where the measurements put it — BEFORE the
    ///   insert on an empty grid (the 20000), AFTER it when the insert is at the end of a
    ///   non-empty rowset. That last count is load-bearing and was measured, not derived: a
    ///   grid holding one line at -10000 numbers the next row -6667 on real BC 27.5/28.3
    ///   (corpus CU60929) — the range up to zero split in THREE, the trailing placeholder
    ///   taking the third share. Mid-grid the placeholder sits beyond <c>rangeEnd</c> and
    ///   does not participate, which the measured -1 for a -10000..10000 insert pins.
    /// </summary>
    private void ProposeAutoSplitKey()
    {
        if (_page == null || !_page.NeedsAutoSplitKey) return;
        _page.SetAutoKeyValue(ClientAutoKeyValue());
    }

    // The insert position CaptureInsertPosition read at New() time, consumed at flush time.
    // Null bounds are meaningful (no saved row on that side), so a separate flag records
    // whether a capture happened at all.
    private object? _insertRangeStart;
    private object? _insertRangeEnd;
    private int _insertDraftRowsBefore;
    private int _insertDraftRowsAfter;
    private bool _insertPositionCaptured;

    /// <summary>
    /// Read the rows around the insertion point — the client half of AutoSplitKey that must
    /// run at New() time, because NewRecord's ALInit erases the cursor row it reads.
    /// </summary>
    private void CaptureInsertPosition()
    {
        _insertPositionCaptured = false;
        if (_page == null || !_page.NeedsAutoSplitKey) return;
        // Non-null: only reached from InsertEmptyRow, which refuses by name first when the
        // page has no record — see RequireRecord there.
        var record = _record!;
        // The AutoSplitKey field is the LAST field of the primary key — BC picks it the same
        // way inside SplitKey, so a page whose key shape the runner read differently would
        // number a different field than BC validates.
        var primaryKey = record.MetaTable?.PrimaryKey;
        if (primaryKey == null || primaryKey.KeyFieldCount == 0) return;
        var keyFieldNo = primaryKey.KeyFieldsList[primaryKey.KeyFieldCount - 1].FieldNo;

        _insertRangeStart = null;
        _insertRangeEnd = null;
        _insertDraftRowsBefore = 0;
        _insertDraftRowsAfter = 0;

        // Cloned with reset:false so it carries the page's filters (a subpage part's
        // SubPageLink above all: without it this would walk the lines of SOME OTHER header)
        // and cannot disturb the cursor the page is on.
        using var probe = record.CloneRecord(record.Parent, reset: false, keepCompany: true);

        // "The cursor sits on a saved row" is decided the way SplitKey itself decides it — a
        // row with the cursor's ALRecordId exists. With no cursor row the client viewport's
        // insert goes after the LAST row of the set (BC's own ALFindLast over the page's
        // filters); with no rows at all the grid is empty.
        var positioned = probe.ExistsAsync(probe.ALRecordId).AsTask().GetAwaiter().GetResult()
            || probe.ALFindLastAsync(DataError.TrapError).GetAwaiter().GetResult();
        if (positioned)
        {
            _insertRangeStart = Unwrap(probe.GetFieldValue(keyFieldNo));
            _insertRangeEnd = NextRowKeyInSequence();
            // At the end of the rowset the trailing blank placeholder row sits AFTER the
            // insert and shares the range; mid-grid it sits beyond rangeEnd and does not.
            // Measured, not derived: -6667 (not -5000) after a single line at -10000.
            _insertDraftRowsAfter = _insertRangeEnd == null ? 1 : 0;
        }
        else
        {
            // Empty grid: the placeholder is the row the insert lands AFTER, so it burns the
            // first interval — the measured 20000 for a first line (corpus CU60922).
            _insertDraftRowsBefore = 1;
        }
        _insertPositionCaptured = true;

        // The next row of the SAME parent, or null when the cursor row ends its sequence —
        // the prefix-compare mirror of NavForm.IsPositionedAtEndOfSequence: iteration is
        // unfiltered primary-key order, so "next row belongs to another parent" shows as its
        // other key fields changing.
        object? NextRowKeyInSequence()
        {
            var prefix = new object?[primaryKey.KeyFieldCount - 1];
            for (var i = 0; i < prefix.Length; i++)
                prefix[i] = Unwrap(probe.GetFieldValue(primaryKey.KeyFieldsList[i].FieldNo));
            if (probe.ALNext() <= 0) return null;
            for (var i = 0; i < prefix.Length; i++)
                if (!Equals(Unwrap(probe.GetFieldValue(primaryKey.KeyFieldsList[i].FieldNo)), prefix[i]))
                    return null;
            return Unwrap(probe.GetFieldValue(keyFieldNo));
        }
    }

    private object? ClientAutoKeyValue()
    {
        if (!_insertPositionCaptured) return null;
        _insertPositionCaptured = false;
        // Non-null: only reached from ProposeAutoSplitKey/FlushPendingNewRow, both gated by
        // _pendingNewRow, which is only set by InsertEmptyRow after its RequireRecord guard.
        var record = _record!;
        var primaryKey = record.MetaTable?.PrimaryKey;
        if (primaryKey == null || primaryKey.KeyFieldCount == 0) return null;
        var keyFieldNo = primaryKey.KeyFieldsList[primaryKey.KeyFieldCount - 1].FieldNo;

        // The key field's CLR type steers the arithmetic, read off the freshly initialised
        // buffer so the proposal is typed like the field: SplitKey feeds it to
        // NavValue.CreateNavValueFromObject, which converts per the field's NCL type, and an
        // Int32 offered for a BigInteger or Decimal key is a different value than BC's
        // client would have sent.
        var draftRowCount = _insertDraftRowsBefore + 1 + _insertDraftRowsAfter;
        return Unwrap(record.GetFieldValue(keyFieldNo)) switch
        {
            int => Box(CalculateClientAutoKey<int>(
                (int?)_insertRangeStart, (int?)_insertRangeEnd, draftRowCount, _insertDraftRowsBefore)),
            long => Box(CalculateClientAutoKey<long>(
                (long?)_insertRangeStart, (long?)_insertRangeEnd, draftRowCount, _insertDraftRowsBefore)),
            decimal => Box(CalculateClientAutoKey<decimal>(
                (decimal?)_insertRangeStart, (decimal?)_insertRangeEnd, draftRowCount, _insertDraftRowsBefore)),
            // GUID: BC's client and SplitKey both just mint a fresh Guid, so no proposal adds
            // nothing. Unsupported key types: SplitKey must be the one to throw, so the AL
            // sees BC's message.
            _ => null,
        };

        static object? Box<T>(T? value) where T : struct => value.HasValue ? value.Value : null;
    }

    /// <summary>
    /// Verbatim port of the client's <c>AutoKeyGenerator.CalculateNumericKeyValue</c>
    /// (Microsoft.Dynamics.Nav.Client.UI.dll) — the algorithm that decides what number a new
    /// grid row gets on a real service tier. Ported rather than invoked because constructing
    /// the real generator needs a live client ColumnBinder; the arithmetic itself is
    /// self-contained. Adjudicated against real BC 27.5/28.3 by corpus CU60922 and CU60929:
    /// append, empty-grid, wide-gap cap, zero-crossing and the placeholder-in-the-divisor
    /// cases are all pinned by measurement.
    ///
    /// Null means "no proposal", which is a real answer and not a failure: the client raises
    /// AutoKeyException there (key space exhausted, overflow), and SplitKey's own bound
    /// arithmetic answers instead.
    /// </summary>
    private static T? CalculateClientAutoKey<T>(
        T? rangeStart, T? rangeEnd, int draftRowCount, int index)
        where T : struct, System.Numerics.INumber<T>
    {
        var hasStart = rangeStart.HasValue;
        var hasEnd = rangeEnd.HasValue;
        var isDecimal = typeof(T) == typeof(decimal);
        checked
        {
            try
            {
                var inc = T.CreateChecked(AutoSplitKeyIncrement);
                if (!hasStart && !hasEnd)
                    return Step(T.Zero, inc, false);
                if (hasStart && !hasEnd && rangeStart!.Value >= T.Zero)
                    return Step(rangeStart.Value, inc, false);
                if (hasEnd && !hasStart && rangeEnd!.Value <= T.Zero)
                    return Step(rangeEnd.Value, -inc, false);

                var slots = T.CreateChecked(draftRowCount + 1);
                var lowerBound = hasStart ? rangeStart!.Value : T.Min(T.Zero, rangeEnd!.Value - slots);
                var upperBound = hasEnd ? rangeEnd!.Value : T.Max(T.Zero, rangeStart!.Value + slots);
                if (lowerBound >= upperBound) return null;
                var crossesZero = lowerBound < T.Zero && upperBound > T.Zero;
                if (!isDecimal && crossesZero)
                {
                    var negRoom = T.Zero - lowerBound;
                    var posRoom = upperBound - T.Zero;
                    if (negRoom >= slots && hasStart && !hasEnd)
                        upperBound = T.Zero;
                    else if (posRoom >= slots && hasEnd && !hasStart)
                        lowerBound = T.Zero;
                    else
                    {
                        var range = upperBound - lowerBound;
                        if (range < slots + T.One)
                        {
                            if (!hasStart)
                                lowerBound -= range - upperBound;
                            else
                            {
                                if (hasEnd) return null;
                                upperBound += range + lowerBound;
                            }
                        }
                    }
                }
                var delta = T.Min(
                    (upperBound - lowerBound - ((crossesZero && !isDecimal) ? T.One : T.Zero)) / slots,
                    inc);
                if (!isDecimal && delta < T.One) return null;
                if (delta <= T.Zero) return null;
                return Step(lowerBound, delta, crossesZero);
            }
            catch (OverflowException)
            {
                return null;
            }

            T Step(T lowerBound, T delta, bool compensateForZero)
            {
                var value = lowerBound + T.CreateChecked(index + 1) * delta;
                if (compensateForZero)
                {
                    if (isDecimal && value == T.Zero)
                        value -= delta / T.CreateChecked(2);
                    else if (!isDecimal && value >= T.Zero)
                        value += T.One;
                }
                return value;
            }
        }
    }

    // The same client model as _pendingNewRow, for the other half of editing: a SetValue on an
    // EXISTING row writes into the record buffer, and the row is persisted when the cursor
    // leaves it or the page closes.
    //
    // Without this, every edit a TestPage made to an existing row was silently discarded. That
    // is worse than it sounds: the page keeps answering with the value that was set, so a test
    // that writes a field and reads it back through the page PASSES, and only a test that goes
    // to the table notices. Tests of the first shape were green while asserting nothing.
    private bool _pendingModify;

    /// <summary>
    /// A control is ABOUT to write to the record. Called by the field before it validates —
    /// which is the only moment at which the implicit new-row line can still be turned into
    /// the row BC would have started.
    ///
    /// <para>Typing into the draft line is what creates a record on a repeater, and the
    /// platform step that creates it is the SAME one <c>New()</c> runs:
    /// <c>NavForm.NewRecordAsync</c>, which resets the buffer, copies the page's single-valued
    /// filters onto the primary-key fields (<c>RecordImplementation.InitRecordFromFilters</c>)
    /// and raises OnNewRecord. So the promotion goes through <see cref="InsertEmptyRow"/>,
    /// the same entry point <c>New()</c> uses — including
    /// <see cref="LiveNavTestPart.InsertEmptyRow"/>'s SubPageLink stamping when the page is a
    /// linked part.</para>
    ///
    /// <para>WHY BEFORE THE VALIDATE, NOT AFTER (issue #2923). <c>MarkEdited</c> below runs
    /// after the control's write, and it used to be the whole promotion: it flipped
    /// <c>_pendingNewRow</c> and left the buffer exactly as <see cref="EnterNewRowLine"/> had
    /// blanked it — key fields cleared, link values sitting unread in the record's filters.
    /// The typed field's own OnValidate therefore ran against a row with no key. On a linked
    /// document part that is fatal rather than cosmetic: <c>Sales Line</c>'s first OnValidate
    /// reaches <c>TestStatusOpen</c> → <c>GetSalesHeader</c> → <c>TestField("Document No.")</c>
    /// and raises "Document No. must have a value" — 35 tests of Microsoft's Tests-SMB bucket,
    /// on the commonest shape in BC test code (<c>SalesQuote.SalesLines.First()</c> on an empty
    /// part, then <c>SetValue</c>).</para>
    ///
    /// <para>Reading the draft line still answers blank, including in the column a SubPageLink
    /// constrains — nothing here runs until a WRITE arrives. Both halves are measured upstream
    /// on real BC (corpus codeunit 60996 "TPDL Tests",
    /// StefanMaron/BusinessCentral.AL.Language.Tests): the draft line of a linked part reads
    /// blank in the linked column, and the row a write starts on it carries the link's value
    /// early enough that the typed field's OnValidate already sees it.</para>
    /// </summary>
    internal void PromoteNewRowLineForWrite()
    {
        if (!_onNewRowLine) return;
        // beforeCurrent: false — the draft line is the LAST row of the rowset, so the row it
        // becomes is inserted after the data, which is also what BC's own TestPageProxy asks
        // for (InsertBehavior = RowUpdateBehavior.After, whatever beforeCurrent says).
        //
        // alreadyStarted: EnterNewRowLine ran the platform's new-record step when the cursor
        // landed on this line — that is the measured BC behaviour (corpus codeunit 60996,
        // 8 legs). Typing does not start the row a second time; it decides that the row already
        // started will be SAVED. Passing false here raised the page's OnNewRecord twice for one
        // row and re-blanked the buffer under the trigger's own output (#3029).
        //
        // Virtual on purpose: a part must reach LiveNavTestPart's override, whose SubPageLink
        // stamping and validate step are still owed on this path.
        InsertEmptyRow(beforeCurrent: false);
    }

    /// <summary>A control wrote to the record. Called by the field, which owns no page state.</summary>
    internal void MarkEdited()
    {
        // The new-row line is normally already gone by the time this runs — the field calls
        // PromoteNewRowLineForWrite() before validating, and that turns the draft line into a
        // pending insert. This branch stays for any write that reaches the record without
        // going through a LiveNavTestField setter: the row still has to become an insert
        // rather than a Modify of a row that is not in the table. It does NOT do the
        // link-stamping half — a write that never announced itself cannot be given one — so
        // the two paths are not equivalent and the pre-write call above is the one that
        // matters.
        if (_onNewRowLine)
        {
            _onNewRowLine = false;
            _newRowLineReturnPosition = null;
            _pendingNewRow = true;
            return;
        }

        // A new row is already going to be written by FlushPendingNewRow; marking it modified
        // as well would try to Modify a row that does not exist yet.
        if (!_pendingNewRow) { _pendingModify = true; return; }

        InsertOnCompletePrimaryKey();
    }

    /// <summary>
    /// Write a page-driven insert as soon as the row's PRIMARY KEY is complete, rather than
    /// holding it back until the page is left — issue #3441.
    ///
    /// Measured on real BC 28.4 and adjudicated on eight cloud legs (corpus codeunit 60636
    /// <c>NewAndInsertRecordEvents_PageDrivenInsert_FireForTheKeyOnly</c>): typing the key of a
    /// new row on a <c>DelayedInsert = false</c> list inserts the row THERE, so
    /// <c>OnInsertRecord</c> and <c>OnInsertRecordEvent</c> see the key set and every later
    /// control still blank, and the next control write is an ordinary page-driven MODIFY with
    /// its own trigger and event. The runner deferred the whole thing to the flush, so the
    /// insert trigger saw a finished row and the modify never happened at all.
    ///
    /// <para>Three limits. A page that saves one RECORD rather than rows keeps the
    /// flush-on-leave timing — see RunnerPageInstance.WritesRowsAsTheyAreCompleted, where both
    /// directions are measured. <c>DelayedInsert = true</c> keeps it too, which is that
    /// property's own definition. And "complete" is BC's own emptiness test —
    /// <c>NavValue.IsZeroOrEmpty</c>, what <c>NavForm.SplitKey</c> uses on the last key field —
    /// so a page whose last key field is filled in BY <c>AutoSplitKey</c> reads as incomplete
    /// while that field is still 0 and keeps the deferred path. Every editable line grid in BC
    /// is that shape, and moving them would change insert timing for the draft-line tests
    /// (corpus 60358/60648) on no evidence.</para>
    /// </summary>
    private void InsertOnCompletePrimaryKey()
    {
        // No page: record-only mode has no DelayedInsert property to read and no page triggers
        // to get the timing wrong, so it keeps the flush-time insert.
        if (_page == null || _page.DelaysInsertUntilTheRowIsLeft) return;
        // A Card saves its one record when the page is left, not when its key is typed — corpus
        // codeunit 60844 Close_WithoutOK_StillPersistsTheNewRow asserts the row is absent right
        // up to Close(), and says in its own message that it is there to catch an eager insert.
        if (!_page.WritesRowsAsTheyAreCompleted) return;
        if (!PrimaryKeyIsComplete(_record!)) return;
        // The same call the flush points make, so the insert keeps BC's order — the write gate,
        // SplitKey, OnInsertRecord's veto, then the record's own Insert — and clears
        // _pendingNewRow, which is what makes the NEXT control write a Modify.
        FlushPendingNewRow();
    }

    /// <summary>Every primary-key field holds a value. <c>NavValue.IsZeroOrEmpty</c> is BC's own
    /// spelling of "this key field has not been filled in" — <c>NavForm.SplitKey</c> tests the
    /// last key field with it before computing an AutoSplitKey value.</summary>
    private static bool PrimaryKeyIsComplete(NavRecord record)
    {
        var primaryKey = record.MetaTable?.PrimaryKey;
        if (primaryKey == null || primaryKey.KeyFieldCount == 0) return false;
        for (var i = 0; i < primaryKey.KeyFieldCount; i++)
            if (record.GetFieldValue(primaryKey.KeyFieldsList[i].FieldNo).IsZeroOrEmpty)
                return false;
        return true;
    }

    internal void FlushPendingModify()
    {
        if (!_pendingModify) return;
        _pendingModify = false;

        // A row whose values did not actually MOVE is not written, so its OnModify does not run
        // (issue #3055). _pendingModify only records that a control assigned a field; BC's
        // SaveRecordAsync decides on the values themselves, and both arms of its gate are value
        // comparisons — CompareAllNormalFields against OldRecord, ORed with
        // RecordImplementation.HasChangedFields, which reaches
        // MutableRecordBuffer.HasActualChangedValues and returns false unless some modified
        // field fails IsChangedValueSameAsOriginalValue. See docs/testpage-write-gate.md.
        //
        // Before the veto, not after, because that is where BC puts it: SaveRecordAsync returns
        // without ever reaching RaiseOnModifyRecordAsync when the comparison finds nothing. A
        // page whose OnModifyRecord has a side effect must not get it for a write that is not
        // happening.
        if (!RowValuesChangedSinceLoad()) return;

        // OnModifyRecord vetoes exactly as OnInsertRecord does.
        if (_page != null && !_page.RaiseOnModifyRecord()) return;

        // Non-null: _pendingModify is only ever set by MarkEdited, which is only wired to a
        // LiveNavTestField — a Rec-bound control, which cannot exist unless the page has a
        // record (RecordPatches.GetPageControlFieldMap returns empty for a page with no
        // SourceTable). A page-variable-bound field (PageVariableTestField) never calls it.
        var record = _record!;

        // SystemModifiedAt/By are stamped by a Cecil prepend on NavRecord.ALModifyAsync — the
        // CODE-driven entry point this method deliberately does NOT use (see below). Real BC
        // stamps them in the data layer, so they move on a page write too; call the same helper
        // the prepend calls so switching entry points does not silently freeze them.
        BcRuntime.StampSystemFieldsOnModify(record);

        // ModifyAsync, NOT ALModifyAsync — and the difference is the whole xRec contract.
        //
        //   NavRecord.ALModifyAsync  (what AL `Rec.Modify()` lowers to) opens with
        //       OldRecord.ALAssign(this)
        //   before delegating to ModifyAsync, so a code-driven Modify deliberately makes xRec
        //   MIRROR Rec — there is no before-image on that path (corpus CU60179
        //   OnModify_xRec_MirrorsRecValues_WhenCalledFromCode pins exactly that).
        //
        //   NavForm.SaveRecordAsync — BC's own page-write path — skips that assignment and calls
        //       SafeSourceTable.ModifyAsync(DataError.ThrowError, runApplicationTrigger: true,
        //                                   runGlobalTrigger: true)
        //   directly, precisely so the before-image the form snapshotted when it loaded the row
        //   (SnapshotBeforeImage below) survives into the table's OnModify. That is why a
        //   PAGE-driven Modify sees the PREVIOUS value in xRec (corpus CU60235
        //   Record_Modify_FromPage_xRecHoldsPreviousValue).
        //
        // Same three arguments BC passes, for the same reasons: ThrowError, because a Modify
        // that cannot be performed is something the user of a real client would be told about —
        // trapping it turned "this page is not positioned on a row" into an edit that appeared
        // to succeed and quietly went nowhere; and both trigger flags on, because a page write
        // runs the table's OnModify and the global-trigger hook exactly like Rec.Modify(true).
        record.ModifyAsync(DataError.ThrowError, true, true).GetAwaiter().GetResult();

        // The write has landed, so the row IS the before-image from here on. BC gets this from
        // the client: SaveRecordAsync ends by raising UpdateRequest(RecordSaved), the client
        // re-reads, and AfterGetCurrRecordAsync's tail assigns OldRecord. This method is the
        // runner's own write path and never reaches SaveRecordAsync, so it takes the snapshot
        // itself — see RunnerPageInstance.RefreshBeforeImageAfterSave for the other half, which
        // covers CurrPage.SaveRecord()/Update(true). Without both, a second write in one page
        // session reported the value from before the FIRST write as its xRec (issue #3440), and
        // the RowValuesChangedSinceLoad gate above measured that same stale row.
        SnapshotBeforeImage();
    }

    // Order matters at every flush point: an in-progress new row is finished by an Insert, an
    // edited existing row by a Modify, and only one of the two is ever pending.
    private void FlushRow() { FlushPendingNewRow(); FlushPendingModify(); }

    /// <summary>
    /// Persist whatever row the page is in the middle of editing — BC's NavForm.SaveRecord,
    /// the "the cursor is leaving this row" step.
    ///
    /// Every OTHER leave-the-row moment in this class already does this (the four cursor
    /// moves, Close, Dispose, the built-in OK action); invoking a page ACTION is the one that
    /// did not, and it is the moment BC's client is most obviously at: the client sends the
    /// edited row to the server before it runs the action, which is why an AL action reads
    /// <c>Rec</c> as a row that exists. Without it the action ran against a row that was still
    /// only a buffer — its AutoSplitKey field unassigned and no row of its own in the table —
    /// so an OnAction that looked the row up, or passed its key to a posting routine, silently
    /// found nothing.
    /// </summary>
    internal void SaveCurrentRow() { FlushParts(); FlushRow(); }

    // BC routes TestPage teardown through both Close() and Dispose() depending on whether
    // the AL test calls Close() explicitly or lets the variable go out of scope. Flush on
    // both so a New() is never silently discarded.
    //
    // Parts flush with their host: an AL test closes the CARD, never the part, so a row
    // started with Card.Lines.New() has no other moment at which it could be persisted.
    public override void Close()
    {
        // A torn-down page (see _tornDown / Loaded()) raises "The TestPage is not open."
        // instead of closing -- measured on real BC, Close() does NOT silently no-op here.
        if (_tornDown) throw MakeTestPageNotOpenException();

        // The EXPLICIT close route's flush, BEFORE the trigger. BC's client sends the row being
        // edited -- INCLUDING a row typed into a part -- and only then drives the close, so
        // OnQueryClosePage reads a part that already holds it. Measured on a real service tier
        // for this route: corpus codeunit 60438 "Opc Close Part Flush Tests"
        // (StefanMaron/BusinessCentral.AL.Language.Tests#320), the TestPage.Close() twin of
        // codeunit 60663's OK-press arms (#315). Issue #3708.
        //
        // Order is parts THEN row here, as at every other close point, because a part's
        // OnValidate can touch the header; the OK route is row-then-parts only because
        // Invoke() has already written this page's own row (#3701).
        //
        // It runs before the refusal branches below on purpose: BC's send-then-close order does
        // not depend on what the trigger answers, and the modal route already flushes ahead of
        // its own raise (AttemptHandlerDrivenClose). No service tier has been asked what a
        // refused close leaves behind on this route, and no test here asserts it either way.
        //
        // The modal route does not come through here at all -- it reaches Dispose() instead --
        // and the two ROUTES nevertheless agree about an uncommitted subpage-part row, because
        // both end in a flush: corpus codeunit 60420 "TPMF Tests"
        // (StefanMaron/BusinessCentral.AL.Language.Tests#311, merged 22e226c4) drives one page
        // through both routes and both persist the row, green on all eight cloud legs of run
        // 34345468218. So neither call site may lose its flush; issue #3682.
        FlushParts(); FlushRow();

        // Two ways BC refuses a close, and they are not the same question — see
        // RunnerPageInstance.CloseRefusal.
        if (_page != null && !_page.RaiseOnClosePage(_formResult, out var refusal))
        {
            // An AL error the trigger raised, consumed by a declared [MessageHandler]. MEASURED
            // on a real service tier (corpus codeunit 60602 "QCM Query Close Msg Tests",
            // StefanMaron/BusinessCentral.AL.Language.Tests#272, green on all eight cloud legs
            // and on the Windows nightly): Close() returns normally, the message reaches the
            // handler exactly once on this route, and the page is left OPEN.
            //
            // Returning here is the whole of that: the tear-down below is skipped, so _opened
            // stays true and BC's own form state is untouched
            // — the test's TestPage variable keeps working, which is what a real tier leaves it
            // holding. It is deliberately NOT a refusal any more; raising one here would be the
            // runner erroring on a path BC completes without an error (issue #3179).
            if (refusal == RunnerPageInstance.CloseRefusal.ErrorShownAsMessage) return;

            // A plain veto (the trigger returned false) on the EXPLICIT TestPage.Close() path.
            // Still a refusal, and still a permanent scope boundary (#2999 lists it among the
            // fourteen): BC leaves the page open awaiting a user, and unlike the arm above no
            // service tier has been asked what a test observes afterwards. A [TryFunction]
            // reading false is BC's outcome.
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                // No " — " in the api — see RequireRecord.
                $"TestPage page {_pageId} (OnQueryClosePage)",
                "testpage-close-veto — the page's OnQueryClosePage returned false, which in BC "
                + "leaves the page open awaiting the user. See docs/scope.md");
        }
        _opened = false;

        // The triggers above are this page's close, so BC's own form state has to agree that
        // it happened — otherwise IsOpen stays true and whoever else is holding the form runs
        // the close a second time. ForceClose raises nothing, which is exactly right here:
        // the triggers have already run once (issue #3091).
        _page?.ForceCloseForm();
    }
    // The MODAL route's flush, and the only one it has. A [ModalPageHandler] never calls
    // Close(): BC wraps the handler in a scope and disposes the page handle as the refcount
    // drops, which lands here -- NavTestExecution.TestHandleModalForm -> NavTestPageHandle
    // .Dispose -> TreeObjectReferenceHandler.Dispose -> NavTestPage.Dispose -> this.
    //
    // Equivalent to what BC does, and measured rather than assumed: corpus codeunit 60420
    // "TPMF Tests" (StefanMaron/BusinessCentral.AL.Language.Tests#311, merged 22e226c4) asserts
    // the two close routes AGREE about an uncommitted part row, green on all eight cloud legs
    // of run 34345468218. Its arms F/G/H invoke no action precisely so the ACTION write-back
    // (OK().Invoke() -> SaveCurrentRow()) cannot stand in for the close.
    //
    // The trap, and why TestPageModalClosePartFlushTests exists: these two calls are REDUNDANT
    // for a part row here -- FlushParts() reaches each part's FlushRow(), and each part page's
    // own Dispose() calls its own FlushRow() -- so removing EITHER one alone leaves every
    // behavioural test green. Only removing both goes red. The IL guard in that file is what
    // fails on a single-call edit. Issue #3682.
    public override void Dispose() { FlushParts(); FlushRow(); }

    /// <summary>
    /// The close attempt a built-in OK/LookupOK invoked from a <c>[ModalPageHandler]</c> /
    /// <c>[PageHandler]</c> makes, which the runner did not make at all before #3593.
    ///
    /// <para>On BC the handler's <c>OK().Invoke()</c> is a CLIENT action: pressing OK drives the
    /// logical form's close, so <c>OnQueryClosePage</c> is raised right there, before the round
    /// trip that opened the page gets control back. The runner's <c>Invoke()</c> only recorded a
    /// result, so the ONLY close attempt on this route was the one
    /// <see cref="AlRunner.Patches.RunnerModalDispatch.FormRunModal"/> makes afterwards.</para>
    ///
    /// <para>The observable consequence, and the reason this is a defect rather than an internal
    /// detail: a close BC REFUSES is attempted twice, so an <c>OnQueryClosePage</c> that raises
    /// an AL error consumed by a <c>[MessageHandler]</c> delivers that message TWICE on the
    /// RunModal route. Measured on a real service tier by corpus codeunit 60602 "QCM Query Close
    /// Msg Tests" (StefanMaron/BusinessCentral.AL.Language.Tests#272, merged bd168356), green on
    /// all eight cloud legs and confirmed by the Windows nightly reference tier, whose modal arms
    /// assert a delivery count of 2 while its TestPage arm asserts 1.</para>
    ///
    /// <para>ONE mechanism produces both counts, which is why this is not a counter. A successful
    /// attempt here CLOSES the form, so <c>FormRunModal</c>'s own <c>IsFormOpen</c> gate skips its
    /// attempt and the trigger is raised exactly once -- the result corpus codeunit 60276 "MQC
    /// Tests" measured for an allowed close, and the one the runner already matched. A REFUSED
    /// attempt leaves the form open, so that gate lets the second attempt through and the message
    /// is delivered twice. Delivering twice unconditionally would break the allowed-close case.</para>
    ///
    /// <para>Restricted to a page the TEST DID NOT OPEN (<c>!_opened</c>, written only by
    /// <see cref="MarkOpened"/>). A page the test opened itself is the test's to close: BC's
    /// client does not press its OK button, and <c>Card.OpenNew(); Card.OK().Invoke();</c>
    /// followed by further calls on the same variable is ordinary AL that must keep working.
    /// That route's close attempt is <see cref="Close"/>, which is unchanged.</para>
    ///
    /// <para>A refusal is SWALLOWED here rather than raised, and that is the faithful answer, not
    /// a convenience: BC's own close handler returns "close refused" to the client without
    /// raising anything the handler can see (the message has already been shown), and the handler
    /// carries on to its own end. What the caller of <c>RunModal()</c> observes is then decided by
    /// <c>FormRunModal</c>'s second attempt, which reaches the same refusal and drops the
    /// handler's result -- so <c>Action::None</c> still comes out of the refused path, unchanged.
    /// The one thing that must NOT be swallowed is a refusal whose message had nowhere to go:
    /// with no <c>[MessageHandler]</c> declared, BC's own "Unhandled UI: Message …" comes out of
    /// <see cref="AlRunner.Patches.RunnerFormCloseHandler"/> rather than being returned, and that
    /// is a real test failure which propagates.</para>
    /// </summary>
    private void AttemptHandlerDrivenClose(FormResult result)
    {
        // The test opened this page itself, so closing it is the test's call, not the client's.
        if (_opened) return;

        // Nothing to raise a trigger on, or AL already closed the page from under the handler
        // (CurrPage.Close() from an OnAction) -- in which case the close has happened and its
        // triggers have run exactly once already (issue #3091).
        if (_page == null || _tornDown || RunnerPageInstance.WasClosedFromAl(_page.Form)) return;

        // Only the CONFIRMING built-ins close the page on BC. A Cancel that reached here would
        // be a second question -- what a cancelled modal's close attempt does -- which no tier
        // has been asked, so it keeps the behaviour it had.
        if (result is not (FormResult.OK or FormResult.LookupOK)) return;

        // BC's client sends the row being edited -- INCLUDING a row typed into a part -- before
        // it drives the close, so OnQueryClosePage reads a part that already holds it. Invoke()
        // above flushes only this page's own row, so without this the trigger sees a part one
        // row short and a page that materialises its part contents on OK saves nothing (#3701).
        // Measured on a real service tier: corpus codeunit 60663 "Opf Ok Part Flush Tests"
        // (StefanMaron/BusinessCentral.AL.Language.Tests#315).
        //
        // Order here is row THEN parts: Invoke() above has already flushed this page's own row.
        // Close()/Dispose()/SaveCurrentRow() are parts-then-row because a part's OnValidate can
        // touch the header. The repeat pass is a no-op -- FlushPendingNewRow/FlushPendingModify
        // clear their flag on entry -- so nothing is written twice. Do not delete Invoke()'s
        // FlushRow() on the strength of this call: FlushParts() does not write the host row, and
        // no test here would catch its loss.
        FlushParts();

        // Both refusals leave the form OPEN and raise nothing here, which is what makes
        // FormRunModal's own attempt run -- and that second attempt is where the second message
        // delivery, and the Action::None, come from. They are one branch on purpose: unlike
        // Close(), which must tell them apart because a veto there is a scope boundary
        // (testpage-close-veto), this route's observable outcome is produced downstream either
        // way, and it is the outcome the route already had before #3593.
        if (!_page.RaiseOnClosePage(result, out _)) return;

        // The close succeeded, so BC's own form state has to agree -- otherwise IsOpen stays
        // true and FormRunModal runs the whole sequence a second time, which is exactly the
        // double-raise issue #3091 fixed. ForceClose raises nothing: the triggers have just run.
        _opened = false;
        _page.ForceCloseForm();
    }

    private void FlushParts()
    {
        foreach (var part in _parts.Values)
            if (part is LiveNavTestPage live) live.FlushRow();
    }
}
