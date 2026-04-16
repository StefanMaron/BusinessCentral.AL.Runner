codeunit 67001 "Filter View Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // --- GetFilter(field) ---

    [Test]
    procedure GetFilter_NoFilter_ReturnsEmpty()
    var
        Rec: Record "FV Test Table";
    begin
        // Negative: no filter set — GetFilter must return ''
        Assert.AreEqual('', Rec.GetFilter(Id), 'GetFilter on unfiltered field must be empty');
    end;

    [Test]
    procedure GetFilter_AfterSetRange_ReturnsRangeExpression()
    var
        Rec: Record "FV Test Table";
    begin
        Rec.SetRange(Id, 1, 5);
        Assert.AreEqual('1..5', Rec.GetFilter(Id), 'GetFilter after SetRange(1,5) must return 1..5');
    end;

    [Test]
    procedure GetFilter_AfterSetRangeEqual_ReturnsSingleValue()
    var
        Rec: Record "FV Test Table";
    begin
        Rec.SetRange(Id, 3);
        Assert.AreEqual('3', Rec.GetFilter(Id), 'GetFilter after SetRange(3) must return 3');
    end;

    [Test]
    procedure GetFilter_AfterSetFilter_ReturnsExpression()
    var
        Rec: Record "FV Test Table";
    begin
        Rec.SetFilter(Category, 'A|B');
        Assert.AreEqual('A|B', Rec.GetFilter(Category), 'GetFilter after SetFilter(A|B) must return A|B');
    end;

    [Test]
    procedure GetFilter_AfterReset_ReturnsEmpty()
    var
        Rec: Record "FV Test Table";
    begin
        Rec.SetRange(Id, 1, 10);
        Rec.Reset();
        Assert.AreEqual('', Rec.GetFilter(Id), 'GetFilter after Reset must be empty');
    end;

    // --- GetView / SetView roundtrip ---

    [Test]
    procedure GetView_SetView_Roundtrip_RestoresRangeFilter()
    var
        Rec: Record "FV Test Table";
        View: Text;
    begin
        Rec.SetRange(Id, 1, 5);
        View := Rec.GetView();
        Rec.Reset();
        Assert.AreEqual('', Rec.GetFilter(Id), 'Precondition: filter cleared after Reset');
        Rec.SetView(View);
        Assert.AreEqual('1..5', Rec.GetFilter(Id), 'GetFilter after SetView must restore range 1..5');
    end;

    [Test]
    procedure GetView_SetView_Roundtrip_RestoresExpressionFilter()
    var
        Rec: Record "FV Test Table";
        View: Text;
    begin
        Rec.SetFilter(Category, 'A|B');
        View := Rec.GetView();
        Rec.Reset();
        Rec.SetView(View);
        Assert.AreEqual('A|B', Rec.GetFilter(Category), 'GetFilter after SetView must restore A|B filter');
    end;

    // --- CopyFilter ---

    [Test]
    procedure CopyFilter_CopiesRangeToTargetRecord()
    var
        Source: Record "FV Test Table";
        Target: Record "FV Test Table";
    begin
        Source.SetRange(Id, 2, 8);
        Source.CopyFilter(Id, Target);
        Assert.AreEqual('2..8', Target.GetFilter(Id),
            'CopyFilter must copy the range filter to the target field');
    end;

    [Test]
    procedure CopyFilter_NoFilter_ClearsTargetFilter()
    var
        Source: Record "FV Test Table";
        Target: Record "FV Test Table";
    begin
        // Target has a filter; Source field has none — CopyFilter must clear target's filter
        Target.SetRange(Id, 1, 3);
        Source.CopyFilter(Id, Target);
        Assert.AreEqual('', Target.GetFilter(Id),
            'CopyFilter with no source filter must clear target filter');
    end;

    // --- FilterGroup ---

    [Test]
    procedure FilterGroup_IsCallable_DoesNotThrow()
    var
        Rec: Record "FV Test Table";
    begin
        // FilterGroup is a stub in standalone mode; it must not throw
        Rec.FilterGroup(1);
        Rec.FilterGroup(6);
        Rec.FilterGroup(0);
        Assert.IsTrue(true, 'FilterGroup must not throw in standalone mode');
    end;
}
