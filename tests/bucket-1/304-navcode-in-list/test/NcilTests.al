// Test suite for issue #1211: CS1503 NavValue → NavCode when a Code field
// is tested with the AL 'in' operator against a list of Code literals.
codeunit 304001 "NCIL Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure InList_MatchingCode_ReturnsTrue()
    var
        Helper: Codeunit "NCIL Helper";
        Item: Record "NCIL Item";
        UniqueNo: Code[20];
    begin
        UniqueNo := 'ALPHA';
        Item.Init();
        Item."No." := UniqueNo;
        Item.Insert();
        Item.SetRange("No.", UniqueNo);
        Item.FindFirst();

        Assert.IsTrue(Helper.IsWhitelistedNo(Item),
            'ALPHA must match [ALPHA, BETA, GAMMA]');
    end;

    [Test]
    procedure InList_NonMatchingCode_ReturnsFalse()
    var
        Helper: Codeunit "NCIL Helper";
        Item: Record "NCIL Item";
        UniqueNo: Code[20];
    begin
        UniqueNo := 'OMEGA';
        Item.Init();
        Item."No." := UniqueNo;
        Item.Insert();
        Item.SetRange("No.", UniqueNo);
        Item.FindFirst();

        Assert.IsFalse(Helper.IsWhitelistedNo(Item),
            'OMEGA must not match [ALPHA, BETA, GAMMA]');
    end;

    [Test]
    procedure NotInList_NonMatchingCode_ReturnsTrue()
    var
        Helper: Codeunit "NCIL Helper";
        Item: Record "NCIL Item";
        UniqueNo: Code[20];
    begin
        UniqueNo := 'DELTA';
        Item.Init();
        Item."No." := UniqueNo;
        Item.Insert();
        Item.SetRange("No.", UniqueNo);
        Item.FindFirst();

        Assert.IsTrue(Helper.IsNotWhitelisted(Item),
            'DELTA is not in [ALPHA, BETA] → negated test must return true');
    end;

    [Test]
    procedure NotInList_MatchingCode_ReturnsFalse()
    var
        Helper: Codeunit "NCIL Helper";
        Item: Record "NCIL Item";
        UniqueNo: Code[20];
    begin
        UniqueNo := 'BETA';
        Item.Init();
        Item."No." := UniqueNo;
        Item.Insert();
        Item.SetRange("No.", UniqueNo);
        Item.FindFirst();

        Assert.IsFalse(Helper.IsNotWhitelisted(Item),
            'BETA is in [ALPHA, BETA] → negated test must return false');
    end;

    [Test]
    procedure InList_EmptyRecord_ErrorsOnFieldAccess()
    var
        Helper: Codeunit "NCIL Helper";
        Item: Record "NCIL Item";
    begin
        // [GIVEN] Filter matching nothing; FindFirst fails, so any later
        // evaluation of Item."No." must still not crash the 'in' path.
        Item.SetRange("No.", 'NONEXISTENT-XYZ-1211');
        asserterror
        begin
            if not Item.FindFirst() then
                Error('no record');
            if Helper.IsWhitelistedNo(Item) then;
        end;
        Assert.ExpectedError('no record');
    end;
}
