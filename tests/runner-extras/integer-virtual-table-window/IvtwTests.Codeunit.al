// Issues #2350, #3376 and #3438 — the runner-specific half of the Integer system virtual table
// (2000000026).
//
// On the service tier the table is computed per request by IntegerDataProvider, which is a
// RangeBasedComputedDataProvider. Decompiled from Ncl.dll, and byte-identical in 27.0 and 28.4:
//
//     protected override IEnumerable<ReadOnlyRecordBuffer> GetValuesWithinRangeForKeyField(...)
//     {
//         if (range.GetInclusiveIntegerBounds(-1000000000, 1000000000, out var low, out var high))
//             for (int thisKey = ...; thisKey >= low && thisKey <= high; ...)
//                 yield return CreateVirtualRecordBuffer(...);
//     }
//
// So real BC is NOT unbounded either: it clamps every request to [-1e9..1e9], and an unbounded
// filter is served as exactly that range rather than refused. CountValuesWithinRange applies the
// same two constants. That is the behaviour codeunit 60368 pins upstream, against a service tier.
//
// The runner serves the table from an in-memory store, so it has to materialise rows. Since #3438
// it materialises them PER REQUEST: a filter closed at both ends materialises exactly the span it
// names, so Number 250000 and -250000 are ordinary rows here too. What the rows themselves say is
// plain BC behaviour and lives upstream in the al-language corpus (codeunit 60368); none of it is
// repeated here.
//
// Two things are left that exist only because this is the runner, and this suite is those two:
//
//   * a span that would push the store past AL_RUNNER_INTEGER_WINDOW_MAX_ROWS is refused by name,
//     on every request path, instead of being answered with however many rows the store holds;
//   * an OPEN bound is answered from the base window [-1000..100000] rather than from BC's ±1e9,
//     because 2,000,000,001 rows cannot be materialised — and when a half-open filter's closed end
//     lies outside that base window, the honest answer is a refusal rather than zero rows.

