// Issues #1708 (a signed FlowField formula) and #1709 (const()/filter() where-conditions).
//
// Before the fix both failed SILENTLY: `-sum(...)` was refused by the CalcFormula parser, so
// the FlowField's CalculationFormula stayed EmptyFormula and CalcFields() left the field at 0;
// const()/filter() conditions were dropped, so the FlowField aggregated rows AL had excluded
// and returned a plausible wrong number. Nothing threw in either case.
//
// The seed rows are chosen so that every FlowField lands on a DIFFERENT value. An
// implementation that ignores the sign, ignores the conditions, or returns the type default
// fails at least one assertion in every test below.
codeunit 64266 "CFS Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "CFS Assert";

    local procedure Seed(): Code[20]
    var
        CfsLine: Record "CFS Line";
        CfsHeader: Record "CFS Header";
    begin
        CfsLine.DeleteAll();
        CfsHeader.DeleteAll();

        CfsHeader.Init();
        CfsHeader."No." := 'D1';
        CfsHeader.Insert(false);

        // Entry 1: Open, Status Open,     100
        // Entry 2: not Open, Status Released, 20
        // Entry 3: Open, Status Closed,     3
        AddLine(1, 'D1', 100, true, CfsLine.Status::Open);
        AddLine(2, 'D1', 20, false, CfsLine.Status::Released);
        AddLine(3, 'D1', 3, true, CfsLine.Status::Closed);

        // A second document, never asserted on: proves the parent link still filters.
        AddLine(4, 'D2', 999, true, CfsLine.Status::Open);

        exit('D1');
    end;

    local procedure AddLine(EntryNo: Integer; DocNo: Code[20]; Amt: Decimal; IsOpen: Boolean; Sts: Option)
    var
        CfsLine: Record "CFS Line";
    begin
        CfsLine.Init();
        CfsLine."Entry No." := EntryNo;
        CfsLine."Doc No." := DocNo;
        CfsLine.Amount := Amt;
        CfsLine.Open := IsOpen;
        CfsLine.Status := Sts;
        CfsLine.Insert(false);
    end;

    [Test]
    procedure SignedFormulaNegatesTheAggregate()
    var
        CfsHeader: Record "CFS Header";
    begin
        // [GIVEN] Three lines on D1 totalling 123, plus 999 on another document.
        CfsHeader.Get(Seed());

        // [WHEN] Both the plain and the signed sum of the same rows are calculated.
        CfsHeader.CalcFields("Total Amount", "Negated Total");

        // [THEN] The unsigned formula is unchanged...
        Assert.AreEqual(123, CfsHeader."Total Amount",
            'the unsigned sum must aggregate only this document''s lines');

        // [THEN] ...and the signed one is its negation. 0 is the pre-fix value (the formula
        // was refused outright), 123 is the value a dropped sign would give.
        Assert.AreEqual(-123, CfsHeader."Negated Total",
            '-sum(...) must negate the aggregate, not drop the sign and not stay at 0');
    end;

    [Test]
    procedure ConstConditionExcludesRows()
    var
        CfsHeader: Record "CFS Header";
    begin
        // [GIVEN] Two of the three D1 lines are Open (100 and 3); one is not (20).
        CfsHeader.Get(Seed());

        // [WHEN]
        CfsHeader.CalcFields("Total Amount", "Open Total", "Closed Count");

        // [THEN] const(true) keeps only the Open rows — a dropped condition would give 123.
        Assert.AreEqual(103, CfsHeader."Open Total",
            'Open = const(true) must exclude the non-Open line');
        Assert.AreNotEqual(CfsHeader."Total Amount", CfsHeader."Open Total",
            'the const condition must actually change which rows are summed');

        // [THEN] const(false) selects the complement, through a count formula.
        Assert.AreEqual(1, CfsHeader."Closed Count",
            'Open = const(false) must count exactly the one non-Open line');
    end;

    [Test]
    procedure FilterConditionAppliesAlternationAndRange()
    var
        CfsHeader: Record "CFS Header";
    begin
        // [GIVEN] Statuses Open / Released / Closed on entries 1 / 2 / 3.
        CfsHeader.Get(Seed());

        // [WHEN]
        CfsHeader.CalcFields("Active Total", "Range Total");

        // [THEN] filter(Open|Released) is one expression selecting two option members.
        Assert.AreEqual(120, CfsHeader."Active Total",
            'Status = filter(Open|Released) must sum the Open and Released lines only');

        // [THEN] filter(2..3) is a range over the entry numbers.
        Assert.AreEqual(23, CfsHeader."Range Total",
            '"Entry No." = filter(2..3) must sum entries 2 and 3 only');
    end;

    [Test]
    procedure ConditionsAreAndedSoAnImpossibleCombinationSumsToZero()
    var
        CfsHeader: Record "CFS Header";
    begin
        // [GIVEN] The only Closed line (entry 3) IS Open, so no line is both Closed and
        // not Open.
        CfsHeader.Get(Seed());

        // [WHEN]
        CfsHeader.CalcFields("Unmatched Total", "Negated Released Total");

        // [THEN] Both conditions apply, and they narrow together — dropping either one
        // would yield 3 or 20 rather than nothing.
        Assert.AreEqual(0, CfsHeader."Unmatched Total",
            'Status = const(Closed) AND Open = const(false) must select no line at all');

        // [THEN] Sign and const condition compose: only the Released line, negated.
        Assert.AreEqual(-20, CfsHeader."Negated Released Total",
            '-sum(... Status = const(Released)) must negate the filtered subtotal');
    end;
}
