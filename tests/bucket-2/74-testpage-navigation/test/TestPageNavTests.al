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
        TP: TestPage "TPN Test Card";
        Result: Boolean;
    begin
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
        TP.OpenView();
        Assert.IsFalse(not TP.GoToKey(1), 'GoToKey should not return false');
        TP.Close();
    end;
}
