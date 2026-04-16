codeunit 58600 "Test Boolean ToText"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure FormatTrue_DefaultFormat_ReturnsYes()
    var
        B: Boolean;
        Result: Text;
    begin
        B := true;
        Result := Format(B);
        Assert.AreEqual('Yes', Result, 'Format(true) must return Yes');
    end;

    [Test]
    procedure FormatFalse_DefaultFormat_ReturnsNo()
    var
        B: Boolean;
        Result: Text;
    begin
        B := false;
        Result := Format(B);
        Assert.AreEqual('No', Result, 'Format(false) must return No');
    end;

    [Test]
    procedure FormatTrue_StandardFormat2_Returns1()
    var
        B: Boolean;
        Result: Text;
    begin
        B := true;
        Result := Format(B, 0, '<Standard Format,2>');
        Assert.AreEqual('1', Result, 'Format(true, 0, Standard Format 2) must return 1');
    end;

    [Test]
    procedure FormatFalse_StandardFormat2_Returns0()
    var
        B: Boolean;
        Result: Text;
    begin
        B := false;
        Result := Format(B, 0, '<Standard Format,2>');
        Assert.AreEqual('0', Result, 'Format(false, 0, Standard Format 2) must return 0');
    end;
}
