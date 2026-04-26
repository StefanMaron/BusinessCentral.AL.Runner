codeunit 308402 "GetRange Date Filter Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── Date field ────────────────────────────────────────────────────────────

    [Test]
    procedure GetRangeMin_DateField_ReturnsMinBound()
    var
        Rec: Record "GetRange Filter Table";
        Helper: Codeunit "GetRange Filter Helper";
        MinDate: Date;
        Expected: Date;
    begin
        // Arrange — 15 Jun 2024 is a non-default value that proves the filter bound is returned,
        // not the type default (0D).
        Expected := DMY2Date(15, 6, 2024);
        Rec.SetRange("Profile Date", DMY2Date(15, 6, 2024), DMY2Date(31, 12, 2024));

        // Act
        MinDate := Helper.GetMinDate(Rec);

        // Assert
        Assert.AreEqual(Expected, MinDate, 'GetRangeMin(Date) should return the lower filter bound');
    end;

    [Test]
    procedure GetRangeMax_DateField_ReturnsMaxBound()
    var
        Rec: Record "GetRange Filter Table";
        Helper: Codeunit "GetRange Filter Helper";
        MaxDate: Date;
        Expected: Date;
    begin
        // Arrange — 31 Dec 2024 is a non-default value that proves the filter bound is returned.
        Expected := DMY2Date(31, 12, 2024);
        Rec.SetRange("Profile Date", DMY2Date(15, 6, 2024), DMY2Date(31, 12, 2024));

        // Act
        MaxDate := Helper.GetMaxDate(Rec);

        // Assert
        Assert.AreEqual(Expected, MaxDate, 'GetRangeMax(Date) should return the upper filter bound');
    end;

    [Test]
    procedure GetRangeMin_DateField_NotMax()
    var
        Rec: Record "GetRange Filter Table";
        Helper: Codeunit "GetRange Filter Helper";
    begin
        // Negative: GetRangeMin must NOT return the upper bound.
        Rec.SetRange("Profile Date", DMY2Date(1, 1, 2024), DMY2Date(31, 12, 2024));
        Assert.AreNotEqual(DMY2Date(31, 12, 2024), Helper.GetMinDate(Rec), 'GetRangeMin(Date) must not return the upper bound');
    end;

    [Test]
    procedure GetRangeMax_DateField_NotMin()
    var
        Rec: Record "GetRange Filter Table";
        Helper: Codeunit "GetRange Filter Helper";
    begin
        // Negative: GetRangeMax must NOT return the lower bound.
        Rec.SetRange("Profile Date", DMY2Date(1, 1, 2024), DMY2Date(31, 12, 2024));
        Assert.AreNotEqual(DMY2Date(1, 1, 2024), Helper.GetMaxDate(Rec), 'GetRangeMax(Date) must not return the lower bound');
    end;

    // ── Text field ────────────────────────────────────────────────────────────

    [Test]
    procedure GetRangeMin_TextField_ReturnsMinBound()
    var
        Rec: Record "GetRange Filter Table";
        Helper: Codeunit "GetRange Filter Helper";
        MinText: Text[100];
    begin
        Rec.SetRange("Description", 'Apple', 'Mango');
        MinText := Helper.GetMinText(Rec);
        Assert.AreEqual('Apple', MinText, 'GetRangeMin(Text) should return the lower filter bound');
    end;

    [Test]
    procedure GetRangeMax_TextField_ReturnsMaxBound()
    var
        Rec: Record "GetRange Filter Table";
        Helper: Codeunit "GetRange Filter Helper";
        MaxText: Text[100];
    begin
        Rec.SetRange("Description", 'Apple', 'Mango');
        MaxText := Helper.GetMaxText(Rec);
        Assert.AreEqual('Mango', MaxText, 'GetRangeMax(Text) should return the upper filter bound');
    end;

    [Test]
    procedure GetRangeMin_TextField_NotMax()
    var
        Rec: Record "GetRange Filter Table";
        Helper: Codeunit "GetRange Filter Helper";
    begin
        Rec.SetRange("Description", 'Apple', 'Mango');
        Assert.AreNotEqual('Mango', Helper.GetMinText(Rec), 'GetRangeMin(Text) must not return the upper bound');
    end;

    // ── Code field ────────────────────────────────────────────────────────────

    [Test]
    procedure GetRangeMin_CodeField_ReturnsMinBound()
    var
        Rec: Record "GetRange Filter Table";
        Helper: Codeunit "GetRange Filter Helper";
        MinCode: Code[20];
    begin
        Rec.SetRange("Code Field", 'AAA', 'ZZZ');
        MinCode := Helper.GetMinCode(Rec);
        Assert.AreEqual('AAA', MinCode, 'GetRangeMin(Code) should return the lower filter bound');
    end;

    [Test]
    procedure GetRangeMax_CodeField_ReturnsMaxBound()
    var
        Rec: Record "GetRange Filter Table";
        Helper: Codeunit "GetRange Filter Helper";
        MaxCode: Code[20];
    begin
        Rec.SetRange("Code Field", 'AAA', 'ZZZ');
        MaxCode := Helper.GetMaxCode(Rec);
        Assert.AreEqual('ZZZ', MaxCode, 'GetRangeMax(Code) should return the upper filter bound');
    end;

    [Test]
    procedure GetRangeMin_CodeField_NotMax()
    var
        Rec: Record "GetRange Filter Table";
        Helper: Codeunit "GetRange Filter Helper";
    begin
        Rec.SetRange("Code Field", 'AAA', 'ZZZ');
        Assert.AreNotEqual('ZZZ', Helper.GetMinCode(Rec), 'GetRangeMin(Code) must not return the upper bound');
    end;

    // ── Boolean field ─────────────────────────────────────────────────────────

    [Test]
    procedure GetRangeMin_BoolField_ReturnsFalse()
    var
        Rec: Record "GetRange Filter Table";
        Helper: Codeunit "GetRange Filter Helper";
        MinBool: Boolean;
    begin
        Rec.SetRange("Active", false, true);
        MinBool := Helper.GetMinBool(Rec);
        Assert.IsFalse(MinBool, 'GetRangeMin(Boolean) should return false (lower bound)');
    end;

    [Test]
    procedure GetRangeMax_BoolField_ReturnsTrue()
    var
        Rec: Record "GetRange Filter Table";
        Helper: Codeunit "GetRange Filter Helper";
        MaxBool: Boolean;
    begin
        Rec.SetRange("Active", false, true);
        MaxBool := Helper.GetMaxBool(Rec);
        Assert.IsTrue(MaxBool, 'GetRangeMax(Boolean) should return true (upper bound)');
    end;

    [Test]
    procedure GetRangeMax_BoolField_NotMin()
    var
        Rec: Record "GetRange Filter Table";
        Helper: Codeunit "GetRange Filter Helper";
    begin
        Rec.SetRange("Active", false, true);
        Assert.AreNotEqual(false, Helper.GetMaxBool(Rec), 'GetRangeMax(Boolean) must not return the lower bound false');
    end;
}
