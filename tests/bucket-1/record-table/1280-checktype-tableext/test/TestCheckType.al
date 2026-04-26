codeunit 1280002 "Test CheckType"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CheckType_DiscountNonZero_ReturnsAmount()
    var
        Rec: Record "CheckType Base Table";
        Logic: Codeunit "CheckType Logic";
        Result: Decimal;
    begin
        // Positive: non-zero discount % returns computed discount amount
        Rec.Init();
        Rec."No." := 'CT01';
        Rec."Amount" := 200;
        Rec."Line Discount %" := 10;
        Rec.Insert(true);

        Rec.Get('CT01');
        Result := Logic.CalcDiscountAmount(Rec);
        Assert.AreEqual(20, Result, 'Discount on 200 at 10% should be 20');
    end;

    [Test]
    procedure CheckType_DiscountZero_ReturnsZero()
    var
        Rec: Record "CheckType Base Table";
        Logic: Codeunit "CheckType Logic";
        Result: Decimal;
    begin
        // Positive: zero discount % returns 0
        Rec.Init();
        Rec."No." := 'CT02';
        Rec."Amount" := 500;
        Rec."Line Discount %" := 0;
        Rec.Insert(true);

        Rec.Get('CT02');
        Result := Logic.CalcDiscountAmount(Rec);
        Assert.AreEqual(0, Result, 'Zero discount should return 0');
    end;

    [Test]
    procedure CheckType_ExtraCodeNonEmpty_ReturnsTrue()
    var
        Rec: Record "CheckType Base Table";
        Logic: Codeunit "CheckType Logic";
    begin
        // Positive: non-empty Extra Code returns true
        Rec.Init();
        Rec."No." := 'CT03';
        Rec."Extra Code" := 'ABC';
        Rec.Insert(true);

        Rec.Get('CT03');
        Assert.IsTrue(Logic.HasExtraCode(Rec), 'Should return true for non-empty Extra Code');
    end;

    [Test]
    procedure CheckType_ExtraCodeEmpty_ReturnsFalse()
    var
        Rec: Record "CheckType Base Table";
        Logic: Codeunit "CheckType Logic";
    begin
        // Negative: empty Extra Code returns false
        Rec.Init();
        Rec."No." := 'CT04';
        Rec.Insert(true);

        Rec.Get('CT04');
        Assert.IsFalse(Logic.HasExtraCode(Rec), 'Should return false for empty Extra Code');
    end;
}
