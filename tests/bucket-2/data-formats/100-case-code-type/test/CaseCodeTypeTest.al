// Renumbered from 59201 to avoid collision in new bucket layout (#1385).
codeunit 1059201 "CCT Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "CCT Helper";

    // -----------------------------------------------------------------------
    // Positive: known category codes return correct labels
    // -----------------------------------------------------------------------

    [Test]
    procedure CategoryLabel_A_ReturnsPremium()
    begin
        // [GIVEN] Category code 'A'
        // [WHEN] CategoryLabel is called
        // [THEN] Returns 'Premium'
        Assert.AreEqual('Premium', Helper.CategoryLabel('A'),
            'CategoryLabel(A) must return Premium');
    end;

    [Test]
    procedure CategoryLabel_B_ReturnsStandard()
    begin
        // [GIVEN] Category code 'B'
        // [WHEN] CategoryLabel is called
        // [THEN] Returns 'Standard'
        Assert.AreEqual('Standard', Helper.CategoryLabel('B'),
            'CategoryLabel(B) must return Standard');
    end;

    [Test]
    procedure CategoryLabel_Unknown_ReturnsOther()
    begin
        // [GIVEN] An unknown category code
        // [WHEN] CategoryLabel is called
        // [THEN] Returns 'Other' (else branch)
        Assert.AreEqual('Other', Helper.CategoryLabel('Z'),
            'CategoryLabel(Z) must return Other');
    end;

    [Test]
    procedure CategoryLabel_Empty_ReturnsOther()
    begin
        // [GIVEN] Empty code
        // [WHEN] CategoryLabel is called
        // [THEN] Returns 'Other' (else branch, empty does not match 'A' or 'B')
        Assert.AreEqual('Other', Helper.CategoryLabel(''),
            'CategoryLabel empty must return Other');
    end;

    // -----------------------------------------------------------------------
    // Positive: Code[20] case works identically
    // -----------------------------------------------------------------------

    [Test]
    procedure StatusCode_Open_ReturnsOne()
    begin
        Assert.AreEqual(1, Helper.StatusCode('OPEN'), 'OPEN must return 1');
    end;

    [Test]
    procedure StatusCode_Closed_ReturnsTwo()
    begin
        Assert.AreEqual(2, Helper.StatusCode('CLOSED'), 'CLOSED must return 2');
    end;

    [Test]
    procedure StatusCode_Unknown_ReturnsZero()
    begin
        Assert.AreEqual(0, Helper.StatusCode('UNKNOWN'), 'Unknown code must return 0');
    end;
}
