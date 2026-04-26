// Test suite for issue #1185: NavValue to NavCode conversion when Code field values
// are used in List of [Code[N]] operations. The BC compiler emits NavValue for some
// Code field accesses; NavList<NavCode> operations must handle NavValue arguments.
// Fix: NavIndirectValueToNavValue<NavCode> in AlCompat handles NavValue→NavCode coercion.
codeunit 234001 "NVL Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CollectCategoryCodes_TwoItems_ReturnsBothCodes()
    var
        Helper: Codeunit "NVL Helper";
        Rec: Record "NVL Item";
        Codes: List of [Code[20]];
        UniqueNo1: Code[20];
        UniqueNo2: Code[20];
    begin
        // [GIVEN] Two items with different Category codes (unique keys to avoid cross-test pollution)
        UniqueNo1 := 'C1-' + Format(Random(9999));
        UniqueNo2 := 'C2-' + Format(Random(9999));

        Rec.Init();
        Rec."No." := UniqueNo1;
        Rec."Category" := 'PREM';
        Rec.Insert();

        Rec.Init();
        Rec."No." := UniqueNo2;
        Rec."Category" := 'STD';
        Rec.Insert();

        Rec.SetFilter("No.", '%1|%2', UniqueNo1, UniqueNo2);

        // [WHEN] CollectCategoryCodes — adds Code field values to List of [Code[20]]
        Codes := Helper.CollectCategoryCodes(Rec);

        // [THEN] Both category codes are in the list
        Assert.AreEqual(2, Codes.Count(), 'List must contain 2 codes');
        Assert.IsTrue(Codes.Contains('PREM'), 'List must contain PREM');
        Assert.IsTrue(Codes.Contains('STD'), 'List must contain STD');
    end;

    [Test]
    procedure CollectCategoryCodes_EmptyFilter_ReturnsEmptyList()
    var
        Helper: Codeunit "NVL Helper";
        Rec: Record "NVL Item";
        Codes: List of [Code[20]];
    begin
        // [GIVEN] Filter that matches nothing
        Rec.SetRange("No.", 'NONEXISTENT-XYZ');

        // [WHEN] CollectCategoryCodes is called
        Codes := Helper.CollectCategoryCodes(Rec);

        // [THEN] Empty list
        Assert.AreEqual(0, Codes.Count(), 'Filtered-out record set must return empty list');
    end;

    [Test]
    procedure BuildCodeList_TwoCodes_ReturnsExpected()
    var
        Helper: Codeunit "NVL Helper";
        Codes: List of [Code[20]];
    begin
        // [GIVEN] Two Code parameters
        Codes := Helper.BuildCodeList('ALPHA', 'BETA');

        // [THEN] Both appear in list
        Assert.AreEqual(2, Codes.Count(), 'List must contain 2 codes');
        Assert.IsTrue(Codes.Contains('ALPHA'), 'List must contain ALPHA');
        Assert.IsTrue(Codes.Contains('BETA'), 'List must contain BETA');
    end;

    [Test]
    procedure ListContainsCode_FindsMatchingCode()
    var
        Helper: Codeunit "NVL Helper";
        Rec: Record "NVL Item";
        UniqueNo: Code[20];
    begin
        // [GIVEN] An item with Category=FINDME
        UniqueNo := 'F-' + Format(Random(9999));
        Rec.Init();
        Rec."No." := UniqueNo;
        Rec."Category" := 'FINDME';
        Rec.Insert();
        Rec.SetFilter("No.", UniqueNo);

        // [WHEN/THEN] Contains finds the code
        Assert.IsTrue(Helper.ListContainsCode(Rec, 'FINDME'),
            'Contains must find FINDME');
    end;

    [Test]
    procedure ListContainsCode_DoesNotFindMissingCode()
    var
        Helper: Codeunit "NVL Helper";
        Rec: Record "NVL Item";
        UniqueNo: Code[20];
    begin
        // [GIVEN] An item with Category=REAL
        UniqueNo := 'R-' + Format(Random(9999));
        Rec.Init();
        Rec."No." := UniqueNo;
        Rec."Category" := 'REAL';
        Rec.Insert();
        Rec.SetFilter("No.", UniqueNo);

        // [WHEN/THEN] Contains does NOT find a different code
        Assert.IsFalse(Helper.ListContainsCode(Rec, 'FAKE'),
            'Contains must not find FAKE when only REAL is in list');
    end;

    [Test]
    procedure IndexOfCode_ReturnsCorrectIndex()
    var
        Helper: Codeunit "NVL Helper";
        Rec: Record "NVL Item";
        UniqueNo: Code[20];
        Result: Integer;
    begin
        // [GIVEN] One item with Category=ONLY
        UniqueNo := 'I-' + Format(Random(9999));
        Rec.Init();
        Rec."No." := UniqueNo;
        Rec."Category" := 'ONLY';
        Rec.Insert();
        Rec.SetFilter("No.", UniqueNo);

        // [WHEN] IndexOf called with 'ONLY'
        Result := Helper.IndexOfCode(Rec, 'ONLY');

        // [THEN] Returns 1 (1-based in AL)
        Assert.AreEqual(1, Result, 'IndexOf must return 1 for the only element');
    end;
}
