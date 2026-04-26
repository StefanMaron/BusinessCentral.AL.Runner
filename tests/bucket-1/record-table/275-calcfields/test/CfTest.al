/// CalcFields FlowField tests (issue #864).
/// Proves that CalcFields correctly evaluates Sum, Count, and Exist
/// FlowField formulas against in-memory child records.
codeunit 116002 "CF Test"
{
    Subtype = Test;
    var Assert: Codeunit Assert;

    // ── Helpers ───────────────────────────────────────────────────────────────

    local procedure InsertParent(Id: Integer)
    var
        P: Record "CF Parent";
    begin
        P.Id := Id;
        P.Insert();
    end;

    local procedure InsertChild(Id: Integer; ParentId: Integer; Amount: Decimal)
    var
        C: Record "CF Child";
    begin
        C.Id := Id;
        C.ParentId := ParentId;
        C.Amount := Amount;
        C.Insert();
    end;

    // ── Sum FlowField ─────────────────────────────────────────────────────────

    [Test]
    procedure CalcFields_Sum_SumsChildAmounts()
    var
        P: Record "CF Parent";
    begin
        // [GIVEN] A parent with two children summing to 42
        InsertParent(1);
        InsertChild(1, 1, 20);
        InsertChild(2, 1, 22);

        // [WHEN] CalcFields is called for TotalAmount
        P.Get(1);
        P.CalcFields(TotalAmount);

        // [THEN] TotalAmount equals the sum of child amounts
        Assert.AreEqual(42, P.TotalAmount, 'CalcFields(Sum) must sum linked child amounts');
    end;

    [Test]
    procedure CalcFields_Sum_EmptyChildren_ReturnsZero()
    var
        P: Record "CF Parent";
    begin
        // [GIVEN] A parent with no children
        InsertParent(2);

        // [WHEN] CalcFields is called
        P.Get(2);
        P.CalcFields(TotalAmount);

        // [THEN] TotalAmount is 0 (no children to sum)
        Assert.AreEqual(0, P.TotalAmount, 'CalcFields(Sum) on empty children must return 0');
    end;

    [Test]
    procedure CalcFields_Sum_NotDefaultAfterInsertingChildren()
    var
        P: Record "CF Parent";
    begin
        // Negative: a no-op CalcFields that always returns 0 would fail this.
        InsertParent(3);
        InsertChild(10, 3, 100);

        P.Get(3);
        P.CalcFields(TotalAmount);

        Assert.AreNotEqual(0, P.TotalAmount,
            'CalcFields(Sum) must not return 0 when children exist');
    end;

    // ── Count FlowField ───────────────────────────────────────────────────────

    [Test]
    procedure CalcFields_Count_CountsChildren()
    var
        P: Record "CF Parent";
    begin
        // [GIVEN] A parent with three children
        InsertParent(4);
        InsertChild(20, 4, 1);
        InsertChild(21, 4, 1);
        InsertChild(22, 4, 1);

        // [WHEN] CalcFields is called for ChildCount
        P.Get(4);
        P.CalcFields(ChildCount);

        // [THEN] ChildCount equals 3
        Assert.AreEqual(3, P.ChildCount, 'CalcFields(Count) must count linked children');
    end;

    [Test]
    procedure CalcFields_Count_ZeroWhenNoChildren()
    var
        P: Record "CF Parent";
    begin
        InsertParent(5);

        P.Get(5);
        P.CalcFields(ChildCount);

        Assert.AreEqual(0, P.ChildCount, 'CalcFields(Count) on empty children must return 0');
    end;

    // ── Exist FlowField ───────────────────────────────────────────────────────

    [Test]
    procedure CalcFields_Exist_TrueWhenChildrenExist()
    var
        P: Record "CF Parent";
    begin
        // [GIVEN] A parent with a child
        InsertParent(6);
        InsertChild(30, 6, 5);

        // [WHEN] CalcFields for HasChildren
        P.Get(6);
        P.CalcFields(HasChildren);

        // [THEN] HasChildren is true
        Assert.IsTrue(P.HasChildren, 'CalcFields(Exist) must return true when children exist');
    end;

    [Test]
    procedure CalcFields_Exist_FalseWhenNoChildren()
    var
        P: Record "CF Parent";
    begin
        InsertParent(7);

        P.Get(7);
        P.CalcFields(HasChildren);

        Assert.IsFalse(P.HasChildren, 'CalcFields(Exist) must return false when no children');
    end;

    // ── Multiple fields in one call ───────────────────────────────────────────

    [Test]
    procedure CalcFields_MultipleFields_AllComputed()
    var
        P: Record "CF Parent";
    begin
        // [GIVEN] Parent with two children summing 15
        InsertParent(8);
        InsertChild(40, 8, 7);
        InsertChild(41, 8, 8);

        // [WHEN] CalcFields for both TotalAmount and ChildCount in one call
        P.Get(8);
        P.CalcFields(TotalAmount, ChildCount);

        // [THEN] Both fields are correctly populated
        Assert.AreEqual(15, P.TotalAmount, 'TotalAmount must be 15');
        Assert.AreEqual(2, P.ChildCount, 'ChildCount must be 2');
    end;

    // ── Children from different parents are not mixed ─────────────────────────

    [Test]
    procedure CalcFields_Sum_OnlyCountsOwnChildren()
    var
        P1: Record "CF Parent";
        P2: Record "CF Parent";
    begin
        // [GIVEN] Two parents each with their own children
        InsertParent(9);
        InsertParent(10);
        InsertChild(50, 9, 100);
        InsertChild(51, 10, 200);

        // [WHEN] Each parent calls CalcFields
        P1.Get(9);
        P1.CalcFields(TotalAmount);
        P2.Get(10);
        P2.CalcFields(TotalAmount);

        // [THEN] Each sees only its own children
        Assert.AreEqual(100, P1.TotalAmount, 'Parent 9 must see only its own 100');
        Assert.AreEqual(200, P2.TotalAmount, 'Parent 10 must see only its own 200');
    end;
}
