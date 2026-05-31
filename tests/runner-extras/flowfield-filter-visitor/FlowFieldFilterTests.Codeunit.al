/// <summary>
/// Proves the temp-table filter visitor evaluates a FlowField (count) used in a
/// SetRange filter without NRE-ing on the self-referencing / empty-CalcFormula
/// probe, and that the by-name source-table resolution makes the count correct.
///
/// RED (before the fix): SetRange on a FlowField forces
/// TempTableDataProvider.RecordBufferEvaluatorVisitor.Evaluate to call
/// FlowFieldsHelper.CalcFieldsAsync directly. FieldsAndFormulaAreSelfReferencing
/// NRE'd on EmptyFormula.Filters (null), and even guarded the formula could not
/// resolve its source table → InvalidOperationException "no NCLMetaTable for table 0".
/// GREEN (after): the visitor computes the count faithfully and the filter selects
/// the correct rows.
/// </summary>
codeunit 60510 "FlowField Filter Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "FF Visitor Assert";

    [Test]
    procedure SetRangeOnFlowField_NoChildren_SelectsAllParents()
    var
        Parent: Record "FF Visitor Parent";
    begin
        // [GIVEN] Two parents, no children at all → each parent's "Child Count" is 0.
        Parent.DeleteAll();
        InsertParent(1);
        InsertParent(2);

        // [WHEN] Filtering parents whose FlowField "Child Count" = 0 (the Purch.-Post pattern).
        Parent.SetRange("Child Count", 0);

        // [THEN] Both parents match (count 0) — visitor evaluated the FlowField without NRE.
        Assert.AreEqual(2, Parent.Count(), 'Both zero-child parents should match Child Count = 0');
        Assert.IsTrue(Parent.FindFirst(), 'FindFirst should succeed on the filtered set');
    end;

    [Test]
    procedure SetRangeOnFlowField_WithChildren_ExcludesNonZeroParent()
    var
        Parent: Record "FF Visitor Parent";
    begin
        // [GIVEN] Parent 1 has 3 children, parent 2 has none.
        Parent.DeleteAll();
        ClearChildren();
        InsertParent(1);
        InsertParent(2);
        InsertChild(101, 1);
        InsertChild(102, 1);
        InsertChild(103, 1);

        // [WHEN] Filtering parents whose "Child Count" = 0.
        Parent.SetRange("Child Count", 0);

        // [THEN] Only parent 2 (zero children) matches; parent 1 (count 3) is excluded.
        Assert.AreEqual(1, Parent.Count(), 'Only the zero-child parent should match Child Count = 0');
        Parent.FindFirst();
        Assert.AreEqual(2, Parent."No.", 'The matching parent must be parent 2');

        // [AND] A direct CalcFields on parent 1 yields the real count 3.
        Parent.Reset();
        Parent.Get(1);
        Parent.CalcFields("Child Count");
        Assert.AreEqual(3, Parent."Child Count", 'CalcFields must compute the real child count');
    end;

    local procedure InsertParent(No: Integer)
    var
        Parent: Record "FF Visitor Parent";
    begin
        Parent.Init();
        Parent."No." := No;
        Parent.Insert();
    end;

    local procedure InsertChild(EntryNo: Integer; ParentNo: Integer)
    var
        Child: Record "FF Visitor Child";
    begin
        Child.Init();
        Child."Entry No." := EntryNo;
        Child."Parent No." := ParentNo;
        Child.Insert();
    end;

    local procedure ClearChildren()
    var
        Child: Record "FF Visitor Child";
    begin
        Child.DeleteAll();
    end;
}
