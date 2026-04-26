codeunit 56611 "EN Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Enum.Names — positive tests
    // -----------------------------------------------------------------------

    [Test]
    procedure EnumNamesReturnsThree()
    var
        Probe: Codeunit "EN Probe";
    begin
        Assert.AreEqual(3, Probe.NamesCount(), 'EN Stage has three declared members');
    end;

    [Test]
    procedure EnumNamesFirstIsDraft()
    var
        Probe: Codeunit "EN Probe";
    begin
        Assert.AreEqual('Draft', Probe.NamesFirst(), 'First declared member is Draft');
    end;

    [Test]
    procedure EnumNamesSecondIsReview()
    var
        Probe: Codeunit "EN Probe";
    begin
        // [GIVEN] EN Stage has Draft(0), Review(1), Published(2)
        // [WHEN] Names() is called and second element read
        // [THEN] Second element is 'Review'
        Assert.AreEqual('Review', Probe.NamesSecond(), 'Second declared member is Review');
    end;

    [Test]
    procedure EnumNamesThirdIsPublished()
    var
        Probe: Codeunit "EN Probe";
    begin
        Assert.AreEqual('Published', Probe.NamesThird(), 'Third declared member is Published');
    end;

    [Test]
    procedure EnumNamesContainsDraft()
    var
        Probe: Codeunit "EN Probe";
    begin
        // [GIVEN] Draft is a declared member of EN Stage
        // [THEN] Names().Contains('Draft') returns true
        Assert.IsTrue(Probe.NamesContains('Draft'), 'Names should contain Draft');
    end;

    [Test]
    procedure EnumNamesTypeQualifierSyntaxWorks()
    var
        Probe: Codeunit "EN Probe";
    begin
        // [GIVEN] Enum::"EN Stage".Names() (static type-qualifier form)
        // [THEN] Returns same count as instance form
        Assert.AreEqual(3, Probe.NamesTypeQualifierCount(), 'Type-qualifier syntax should return 3 names');
    end;

    // -----------------------------------------------------------------------
    // Enum.Names — negative tests
    // -----------------------------------------------------------------------

    [Test]
    procedure EnumNamesDoesNotContainUnknownMember()
    var
        Probe: Codeunit "EN Probe";
    begin
        // [GIVEN] 'Approved' is not a member of EN Stage
        // [THEN] Names().Contains('Approved') returns false
        Assert.IsFalse(Probe.NamesContains('Approved'), 'Names should not contain Approved');
    end;

    [Test]
    procedure EnumNamesCountIsNotTwo()
    var
        Probe: Codeunit "EN Probe";
    begin
        // A no-op implementation returning an empty/short list would fail this
        Assert.AreNotEqual(2, Probe.NamesCount(), 'Count must not be 2 — EN Stage has exactly 3 members');
    end;
}
