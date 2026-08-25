/// #1883 follow-up to #1800/#1947/#1970/#1990/#2004 — one more cluster of orphaned
/// `Hook(...)` (JmpHook) registrations in AlRunner/BcRuntime.cs, measured via
/// `AL_RUNNER_HOOK_AUDIT=1` against the al-language corpus. JmpHook is disabled by default
/// (see AlRunner/Infrastructure/JmpHook.cs), so this cluster was a silent no-op — BC's real,
/// unpatched body ran instead, and the corpus (2128/2128) was already green before this PR
/// touched anything.
///
/// Cluster — NavCancellationToken (3 registrations: ThrowIfCancellationRequested,
/// ThrowOperationCanceledException x2 overloads). The deleted registration's own comment
/// claimed "uninitialized cancellation tokens trip the check", implying BC's real body would
/// NRE on a bare/default token. Decompiled (ilspycmd) to check: real
/// `ThrowIfCancellationRequested()` only reads the plain `cancellationToken.
/// IsCancellationRequested` bool field — it never touches the private `source` field that
/// would NRE on an uninitialized token. And the runner never actually threads a bare
/// `default(NavCancellationToken)` through this path anyway: `NavSession.CancellationToken`
/// (`AlRunner/Patches/RecordPatches.FieldFindIntercept.cs`) is exactly the token
/// `RecordPatches.FieldFindIntercept.cs` passes into BC's real async-enumerator
/// `GetAsyncEnumerator(session.CancellationToken)` on every `Find`/`FindSet`/`Next` — the same
/// "root scope carries a real, never-cancelled token, not a bare default" shape
/// `AlRunner/Infrastructure/NclCecilRewrite.cs`'s "NavMethodScope.RegisterCancellationToken —
/// root-scope early-return" block already documents for report resource-governance handlers.
/// Both `ThrowOperationCanceledException` overloads are private and only reached from inside
/// `NavCancellationToken`'s own `RunActionWithCancellationToken*` helpers after they have
/// already caught a real `OperationCanceledException` — never reachable from a plain Find/Next
/// loop, so this cluster's only actually-exercised member is `ThrowIfCancellationRequested`.
/// Deleted outright (`AlRunner/BcRuntime.cs`'s shared `NoOp_OneArg`/`NoOp2` stay: used by other
/// still-orphaned clusters elsewhere in the same file).
///
/// The tests below are regression guards with a narrower runner-mechanism claim than the full
/// BC behaviour: "no redirect fires here, and BC's real, unpatched
/// NavCancellationToken.ThrowIfCancellationRequested body completes correctly across a
/// multi-record Find/FindSet/Next loop" — not a re-proof of Record.FindSet/Next's iteration
/// contract itself, which is already proven upstream by the al-language corpus (65+ files use
/// FindSet/Find/FindFirst/FindLast, all passing today with this registration already inert).
/// Same framing as error-string-hooks-1883/EshTests.Codeunit.al and the other #1883-follow-up
/// suites in this consolidated app (see bc-behavior-tests-go-upstream.md).
codeunit 60707 "NCT Tests"
{
    Subtype = Test;

    // No Codeunit Assert dependency in this app (see the sibling suites in this consolidated
    // app.json) — plain Error() with an explicit condition check plays the same role.

    local procedure Seed(Count: Integer)
    var
        Item: Record "NCT Item";
        i: Integer;
    begin
        Item.DeleteAll();
        for i := 1 to Count do begin
            Item.Init();
            Item."No." := Format(i, 5, '<Integer,5><Filler Character,0>');
            Item.Value := i * 10;
            Item.Insert();
        end;
    end;

    // ── NavCancellationToken.ThrowIfCancellationRequested, multi-record Find/Next loop ────────
    // Every FindSet + Next() call routes through RecordPatches.FieldFindIntercept.cs's real
    // GetAsyncEnumerator(session.CancellationToken) — the exact call site the deleted hook
    // would have redirected. 5 records forces multiple MoveNextAsync calls, not just one.
    [Test]
    procedure FindSet_MultipleRecords_RealCancellationTokenPath_IteratesAll()
    var
        Item: Record "NCT Item";
        Count: Integer;
        Total: Integer;
    begin
        Seed(5);

        if not Item.FindSet() then
            Error('FindSet() must find the 5 seeded records');

        repeat
            Count += 1;
            Total += Item.Value;
        until Item.Next() = 0;

        if Count <> 5 then
            Error('Expected to iterate 5 records via the real cancellation-token path, got: %1', Count);
        if Total <> 150 then // 10+20+30+40+50
            Error('Expected Value sum 150 after iterating all 5 records, got: %1', Total);
    end;

    // ── Same path, single-record shape (Find('-')) ────────────────────────────────────────────
    [Test]
    procedure FindFirst_SingleRecord_RealCancellationTokenPath_Succeeds()
    var
        Item: Record "NCT Item";
    begin
        Seed(1);

        if not Item.FindFirst() then
            Error('FindFirst() must find the 1 seeded record');
        if Item.Value <> 10 then
            Error('Expected Value 10 on the single seeded record, got: %1', Item.Value);
    end;

    // ── Same path, zero-row shape — GetAsyncEnumerator still constructed and iterated once ────
    // (limited claim: this only proves no exception, per tdd.md's *_NoThrow convention).
    [Test]
    procedure FindSet_EmptyTable_RealCancellationTokenPath_NoThrow()
    var
        Item: Record "NCT Item";
    begin
        Seed(0);

        if Item.FindSet() then
            Error('FindSet() on an empty table must return false');
    end;
}
