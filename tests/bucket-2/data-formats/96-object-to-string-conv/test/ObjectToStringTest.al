codeunit 305011 "Obj To Str Test"
{
    Subtype = Test;

    [Test]
    procedure StrSubstNoFromVariant_ReturnsFormatted()
    var
        Helper: Codeunit "Obj To Str Helper";
        Result: Text;
    begin
        // Variant holding format string + variant arg passed to StrSubstNo
        Result := Helper.StrSubstNoFromVariant('Hello %1', 'World');
        Assert.AreEqual('Hello World', Result, 'StrSubstNo via Variant should format correctly');
    end;

    [Test]
    procedure ErrorFromVariant_RaisesExpectedError()
    var
        Helper: Codeunit "Obj To Str Helper";
    begin
        asserterror Helper.ErrorFromVariant('Variant error message');
        Assert.ExpectedError('Variant error message');
    end;

    [Test]
    procedure ErrorFromVariantFmt_RaisesFormattedError()
    var
        Helper: Codeunit "Obj To Str Helper";
    begin
        asserterror Helper.ErrorFromVariantFmt('Error: %1', 'detail');
        Assert.ExpectedError('Error: detail');
    end;

    [Test]
    procedure CopyStrFromVariant_ReturnsCopied()
    var
        Helper: Codeunit "Obj To Str Helper";
        Result: Text;
    begin
        // CopyStr(S, 2, 3) on 'ABCDEF' → 'BCD'
        Result := Helper.CopyStrFromVariant('ABCDEF');
        Assert.AreEqual('BCD', Result, 'CopyStr via Variant should return substring');
    end;

    [Test]
    procedure LowerFromVariant_ReturnsLowerCase()
    var
        Helper: Codeunit "Obj To Str Helper";
        Result: Text;
    begin
        Result := Helper.LowerFromVariant('HELLO');
        Assert.AreEqual('hello', Result, 'LowerCase via Variant should return lowercase');
    end;

    [Test]
    procedure UpperFromVariant_ReturnsUpperCase()
    var
        Helper: Codeunit "Obj To Str Helper";
        Result: Text;
    begin
        Result := Helper.UpperFromVariant('hello');
        Assert.AreEqual('HELLO', Result, 'UpperCase via Variant should return uppercase');
    end;

    [Test]
    procedure PadStrFromVariant_ReturnsPadded()
    var
        Helper: Codeunit "Obj To Str Helper";
        Result: Text;
    begin
        // PadStr('AB', 8) → 'AB      ' (padded to 8)
        Result := Helper.PadStrFromVariant('AB');
        Assert.AreEqual(8, StrLen(Result), 'PadStr via Variant should pad to length');
    end;

    [Test]
    procedure IncStrFromVariant_ReturnsIncremented()
    var
        Helper: Codeunit "Obj To Str Helper";
        Result: Text;
    begin
        Result := Helper.IncStrFromVariant('ABC1');
        Assert.AreEqual('ABC2', Result, 'IncStr via Variant should increment number');
    end;

    [Test]
    procedure DelChrFromVariant_ReturnsFiltered()
    var
        Helper: Codeunit "Obj To Str Helper";
        Result: Text;
    begin
        // DelChr('A B C', '=', ' ') → 'ABC'
        Result := Helper.DelChrFromVariant('A B C');
        Assert.AreEqual('ABC', Result, 'DelChr via Variant should remove characters');
    end;

    [Test]
    procedure SetFilterFromVariantExpr_Compiles()
    var
        Helper: Codeunit "Obj To Str Helper";
        Rec: Record "Obj To Str Table";
    begin
        // Just verify it compiles and does not crash
        Helper.SetFilterFromVariantExpr(Rec, 'ABC');
    end;

    [Test]
    procedure SetFilterFromVariantExprAndArg_FindsRecord()
    var
        Helper: Codeunit "Obj To Str Helper";
        Rec: Record "Obj To Str Table";
    begin
        Rec.Init();
        Rec."Code" := 'TEST';
        Rec.Description := 'Test Record';
        Rec.Insert();

        Helper.SetFilterFromVariantExprAndArg(Rec, '%1', 'TEST');
        Assert.IsTrue(Rec.FindFirst(), 'Should find record via Variant filter');
        Assert.AreEqual('TEST', Rec."Code", 'Found wrong record');
    end;

    var
        Assert: Codeunit Assert;
}
