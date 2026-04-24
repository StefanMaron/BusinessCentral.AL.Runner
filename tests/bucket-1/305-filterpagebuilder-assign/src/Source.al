codeunit 305001 "FPB Assign Src"
{
    /// <summary>
    /// Creates a FilterPageBuilder, adds a table, sets a view,
    /// assigns it to a second variable, then returns the count from the copy.
    /// Proves ALAssign copies state correctly.
    /// </summary>
    procedure AssignAndGetCount(): Integer
    var
        FPB1: FilterPageBuilder;
        FPB2: FilterPageBuilder;
    begin
        FPB1.AddTable('Items', 27);
        FPB1.AddTable('Customers', 18);
        FPB2 := FPB1;
        exit(FPB2.Count);
    end;

    /// <summary>
    /// Creates a FilterPageBuilder, adds a table with a view,
    /// assigns it to another variable, and returns the view from the copy.
    /// Proves the view data survives assignment.
    /// </summary>
    procedure AssignAndGetView(): Text
    var
        FPB1: FilterPageBuilder;
        FPB2: FilterPageBuilder;
    begin
        FPB1.AddTable('Items', 27);
        FPB1.SetView('Items', 'WHERE(No.=FILTER(1000..2000))');
        FPB2 := FPB1;
        exit(FPB2.GetView('Items'));
    end;

    /// <summary>
    /// Assigns a FilterPageBuilder, then modifies the original.
    /// The copy must retain its original state (deep copy semantics).
    /// </summary>
    procedure AssignIsDeepCopy(): Integer
    var
        FPB1: FilterPageBuilder;
        FPB2: FilterPageBuilder;
    begin
        FPB1.AddTable('Items', 27);
        FPB2 := FPB1;
        FPB1.AddTable('Customers', 18);
        // FPB2 should still have count 1 if deep copy
        // FPB2 would have count 2 if shallow copy
        exit(FPB2.Count);
    end;
}
