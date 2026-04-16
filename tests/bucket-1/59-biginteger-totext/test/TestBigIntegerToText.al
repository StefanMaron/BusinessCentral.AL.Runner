codeunit 59401 "Test BigInteger ToText"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure BigInteger_Format_PositiveValue()
    var
        BigInt: BigInteger;
        Result: Text;
    begin
        BigInt := 12345;
        Result := Format(BigInt);
        Assert.AreEqual('12345', Result, 'Positive BigInteger must format as decimal string');
    end;

    [Test]
    procedure BigInteger_Format_NegativeValue()
    var
        BigInt: BigInteger;
        Result: Text;
    begin
        BigInt := -9999;
        Result := Format(BigInt);
        Assert.AreEqual('-9999', Result, 'Negative BigInteger must include minus sign');
    end;

    [Test]
    procedure BigInteger_Format_Zero()
    var
        BigInt: BigInteger;
        Result: Text;
    begin
        BigInt := 0;
        Result := Format(BigInt);
        Assert.AreEqual('0', Result, 'Zero BigInteger must format as ''0''');
    end;

    [Test]
    procedure BigInteger_Format_LargeValue()
    var
        BigInt: BigInteger;
        Result: Text;
    begin
        BigInt := 9223372036854775807;  // Int64.MaxValue
        Result := Format(BigInt);
        Assert.AreEqual('9223372036854775807', Result, 'Max Int64 BigInteger must format correctly');
    end;

    [Test]
    procedure BigInteger_Format_NegativeLargeValue()
    var
        BigInt: BigInteger;
        Result: Text;
    begin
        BigInt := -9223372036854775807;
        Result := Format(BigInt);
        Assert.AreEqual('-9223372036854775807', Result, 'Large negative BigInteger must format correctly');
    end;
}
