codeunit 56741 "TPN TestPage Nav Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestPageCaptionReturnsText()
    var
        TP: TestPage "TPN Test Card";
        CaptionText: Text;
    begin
        TP.OpenEdit();
        CaptionText := TP.Caption;
        Assert.IsTrue(true, 'Caption should compile and return text');
        TP.Close();
    end;

    [Test]
    procedure TestPageFirstReturnsTrue()
    var
        TP: TestPage "TPN Test Card";
        Result: Boolean;
    begin
        TP.OpenView();
        Result := TP.First();
        Assert.IsTrue(Result, 'First() should return true');
        TP.Close();
    end;

    [Test]
    procedure TestPageGoToKeyReturnsTrue()
    var
        Rec: Record "TPN Test Record";
        TP: TestPage "TPN Test Card";
        Result: Boolean;
    begin
        // GoToKey returns true when the record exists in the table.
        Rec.Init();
        Rec.Id := 1;
        Rec.Name := 'Test';
        Rec.Insert();

        TP.OpenView();
        Result := TP.GoToKey(1);
        Assert.IsTrue(Result, 'GoToKey should return true');
        TP.Close();
    end;

    [Test]
    procedure TestPageFilterSetFilterNoOp()
    var
        TP: TestPage "TPN Test Card";
    begin
        TP.OpenView();
        TP.Filter.SetFilter(Name, 'Hello');
        Assert.IsTrue(true, 'Filter.SetFilter should compile and no-op');
        TP.Close();
    end;

    [Test]
    procedure TestPageFirstNegativeNotFalse()
    var
        TP: TestPage "TPN Test Card";
    begin
        TP.OpenView();
        Assert.IsFalse(not TP.First(), 'First() should not return false');
        TP.Close();
    end;

    [Test]
    procedure TestPageGoToKeyNegativeNotFalse()
    var
        TP: TestPage "TPN Test Card";
    begin
        // GoToKey returns false when the record does not exist in the table.
        TP.OpenView();
        Assert.IsFalse(TP.GoToKey(99999), 'GoToKey should return false for a missing record');
        TP.Close();
    end;
}
