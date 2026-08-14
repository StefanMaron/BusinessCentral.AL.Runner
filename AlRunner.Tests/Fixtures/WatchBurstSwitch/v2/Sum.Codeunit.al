/// <summary>Version B: sums F0..F5 (each 10) and expects 60.</summary>
codeunit 60210 "Burst Sum Tests RXT"
{
    Subtype = Test;

    var
        Assert: Codeunit "Burst Assert RXT";
        F0: Codeunit "Burst F0 RXT";
        F1: Codeunit "Burst F1 RXT";
        F2: Codeunit "Burst F2 RXT";
        F3: Codeunit "Burst F3 RXT";
        F4: Codeunit "Burst F4 RXT";
        F5: Codeunit "Burst F5 RXT";

    [Test]
    procedure Sum_OfAllValues_MatchesExpectedTotal()
    var
        Total: Integer;
    begin
        // [GIVEN] six addend codeunits
        // [WHEN] their values are summed
        Total := F0.GetValue() + F1.GetValue() + F2.GetValue() + F3.GetValue()
            + F4.GetValue() + F5.GetValue();

        // [THEN] the total matches this version's expectation — see v1/Sum.Codeunit.al's
        // header for why every addend AND this expectation living in separate files
        // matters for the #1904 reproduction.
        Assert.AreEqual('60', Format(Total),
            'sum of six addends did not match version B''s expected total');
    end;
}
