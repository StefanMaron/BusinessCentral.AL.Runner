codeunit 87001 "JAG Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JAG Src";

    // -----------------------------------------------------------------------
    // Positive: Get integer value by index
    // -----------------------------------------------------------------------

    [Test]
    procedure Get_Integer_ReturnsCorrectValue()
    var
        JA: JsonArray;
    begin
        JA.Add(42);
        Assert.AreEqual(42, Src.GetIntAt(JA, 0), 'Get(0) must return 42');
    end;

    [Test]
    procedure Get_MultipleIntegers_ReturnsByIndex()
    var
        JA: JsonArray;
    begin
        JA.Add(10);
        JA.Add(20);
        JA.Add(30);
        Assert.AreEqual(10, Src.GetIntAt(JA, 0), 'Get(0) must return 10');
        Assert.AreEqual(20, Src.GetIntAt(JA, 1), 'Get(1) must return 20');
        Assert.AreEqual(30, Src.GetIntAt(JA, 2), 'Get(2) must return 30');
    end;

    // -----------------------------------------------------------------------
    // Positive: Get text value by index
    // -----------------------------------------------------------------------

    [Test]
    procedure Get_Text_ReturnsCorrectValue()
    var
        JA: JsonArray;
    begin
        JA.Add('hello');
        Assert.AreEqual('hello', Src.GetTextAt(JA, 0), 'Get(0) must return ''hello''');
    end;

    [Test]
    procedure Get_MultipleTexts_ReturnsByIndex()
    var
        JA: JsonArray;
    begin
        JA.Add('first');
        JA.Add('second');
        Assert.AreEqual('first', Src.GetTextAt(JA, 0), 'Get(0) must return ''first''');
        Assert.AreEqual('second', Src.GetTextAt(JA, 1), 'Get(1) must return ''second''');
    end;

    // -----------------------------------------------------------------------
    // Positive: Get boolean value by index
    // -----------------------------------------------------------------------

    [Test]
    procedure Get_Boolean_True_ReturnsTrue()
    var
        JA: JsonArray;
    begin
        JA.Add(true);
        Assert.IsTrue(Src.GetBoolAt(JA, 0), 'Get(0) must return true');
    end;

    [Test]
    procedure Get_Boolean_False_ReturnsFalse()
    var
        JA: JsonArray;
    begin
        JA.Add(false);
        Assert.IsFalse(Src.GetBoolAt(JA, 0), 'Get(0) must return false');
    end;

    // -----------------------------------------------------------------------
    // Positive: Get decimal value by index
    // -----------------------------------------------------------------------

    [Test]
    procedure Get_Decimal_ReturnsCorrectValue()
    var
        JA: JsonArray;
    begin
        JA.Add(3.14);
        Assert.AreEqual(3.14, Src.GetDecimalAt(JA, 0), 'Get(0) must return 3.14');
    end;

    // -----------------------------------------------------------------------
    // Positive: Get returns true on success
    // -----------------------------------------------------------------------

    [Test]
    procedure Get_ReturnsTrue_WhenIndexExists()
    var
        JA: JsonArray;
        JT: JsonToken;
    begin
        JA.Add('test');
        Assert.IsTrue(Src.GetAt(JA, 0, JT), 'Get must return true for valid index');
    end;

    // -----------------------------------------------------------------------
    // Negative: out-of-bounds raises error
    // -----------------------------------------------------------------------

    [Test]
    procedure Get_OutOfBounds_RaisesError()
    var
        JA: JsonArray;
    begin
        asserterror Src.GetIntAt(JA, 0);
        Assert.ExpectedError('');
    end;

    [Test]
    procedure Get_NegativeIndex_RaisesError()
    var
        JA: JsonArray;
    begin
        JA.Add(1);
        asserterror Src.GetIntAt(JA, -1);
        Assert.ExpectedError('');
    end;
}
