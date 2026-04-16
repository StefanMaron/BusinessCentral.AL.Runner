codeunit 50967 "Text Builtins Test"
{
    Subtype = Test;

    var
        Helper: Codeunit "Text Builtins Helper";
        Assert: Codeunit Assert;

    [Test]
    procedure TestToLower()
    begin
        Assert.AreEqual('hello world', Helper.CallToLower('Hello World'), 'ToLower mixed case');
        Assert.AreEqual('abcdef', Helper.CallToLower('ABCDEF'), 'ToLower all upper');
        Assert.AreEqual('already lower', Helper.CallToLower('already lower'), 'ToLower already lower');
        Assert.AreEqual('', Helper.CallToLower(''), 'ToLower empty string');
        Assert.AreEqual('123!@#', Helper.CallToLower('123!@#'), 'ToLower non-letters unchanged');
    end;

    [Test]
    procedure TestToUpper()
    begin
        Assert.AreEqual('HELLO WORLD', Helper.CallToUpper('Hello World'), 'ToUpper mixed case');
        Assert.AreEqual('ABCDEF', Helper.CallToUpper('abcdef'), 'ToUpper all lower');
        Assert.AreEqual('ALREADY UPPER', Helper.CallToUpper('ALREADY UPPER'), 'ToUpper already upper');
        Assert.AreEqual('', Helper.CallToUpper(''), 'ToUpper empty string');
        Assert.AreEqual('123!@#', Helper.CallToUpper('123!@#'), 'ToUpper non-letters unchanged');
    end;

    [Test]
    procedure TestSubstring()
    begin
        // AL Substring is 1-based
        Assert.AreEqual('Hello', Helper.CallSubstring('Hello World', 1, 5), 'Substring from start');
        Assert.AreEqual('World', Helper.CallSubstring('Hello World', 7, 5), 'Substring at offset');
        Assert.AreEqual('o', Helper.CallSubstring('Hello World', 5, 1), 'Single char substring');
        Assert.AreEqual('Hello World', Helper.CallSubstring('Hello World', 1, 11), 'Full string substring');
    end;

    [Test]
    procedure TestSubstringFromStart()
    begin
        // Substring(start) returns from start to end
        Assert.AreEqual('World', Helper.CallSubstringFromStart('Hello World', 7), 'Substring to end');
        Assert.AreEqual('Hello World', Helper.CallSubstringFromStart('Hello World', 1), 'Substring from 1');
        Assert.AreEqual('d', Helper.CallSubstringFromStart('Hello World', 11), 'Last char');
    end;

    [Test]
    procedure TestSubstringInvalidRange()
    begin
        // AL Substring errors on out-of-range arguments
        asserterror Helper.CallSubstring('Hello', 10, 3);
    end;

    [Test]
    procedure TestPadStrRightFills()
    begin
        // Positive length = right-pad with space (default)
        Assert.AreEqual('Hello     ', Helper.CallPadStr('Hello', 10), 'PadStr right-pad default');
        Assert.AreEqual('Hello     ', Helper.CallPadStrRight('Hello', 10), 'PadStr right-pad explicit');
    end;

    [Test]
    procedure TestPadStrTruncates()
    begin
        // When input is longer than length, truncate (no padding)
        Assert.AreEqual('Hello', Helper.CallPadStr('Hello World', 5), 'PadStr truncates long input');
    end;

    [Test]
    procedure TestPadStrLeftFills()
    begin
        // Negative length = left-pad with space (default)
        Assert.AreEqual('     Hello', Helper.CallPadStrLeft('Hello', 10), 'PadStr left-pad');
    end;

    [Test]
    procedure TestPadStrWithChar()
    var
        Star: Char;
    begin
        Star := 42; // '*'
        Assert.AreEqual('Hello*****', Helper.CallPadStrChar('Hello', 10, Star), 'PadStr right-pad with char');
        Assert.AreEqual('*****Hello', Helper.CallPadStrChar('Hello', -10, Star), 'PadStr left-pad with char');
    end;

    [Test]
    procedure TestPadStrLeftTruncates()
    begin
        // Negative length with over-length input should still truncate
        Assert.AreEqual('Hello', Helper.CallPadStrLeft('Hello World', 5), 'PadStr negative length truncates');
    end;

    [Test]
    procedure TestStrPos()
    begin
        // StrPos is 1-based, 0 = not found
        Assert.AreEqual(1, Helper.CallStrPos('Hello World', 'Hello'), 'StrPos match at start');
        Assert.AreEqual(7, Helper.CallStrPos('Hello World', 'World'), 'StrPos match middle');
        Assert.AreEqual(2, Helper.CallStrPos('Hello', 'ell'), 'StrPos match middle 2');
        Assert.AreEqual(0, Helper.CallStrPos('Hello', 'xyz'), 'StrPos no match returns 0');
        Assert.AreEqual(0, Helper.CallStrPos('', 'a'), 'StrPos empty haystack');
    end;

    [Test]
    procedure TestIndexOfAny()
    begin
        // 1-based, 0 if not found
        Assert.AreEqual(3, Helper.CallIndexOfAny('Hello World', 'lo'), 'IndexOfAny first l');
        Assert.AreEqual(6, Helper.CallIndexOfAny('Hello World', ' '), 'IndexOfAny space');
        Assert.AreEqual(0, Helper.CallIndexOfAny('Hello', 'xyz'), 'IndexOfAny no match');
    end;

    [Test]
    procedure TestIndexOfAnyWithStart()
    begin
        // Search from given 1-based start index
        Assert.AreEqual(4, Helper.CallIndexOfAnyStart('Hello World', 'lo', 4), 'IndexOfAny from 4');
        Assert.AreEqual(5, Helper.CallIndexOfAnyStart('Hello World', 'lo', 5), 'IndexOfAny from 5');
        Assert.AreEqual(8, Helper.CallIndexOfAnyStart('Hello World', 'lo', 6), 'IndexOfAny from 6');
    end;

    [Test]
    procedure TestStrCheckSum()
    begin
        // BC StrCheckSum default modulus is 10 (not 11).
        // Formula: (10 - (sum(digit[i]*weight[i]) mod 10)) mod 10
        // '12' weights '34': 1*3 + 2*4 = 11; 11 mod 10 = 1; (10 - 1) mod 10 = 9
        Assert.AreEqual(9, Helper.CallStrCheckSum('12', '34'), 'StrCheckSum 12,34');
        // '123' weights '111': 1+2+3 = 6; (10 - 6) mod 10 = 4
        Assert.AreEqual(4, Helper.CallStrCheckSum('123', '111'), 'StrCheckSum 123,111 weights-all-1');
        // '0' weights '1': 0; (10 - 0) mod 10 = 0
        Assert.AreEqual(0, Helper.CallStrCheckSum('0', '1'), 'StrCheckSum zero input');
    end;

    [Test]
    procedure TestStrCheckSumBarcode()
    begin
        // EAN-13 style: digits '590123412345' with alternating weights '131313131313'.
        // sum = 5+27+0+3+2+9+4+3+2+9+4+15 = 83; 83 mod 10 = 3; (10 - 3) mod 10 = 7
        Assert.AreEqual(7, Helper.CallStrCheckSum('590123412345', '131313131313'), 'StrCheckSum EAN-13 style');
    end;
}
