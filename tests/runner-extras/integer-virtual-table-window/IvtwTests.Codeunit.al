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
    procedure Integer_UnboundedFilter_IsServedFromTheBaseWindowRatherThanRefused()
    var
        IntRec: Record Integer;
        Seen: Integer;
    begin
        // Issue #3376 asked for the opposite of this test, and the decompile is why it does not
        // get it. #3376 reads the unbounded case as "you asked for all of them and we invented a
        // bound", and proposes refusing it. But BC invents a bound too — GetInclusiveIntegerBounds
        // substitutes -1e9 for an open low and +1e9 for an open high — so refusing would diverge
        // from a service tier on a shape BaseApp uses constantly. 18 of BaseApp's 658 reports
        // declare MaxIteration over an unbounded `dataitem(x; Integer)`.
        //
        // So an open bound whose closed end the base window can answer from is served, and this
        // test pins WHERE it stops: at the base window's edge, not at 1,000,000,000. That
        // remaining divergence is a row count, and it is recorded in docs/limitations.md.
        IntRec.SetFilter(Number, '>=1');

        Assert.IsTrue(IntRec.FindFirst(), 'An unbounded Integer filter must be served, not refused.');
        Assert.AreEqual(1, IntRec.Number, 'Expected the unbounded range to start at 1.');

        // Not merely "it did not throw": the base window's own upper edge is reachable through it,
        // and is where the enumeration ends.
        IntRec.SetFilter(Number, '>=99998');
        Assert.IsTrue(IntRec.FindSet(), 'Expected rows at the top of the base window.');
        repeat
            Seen += 1;
        until IntRec.Next() = 0;
        Assert.AreEqual(3, Seen, 'Expected 99998, 99999 and 100000 — the base window''s last three rows.');
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
        Assert.IsFalse(Reached, 'TryFindSet must not have completed.');
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
