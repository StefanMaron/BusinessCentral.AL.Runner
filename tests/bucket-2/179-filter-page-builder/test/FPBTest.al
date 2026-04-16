codeunit 97201 "FPB Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure Ping_Proving()
    var
        Src: Codeunit "FPB Src";
    begin
        Assert.AreEqual(42, Src.Ping(), 'Ping must return 42');
    end;

    // --- Count ---

    [Test]
    procedure AddTable_EmptyFPB_CountIsZero()
    var
        FPB: FilterPageBuilder;
    begin
        Assert.AreEqual(0, FPB.Count, 'Initial count must be 0');
    end;

    [Test]
    procedure AddTable_OneTable_CountIsOne()
    var
        Src: Codeunit "FPB Src";
    begin
        Assert.AreEqual(1, Src.AddTableAndCount(), 'After AddTable count must be 1');
    end;

    [Test]
    procedure AddTable_TwoTables_CountIsTwo()
    var
        Src: Codeunit "FPB Src";
    begin
        Assert.AreEqual(2, Src.AddTwoTablesCount(), 'After two AddTable calls count must be 2');
    end;

    // --- SetView / GetView ---

    [Test]
    procedure SetView_GetView_EmptyView_RoundTrips()
    var
        Src: Codeunit "FPB Src";
    begin
        Assert.AreEqual('', Src.SetAndGetView(''), 'Empty view must round-trip');
    end;

    [Test]
    procedure SetView_GetView_NonEmptyView_RoundTrips()
    var
        Src: Codeunit "FPB Src";
    begin
        Assert.AreEqual(
            'WHERE(No.=FILTER(A001..Z999))',
            Src.SetAndGetView('WHERE(No.=FILTER(A001..Z999))'),
            'Filter view must round-trip through SetView/GetView');
    end;

    [Test]
    procedure GetView_WithoutSetView_ReturnsEmpty()
    var
        FPB: FilterPageBuilder;
    begin
        FPB.AddTable('Items', 27);
        Assert.AreEqual('', FPB.GetView('Items'), 'GetView without SetView must return empty string');
    end;

    // --- Name ---

    [Test]
    procedure Name_FirstIndex_ReturnsFirstCaption()
    var
        Src: Codeunit "FPB Src";
    begin
        Assert.AreEqual('Alpha', Src.NameAtIndex(1), 'Name(1) must return first registered caption');
    end;

    [Test]
    procedure Name_SecondIndex_ReturnsSecondCaption()
    var
        Src: Codeunit "FPB Src";
    begin
        Assert.AreEqual('Beta', Src.NameAtIndex(2), 'Name(2) must return second registered caption');
    end;

    // --- RunModal ---

    [Test]
    procedure RunModal_ReturnsOKAction()
    var
        Src: Codeunit "FPB Src";
    begin
        Assert.AreEqual(Action::OK, Src.RunModalResult(), 'RunModal in standalone must return Action::OK');
    end;

    // --- PageCaption ---

    [Test]
    procedure PageCaption_SetAndGet_RoundTrips()
    var
        Src: Codeunit "FPB Src";
    begin
        Assert.AreEqual('My Filter', Src.SetAndGetPageCaption('My Filter'), 'PageCaption must round-trip');
    end;

    // --- Negative / inequality ---

    [Test]
    procedure AddTable_CountsDiffer_OneVsTwo()
    var
        Src: Codeunit "FPB Src";
    begin
        Assert.AreNotEqual(Src.AddTableAndCount(), Src.AddTwoTablesCount(), 'Count with 1 table must differ from count with 2');
    end;
}
