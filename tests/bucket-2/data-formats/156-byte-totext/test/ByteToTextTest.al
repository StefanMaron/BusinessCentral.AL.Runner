codeunit 50157 "BTT Tests"
{
    Subtype = Test;

    var
        Helper: Codeunit "BTT Helper";
        Assert: Codeunit "Library Assert";

    [Test]
    procedure ByteToText_42_Returns42()
    var
        B: Byte;
    begin
        B := 42;
        Assert.AreEqual('42', Helper.ByteToText(B), 'Byte 42 must format as ''42''');
    end;

    [Test]
    procedure ByteToText_0_Returns0()
    var
        B: Byte;
    begin
        B := 0;
        Assert.AreEqual('0', Helper.ByteToText(B), 'Byte 0 must format as ''0''');
    end;

    [Test]
    procedure ByteToText_255_Returns255()
    var
        B: Byte;
    begin
        B := 255;
        Assert.AreEqual('255', Helper.ByteToText(B), 'Byte 255 must format as ''255''');
    end;

    [Test]
    procedure ByteToText_NotEmpty()
    var
        B: Byte;
    begin
        B := 100;
        Assert.IsFalse(Helper.IsEmpty(B), 'Non-zero byte must not produce empty text');
    end;

    [Test]
    procedure ByteToText_1_Returns1()
    var
        B: Byte;
    begin
        B := 1;
        Assert.AreEqual('1', Helper.ByteToText(B), 'Byte 1 must format as ''1''');
    end;

    [Test]
    procedure ByteToText_NotEqual_DifferentValues()
    var
        B1: Byte;
        B2: Byte;
    begin
        B1 := 10;
        B2 := 20;
        Assert.AreNotEqual(Helper.ByteToText(B1), Helper.ByteToText(B2), 'Different byte values must produce different text');
    end;
}
