// InnerJoin/LeftOuterJoin coverage (real BC join semantics, including runtime SetRange/
// SetFilter on parent and child query columns) migrated upstream to the al-language corpus
// (tests/al-language, query/). Only RightOuterJoin stays: it is not a BC-semantics assertion
// but a runner-specific OutOfScope classification (loud-failures rule) — the in-memory
// nested-loop join executor cannot faithfully reproduce RightOuterJoin, so opening this query
// must throw RunnerOutOfScopeException with a named reason rather than return wrong rows.
codeunit 60391 "QJ Query Join Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "QJ Assert";

    local procedure Seed()
    var
        Cust: Record "QJ Customer";
        Ord: Record "QJ Order";
    begin
        Cust.DeleteAll();
        Ord.DeleteAll();

        InsertCust(Cust, 'C1', 'Alice');
        InsertCust(Cust, 'C2', 'Bob');
        InsertCust(Cust, 'C3', 'Carol');

        InsertOrder(Ord, 1, 'C1', 100);
        InsertOrder(Ord, 2, 'C1', 200);
        InsertOrder(Ord, 3, 'C2', 300);
    end;

    local procedure InsertCust(var Cust: Record "QJ Customer"; No: Code[20]; Name: Text[50])
    begin
        Cust.Init();
        Cust."No." := No;
        Cust.Name := Name;
        Cust.Insert();
    end;

    local procedure InsertOrder(var Ord: Record "QJ Order"; EntryNo: Integer; CustNo: Code[20]; Amount: Decimal)
    begin
        Ord.Init();
        Ord."Entry No." := EntryNo;
        Ord."Customer No." := CustNo;
        Ord.Amount := Amount;
        Ord.Insert();
    end;

    [Test]
    procedure RightOuterJoin_IsAGap_ThrowsAReasonThatTearsThroughATryFunction()
    var
        Q: Query "QJ Cust Orders Right";
    begin
        // RightOuterJoin is valid AL and real BC executes it; the in-memory nested-loop join
        // executor does not take it yet. Per loud-failures, opening/reading it must throw
        // RunnerOutOfScopeException naming the API and a specific reason — never silently
        // return wrong rows.
        Seed();
        asserterror begin
            Q.Open();
            Q.Read();
        end;

        // Three claims, and each one is load-bearing (#2966).
        //
        // 1. The refusal LEADS with "not-yet-implemented". That token is what
        //    ApplicationObjectBasePatches.IsPermanentOutOfScope reads to decide whether an AL
        //    [TryFunction] may absorb the refusal into `false`. This is a runner gap, so it
        //    must tear through instead. Before #2966 the reason led with the surface anchor
        //    and a [TryFunction] here read `false` with nothing printed.
        if StrPos(GetLastErrorText(), 'not-yet-implemented') = 0 then
            Error('Expected the not-yet-implemented anchor, got: %1', GetLastErrorText());
        // 2. It no longer claims docs/scope.md, the permanently-out-of-scope manifest.
        if StrPos(GetLastErrorText(), 'docs/scope.md') > 0 then
            Error('The refusal still cites docs/scope.md: %1', GetLastErrorText());
        // 3. And it still names WHICH sub-shape was refused, so the message stays actionable.
        Assert.ExpectedError('query-join-rightouterjoin-link-type');
    end;
}
