codeunit 83501 "Int ToText Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "Int ToText Src";

    [Test]
    procedure PositiveInteger_ToText_ReturnsString()
    begin
        Assert.AreEqual('42', Src.PositiveToText(), 'Integer 42 ToText must return ''42''');
    end;

    [Test]
    procedure NegativeInteger_ToText_ReturnsSignedString()
    begin
        Assert.AreEqual('-7', Src.NegativeToText(), 'Integer -7 ToText must return ''-7''');
    end;

    [Test]
    procedure Zero_ToText_ReturnsZeroString()
    begin
        Assert.AreEqual('0', Src.ZeroToText(), 'Integer 0 ToText must return ''0''');
    end;

    [Test]
    procedure LargeInteger_ToText_ReturnsString()
    begin
        Assert.AreEqual('1000000', Src.LargeToText(), 'Integer 1000000 ToText must return ''1000000''');
    end;

    [Test]
    procedure Inline_ToText_RoundTrip()
    var
        n: Integer;
    begin
        n := 99;
        Assert.AreEqual('99', n.ToText(), 'Inline integer ToText must return ''99''');
    end;

    [Test]
    procedure ToText_NotEmpty()
    var
        n: Integer;
    begin
        n := 5;
        Assert.AreNotEqual('', n.ToText(), 'ToText must not return empty string');
    end;
}