codeunit 64591 "Ivtw Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Ivtw Assert";

    [Test]
    procedure Integer_SpanPastTheRowCap_ThrowsOutOfScopeOnTheFindPath()
    var
        IntRec: Record Integer;
    begin
        // [GIVEN] a closed range of 900,000 rows: far inside the ±1e9 a service tier serves, and
        // past the 500,000-row cap on what the runner will materialise.
        IntRec.SetRange(Number, 1, 900000);

        // [THEN] the runner refuses by name rather than answering from what it happens to hold.
        // Answering would return a short count for a request BC answers in full, and the caller
        // reads the short number as the real one.
        asserterror IntRec.FindSet();
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('integer-virtual-table');
        Assert.ExpectedError('row cap');
    end;

    [Test]
    procedure Integer_SpanPastTheRowCap_ThrowsOnTheCountPathToo()
    var
        IntRec: Record Integer;
    begin
        // Count() builds a CountCacheRequest, not a FindCacheRequest, so the find guard never
        // sees it. Without a guard on this path Count() answers from the store — a number that
        // looks entirely real and is short.
        IntRec.SetRange(Number, 1, 900000);

        asserterror CountRows(IntRec);
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('integer-virtual-table');
        Assert.ExpectedError('row cap');
    end;

    [Test]
    procedure Integer_SpanPastTheRowCap_ThrowsOnTheIsEmptyPathToo()
    var
        IntRec: Record Integer;
    begin
        // IsEmpty() is a third request path: RecordImplementation.IsEmptyAsync builds an
        // ExistsCacheRequest and reaches DataAccess.ExistsAsync, never CountAsync (#3006 proved
        // exactly this for the Date table). Without a guard here IsEmpty() answers from the rows
        // the store happens to hold, which says nothing about the range asked for.
        IntRec.SetRange(Number, 1, 900000);

        asserterror IsEmptyRows(IntRec);
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('integer-virtual-table');
        Assert.ExpectedError('row cap');
    end;

    [Test]
    procedure Integer_HalfOpenFilterClosedFarAboveTheBaseWindow_ThrowsOutOfScope()
    var
        IntRec: Record Integer;
    begin
        // [GIVEN] `>=249000` — closed at one end, open at the other. BC substitutes 1e9 for the
        // open end and answers with 751,000,001 rows.
        //
        // [THEN] the runner cannot materialise an open bound: it answers one from the base window
        // [-1000..100000], which holds no row at or above 249000. Serving the request from that
        // window would report success with zero rows — the silent wrong answer this guard exists
        // to remove — so it refuses and names the window it could not answer from.
        IntRec.SetFilter(Number, '>=249000');

        asserterror IntRec.FindSet();
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('integer-virtual-table');
        Assert.ExpectedError('with its other end open');
    end;

    [Test]
    procedure Integer_HalfOpenFilterClosedFarBelowTheBaseWindow_ThrowsOutOfScope()
    var
        IntRec: Record Integer;
    begin
        // The mirror shape, and the one a check written only against the upper edge would miss:
        // `..-249000` is open at the LOW end and closed below the base window's floor.
        IntRec.SetFilter(Number, '..-249000');

        asserterror CountRows(IntRec);
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('integer-virtual-table');
        Assert.ExpectedError('with its other end open');
    end;

    [Test]
    procedure Integer_ClosedSpanPastTheBaseWindow_IsMaterialisedRatherThanRefused()
    var
        IntRec: Record Integer;
    begin
        // The control that separates the two refusals above from a guard that simply throws
        // whenever a bound leaves the base window — which is what the runner did before #3438,
        // and what these two tests would otherwise be satisfied by. A closed span outside the base
        // window and inside the cap is materialised on demand and answered in full.
        //
        // The rows themselves are BC's claim and are pinned upstream (codeunit 60368); what is
        // pinned here is that the runner reaches them by materialising rather than by widening a
        // fixed list, on a span that touches neither the base window nor the cap.
        IntRec.SetRange(Number, 400000, 400009);

        Assert.AreEqual(10, IntRec.Count(), 'Expected 10 rows for Number in [400000..400009].');
        Assert.IsFalse(IntRec.IsEmpty(), 'Number in [400000..400009] must not be empty.');
        Assert.IsTrue(IntRec.FindFirst(), 'Record Integer found no row for Number in [400000..400009].');
        Assert.AreEqual(400000, IntRec.Number, 'Expected the range to start at 400000.');
    end;

    [Test]
    procedure Integer_RangeInsideTheBaseWindow_StillAnswersNormally()
    var
        IntRec: Record Integer;
        Seen: Integer;
    begin
        // The control, and the reason this suite cannot pass with a guard that throws on every
        // Integer read. A range wholly inside the base window is answered, not refused, and the
        // rows are the real ones in the real order.
        IntRec.SetRange(Number, 10, 14);

        Assert.AreEqual(5, IntRec.Count(), 'Expected 5 rows for Number in [10..14].');
        Assert.IsFalse(IntRec.IsEmpty(), 'Number in [10..14] is not empty.');
        Assert.IsTrue(IntRec.FindSet(), 'Record Integer found no row for Number in [10..14].');
        Assert.AreEqual(10, IntRec.Number, 'Expected the range to start at 10.');

        repeat
            Seen += 1;
        until IntRec.Next() = 0;
        Assert.AreEqual(5, Seen, 'Expected to iterate exactly 5 rows.');
    end;

    [Test]
    procedure Integer_UnboundedFilter_IsServedFromWhatIsMaterialised_NotFromBcsOwnBound()
    var
        IntRec: Record Integer;
    begin
        // Issue #3376 asked for an unbounded filter to be REFUSED, and the decompile is why it
        // does not get that. BC invents a bound too - GetInclusiveIntegerBounds substitutes -1e9
        // for an open low and +1e9 for an open high - so refusing would diverge from a service
        // tier on a shape BaseApp uses constantly: 18 of its 658 reports declare MaxIteration
        // over an unbounded `dataitem(x; Integer)`.
        //
        // So an open bound is served, and this test pins WHERE it stops. It is the one shape
        // per-request materialising cannot follow: BC would enumerate to 1,000,000,000 and the
        // runner enumerates what it has materialised, which always covers the base window and
        // never reaches BC's bound. That divergence is recorded in docs/limitations.md.
        IntRec.SetFilter(Number, '>=1');

        Assert.IsTrue(IntRec.FindFirst(), 'An unbounded Integer filter must be served, not refused.');
        Assert.AreEqual(1, IntRec.Number, 'Expected the unbounded range to start at 1.');

        // Not merely "it did not throw": the enumeration reaches at least the base window's own
        // upper edge, so an open bound is answered from a populated store rather than an empty
        // one - and it stops short of BC's bound, which is the divergence this suite exists to
        // state rather than hide. The upper assertion is deliberately not an exact count: an
        // earlier request in the same process may have materialised further rows, which an open
        // filter then legitimately selects.
        Assert.IsTrue(IntRec.FindLast(), 'An unbounded Integer filter returned no last row.');
        Assert.IsTrue(IntRec.Number >= 100000, 'An open-ended range must reach the base window''s upper edge, 100000.');
        Assert.IsTrue(IntRec.Number < 1000000000, 'The runner stops at what it materialised; only real BC reaches 1000000000.');
    end;

    [Test]
    procedure Integer_KeyedGetAtTheBaseWindowEdges_StillAnswers()
    var
        IntRec: Record Integer;
    begin
        // The boundary control for the keyed-Get path. Both edges of the base window are
        // INCLUSIVE, so an off-by-one in what the handout materialises loses a row that every
        // other test here would still pass without.
        Assert.IsTrue(IntRec.Get(100000), 'Number 100000 is the base window''s upper edge and must be readable.');
        Assert.AreEqual(100000, IntRec.Number, 'Get(100000) returned a different row.');

        Assert.IsTrue(IntRec.Get(-1000), 'Number -1000 is the base window''s lower edge and must be readable.');
        Assert.AreEqual(-1000, IntRec.Number, 'Get(-1000) returned a different row.');
    end;

    [Test]
    procedure Integer_RowCapRefusal_TearsThroughATryFunction_InsteadOfReadingAsFalse()
    var
        IntRec: Record Integer;
        Reached: Boolean;
    begin
        // Issue #2965's rule, applied to this table. ApplicationObjectBasePatches.
        // IsPermanentOutOfScope reads the reason's FIRST token:
        //
        //     return oos != null && !oos.Reason.StartsWith("not-yet-implemented", ...);
        //
        // so a refusal anchored anywhere else is trapped by an AL [TryFunction] into `false` —
        // the silent default loud-failures.md exists to prevent. AL would carry on having quietly
        // done without the table and the test would go green. The row cap is a runner limit, not a
        // permanent scope boundary: a service tier answers this read.
        IntRec.SetRange(Number, 1, 900000);

        asserterror Reached := TryFindSet(IntRec);
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('integer-virtual-table');
        Assert.IsFalse(Reached, 'TryFindSet must not have completed.');
    end;

    [Test]
    procedure Integer_MultiRangeFilterWithAHighEndPastTheWindow_ThrowsOutOfScope()
    var
        IntRec: Record Integer;
    begin
        // Issue #3471. A filter may name several ranges, and the refusal above has to be decided
        // PER RANGE. `1..50|200000..` closes 1 and 50 and leaves the second range open at the top,
        // so the filter's outermost closed bounds are 1 and 50 — both inside the base window. Read
        // through those bounds alone the request looks answerable, and the base window serves it
        // with the 50 rows of the first range while real BC also returns 200000 and everything
        // above it (pinned upstream, codeunit 60368).
        //
        // Dropping a whole range is the same silent wrong answer the single-range refusal exists
        // to remove, so it is refused the same way.
        IntRec.SetFilter(Number, '1..50|200000..');

        asserterror IntRec.FindSet();
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('integer-virtual-table');
        Assert.ExpectedError('with its other end open');
        Assert.ExpectedError('200000');
    end;

    [Test]
    procedure Integer_MultiRangeFilterWithALowEndPastTheWindow_ThrowsOutOfScope()
    var
        IntRec: Record Integer;
    begin
        // The mirror, and the one a check written only against the upper edge would miss:
        // `..-200000|1..50` is open at the LOW end of its first range, and -200000 is a HIGH bound
        // there, so it is not the outermost bound in either direction — 50 is higher and 1 is the
        // lowest closed low. Both sit inside the base window, so this shape too reads as
        // answerable through the outermost bounds alone.
        IntRec.SetFilter(Number, '..-200000|1..50');

        asserterror CountRows(IntRec);
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('integer-virtual-table');
        Assert.ExpectedError('with its other end open');
        Assert.ExpectedError('-200000');
    end;

    [Test]
    procedure Integer_MultiRangeFilterEntirelyInsideTheWindow_StillAnswers()
    var
        IntRec: Record Integer;
    begin
        // The control that keeps the refusal above from being "any filter with a `|` in it".
        // Every range here is closed and inside the base window, so the union is materialised and
        // answered: 5 rows from 1..5 and 3 from 90..92.
        IntRec.SetFilter(Number, '1..5|90..92');

        Assert.AreEqual(8, CountRows(IntRec), 'A multi-range filter inside the base window must be answered, not refused.');
        Assert.IsTrue(IntRec.FindFirst(), 'A multi-range filter inside the base window returned no rows.');
        Assert.AreEqual(1, IntRec.Number, 'Expected the first row of 1..5|90..92 to be Number 1.');
        Assert.IsTrue(IntRec.FindLast(), 'A multi-range filter inside the base window returned no last row.');
        Assert.AreEqual(92, IntRec.Number, 'Expected the last row of 1..5|90..92 to be Number 92.');
    end;

    [Test]
    procedure Integer_MultiRangeFilterClosedPastTheWindow_IsMaterialisedRatherThanRefused()
    var
        IntRec: Record Integer;
    begin
        // The other side of the per-range decision (#3471), and the arm that keeps the two
        // refusals above from reading as "a filter reaching past the base window is refused".
        // Every range here is CLOSED, so the union is provably 60 rows however far out the second
        // range sits: 50 from 1..50 and 10 from 200000..200009, the second entirely above the base
        // window's upper edge 100000. Nothing is open, so nothing has to be guessed at, and both
        // ranges are materialised on demand.
        //
        // The row counts are BC's claim and are pinned upstream (codeunit 60368,
        // Record_Integer_TwoClosedRanges_CountTheirUnion); what is runner-specific here is that a
        // span outside the base window is materialised at all rather than refused.
        IntRec.SetFilter(Number, '1..50|200000..200009');

        Assert.AreEqual(60, CountRows(IntRec), 'A closed multi-range filter reaching past the base window must be materialised.');
        Assert.IsTrue(IntRec.FindLast(), 'A closed multi-range filter reaching past the base window returned no last row.');
        Assert.AreEqual(200009, IntRec.Number, 'Expected the last row of 1..50|200000..200009 to be Number 200009.');
        Assert.IsTrue(IntRec.Get(200000), 'Number 200000 must be readable after the span was materialised.');
    end;

    [Test]
    procedure Integer_HalfOpenRangeWithASiblingRangeFurtherOut_IsStillRefused()
    var
        IntRec: Record Integer;
    begin
        // Issue #3528, and a CONTROL rather than a RED -> GREEN: this arm is refused on
        // a3fb61d5 too, before the comparison was moved onto the window. It pins that a sibling
        // range cannot move the bar the half-open range is judged against. Two things hold that
        // and only one of them is ours -- the refusal compares against the base window rather
        // than against the envelope-widened span, and BC's own FilterExpression.ToRangeList
        // merges this filter's second range into its first, because a range wide enough to widen
        // the envelope's high bound past 200000 necessarily overlaps `200000..`.
        IntRec.SetFilter(Number, '200000..|300000..300010');

        // The bound is asserted, not just the fact of a refusal: a refusal naming 300000 or
        // 300010 would mean the per-range decision landed on the wrong range.
        asserterror CountRows(IntRec);
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('integer-virtual-table');
        Assert.ExpectedError('with its other end open');
        Assert.ExpectedError('200000');
    end;

    [Test]
    procedure Integer_HalfOpenLowRangeWithASiblingRangeFurtherOut_IsStillRefused()
    var
        IntRec: Record Integer;
    begin
        // The mirror, on the low side and on the find path. `..-200000|-300010..-300000` is open
        // at the LOW end of its first range, and its sibling reaches further down still -- the
        // shape that would widen lowBound below -200000 if the envelope were what this range was
        // judged against. Refused on a3fb61d5 as well, for the merge reason above; what makes it
        // hold without that merge is the window comparison.
        IntRec.SetFilter(Number, '..-200000|-300010..-300000');

        asserterror IntRec.FindSet();
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('integer-virtual-table');
        Assert.ExpectedError('with its other end open');
        Assert.ExpectedError('-200000');
    end;

    local procedure CountRows(var IntRec: Record Integer): Integer
    begin
        exit(IntRec.Count());
    end;

    local procedure IsEmptyRows(var IntRec: Record Integer): Boolean
    begin
        exit(IntRec.IsEmpty());
    end;

    [TryFunction]
    local procedure TryFindSet(var IntRec: Record Integer)
    begin
        IntRec.FindSet();
    end;
}
