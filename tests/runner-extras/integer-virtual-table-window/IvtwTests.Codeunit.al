// Issues #2350 and #3376 — the runner-specific half of the Integer system virtual table
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
// same two constants. That is the behaviour codeunit 60369 pins upstream, against a service tier.
//
// The runner serves the table from an in-memory store, so it has to materialise rows, and 2e9+1
// of them is not on the table. It materialises [-1000..100000] and — this is the claim below —
// refuses by name when a request reaches past that, rather than answering with the rows it
// happens to hold.
//
// What the rows themselves say — that 0 and negative Numbers are real rows, that a range yields
// every value in ascending order, that an inverted range matches nothing — is plain BC behaviour
// and lives upstream in the al-language corpus (codeunit 60368, "Test Integer Virtual Table").
// None of it is repeated here.
//
// The damaging case without the guard: a report dataitem filtered to Number in [1..250000]
// silently iterates 100000 times and reports success. The dataset is short by 150000 rows and
// nothing says so.

codeunit 64591 "Ivtw Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Ivtw Assert";

    [Test]
    procedure Integer_ClosedRangePastTheWindow_ThrowsOutOfScopeOnTheFindPath()
    var
        IntRec: Record Integer;
    begin
        // [GIVEN] a closed range whose upper bound is past the materialised window but far
        // inside the +-1e9 range a service tier would serve, so BC itself answers this with
        // 250000 rows and refusing is a runner limit rather than a statement about BC.
        IntRec.SetRange(Number, 1, 250000);

        // [THEN] the runner refuses by name rather than answering from the window it holds.
        // Answering would return 100000 rows for a request BC answers with 250000 — and the
        // caller reads the short count as the real one.
        asserterror IntRec.FindSet();
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('past the');
    end;

    [Test]
    procedure Integer_ClosedRangePastTheWindow_ThrowsOnTheCountPathToo()
    var
        IntRec: Record Integer;
    begin
        // Count() builds a CountCacheRequest, not a FindCacheRequest, so the find guard never
        // sees it. Without a guard on this path Count() answers 100000 — a number that looks
        // entirely real, and is short by 150000.
        IntRec.SetRange(Number, 1, 250000);

        asserterror CountRows(IntRec);
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('past the');
    end;

    [Test]
    procedure Integer_ClosedRangePastTheWindow_ThrowsOnTheIsEmptyPathToo()
    var
        IntRec: Record Integer;
    begin
        // IsEmpty() is a third request path: RecordImplementation.IsEmptyAsync builds an
        // ExistsCacheRequest and reaches DataAccess.ExistsAsync, never CountAsync (#3006 proved
        // exactly this for the Date table). Without a guard here IsEmpty() answers FALSE from
        // the rows it happens to hold — true by accident, and says nothing about the range asked
        // for.
        IntRec.SetRange(Number, 1, 250000);

        asserterror IsEmptyRows(IntRec);
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('past the');
    end;

    [Test]
    procedure Integer_KeyedGetPastTheWindow_ThrowsOutOfScope()
    var
        IntRec: Record Integer;
    begin
        // A full-primary-key Get never reaches the find path: DataAccess has its own
        // primary-key route straight to the provider (#2504 for Aggregate Permission Set, #2648
        // for Date). Without a guard there, Get(250000) answers FALSE — which reads as "no such
        // row", when on a service tier row 250000 plainly exists.
        asserterror GetRow(IntRec, 250000);
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('past the');
    end;

    [Test]
    procedure Integer_ClosedRangeBelowTheWindow_ThrowsOutOfScope()
    var
        IntRec: Record Integer;
    begin
        // The window has a LOWER edge too, at -1000, and it is the one nothing else in this
        // suite touches. A guard that only compared against IntegerWindowMax would pass every
        // other test here and still answer this one short: BC serves [-5000..-4000] with 1001
        // rows, the runner holds none of them and would report 0.
        IntRec.SetRange(Number, -5000, -4000);

        asserterror CountRows(IntRec);
        Assert.ExpectedError('out-of-scope: Integer (virtual table 2000000026)');
        Assert.ExpectedError('past the');
    end;

    [Test]
    procedure Integer_RangeInsideTheWindow_StillAnswersNormally()
    var
        IntRec: Record Integer;
        Seen: Integer;
    begin
        // The control, and the reason this suite cannot pass with a guard that simply throws on
        // every Integer read. A range wholly inside the window is answered, not refused, and the
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
    procedure Integer_KeyedGetAtTheWindowEdges_StillAnswers()
    var
        IntRec: Record Integer;
    begin
        // The boundary control for the keyed-Get guard. Both edges of the window are INCLUSIVE,
        // so a guard written with > instead of >= (or < instead of <=) refuses a row it holds
        // and fails here while passing every other test in this suite.
        Assert.IsTrue(IntRec.Get(100000), 'Number 100000 is the window''s upper edge and must be readable.');
        Assert.AreEqual(100000, IntRec.Number, 'Get(100000) returned a different row.');

        Assert.IsTrue(IntRec.Get(-1000), 'Number -1000 is the window''s lower edge and must be readable.');
        Assert.AreEqual(-1000, IntRec.Number, 'Get(-1000) returned a different row.');
    end;

    [Test]
    procedure Integer_UnboundedFilter_IsServedFromTheWindowRatherThanRefused()
    var
        IntRec: Record Integer;
        Seen: Integer;
    begin
        // Issue #3376 asked for the opposite of this test, and the decompile is why it does not
        // get it. #3376 reads the unbounded case as "you asked for all of them and we invented a
        // bound", and proposes refusing it. But BC invents a bound too — GetInclusiveIntegerBounds
        // substitutes -1e9 for an open low and +1e9 for an open high — so refusing would diverge
        // from a service tier on a shape BaseApp uses constantly. 18 of BaseApp's 658 reports
        // declare MaxIteration over an unbounded `dataitem(x; Integer)`, and #3374's fix makes
        // exactly those loops stop after one row; refusing here would turn every one of them
        // from a working report into a hard failure.
        //
        // So the unbounded case is served, and this test pins that it is served rather than
        // refused. The runner's remaining divergence from BC — 101,001 rows where BC yields
        // 2,000,000,001 — is real, and is recorded in docs/limitations.md rather than papered
        // over here.
        IntRec.SetFilter(Number, '>=1');

        Assert.IsTrue(IntRec.FindFirst(), 'An unbounded Integer filter must be served, not refused.');
        Assert.AreEqual(1, IntRec.Number, 'Expected the unbounded range to start at 1.');

        // Not merely "it did not throw": the window's own upper edge is reachable through it.
        IntRec.SetFilter(Number, '>=99998');
        Assert.IsTrue(IntRec.FindSet(), 'Expected rows at the top of the window.');
        repeat
            Seen += 1;
        until IntRec.Next() = 0;
        Assert.AreEqual(3, Seen, 'Expected 99998, 99999 and 100000 — the window''s last three rows.');
    end;

    [Test]
    procedure Integer_WindowRefusal_TearsThroughATryFunction_InsteadOfReadingAsFalse()
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
        // done without the table and the test would go green. The Integer window is a runner
        // limit, not a permanent scope boundary: a service tier answers this read.
        IntRec.SetRange(Number, 1, 250000);

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

    local procedure GetRow(var IntRec: Record Integer; Number: Integer): Boolean
    begin
        exit(IntRec.Get(Number));
    end;

    [TryFunction]
    local procedure TryFindSet(var IntRec: Record Integer)
    begin
        IntRec.FindSet();
    end;
}
