// Issue #5177. A part linked through a Provider reads the Provider's CURRENT row, so it follows
// the Provider's moves without being moved itself, and shows nothing when the Provider shows
// nothing. The AL arms mirror corpus codeunit 69143 "PVD Provider Move Tests" (measured on real
// BC); this pins the runner's own wiring over a source-compiled fixture.
codeunit 70961 "PF Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "PF Assert";

    local procedure Initialize()
    begin
        DeleteAll();
        AddHeader('H1');
        AddHeader('H2');
        AddHeader('H3');
        AddLine('H1', 10);
        AddLine('H1', 20);
        AddLine('H3', 30);
        AddDetail(1, 10, 'ten');
        AddDetail(2, 20, 'twenty');
        AddDetail(3, 1, 'decoy');
        AddDetail(4, 30, 'thirty');
    end;

    local procedure DeleteAll()
    var
        Header: Record "PF Header";
        Line: Record "PF Line";
        Detail: Record "PF Detail";
    begin
        Detail.DeleteAll();
        Line.DeleteAll();
        Header.DeleteAll();
    end;

    local procedure AddHeader(No: Code[20])
    var
        Header: Record "PF Header";
    begin
        Header.Init();
        Header."No." := No;
        Header."Line No." := 1;
        Header.Insert();
    end;

    local procedure AddLine(HeaderNo: Code[20]; LineNo: Integer)
    var
        Line: Record "PF Line";
    begin
        Line.Init();
        Line."Header No." := HeaderNo;
        Line."Line No." := LineNo;
        Line.Insert();
    end;

    local procedure AddDetail(EntryNo: Integer; LineRef: Integer; Info: Text[30])
    var
        Detail: Record "PF Detail";
    begin
        Detail.Init();
        Detail."Entry No." := EntryNo;
        Detail."Line Ref" := LineRef;
        Detail.Info := Info;
        Detail.Insert();
    end;

    [Test]
    procedure DependentFollowsTheProvider_WithoutItsOwnMove()
    var
        Card: TestPage "PF Card";
    begin
        Initialize();
        Card.OpenView();
        Card.GoToKey('H1');
        Assert.AreEqual('ten', Card.Detail.Info.Value, 'initial');

        Card.Lines.Next();
        Assert.AreEqual('twenty', Card.Detail.Info.Value, 'after the Provider moved to line 20');

        Card.Lines.Previous();
        Assert.AreEqual('ten', Card.Detail.Info.Value, 'after the Provider moved back to line 10');
    end;

    [Test]
    procedure DependentFollowsTheProvider_ItsOwnCursorIsOnTheProvidersRow()
    var
        Card: TestPage "PF Card";
    begin
        Initialize();
        Card.OpenView();
        Card.GoToKey('H1');
        Card.Lines.Next();

        Assert.IsFalse(Card.Detail.Next(), 'exactly one detail row is keyed to line 20');
        Assert.AreEqual('twenty', Card.Detail.Info.Value, 'Next() past the only row stays on it');
    end;

    [Test]
    procedure HostMovesToAnEmptyProvider_DependentShowsNothing()
    var
        Card: TestPage "PF Card";
    begin
        Initialize();
        Card.OpenView();
        Card.GoToKey('H1');
        Assert.AreEqual('ten', Card.Detail.Info.Value, 'initial');

        Card.GoToKey('H2');
        Assert.AreEqual('', Card.Detail.Info.Value, 'H2 has no lines, so no detail is shown');
        Assert.IsFalse(Card.Detail.First(), 'and the Detail part has no row to go to');
        Assert.AreEqual('', Card.Detail.Info.Value, 'still blank');

        Card.GoToKey('H3');
        Assert.AreEqual('thirty', Card.Detail.Info.Value, 'a Provider that has rows again re-points the part');
    end;

    [Test]
    procedure PlainLinkedPart_HostMovesToARowWithNoChildren_ShowsNothing()
    var
        Card: TestPage "PF Plain Card";
    begin
        Initialize();
        Card.OpenView();
        Card.GoToKey('H1');
        Assert.AreEqual('H1', Card.Lines.HeaderNo.Value, 'initial');

        Card.GoToKey('H2');
        Assert.AreEqual('', Card.Lines.HeaderNo.Value, 'H2 has no lines, and the part keeps no row');
    end;
}
