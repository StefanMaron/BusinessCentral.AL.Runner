/// <summary>
/// Version A: sums F0..F5 (each 1) and expects 6. WatchBurstSwitchTests overwrites this
/// file's expected total to 60 (matching version B's addends) as the LAST write of the
/// burst switch — so a watch cycle that fires before every F-file AND this file have all
/// settled to version B reads a mismatched mix and reports a phantom FAIL, even though
/// both version A (all files at 1/6) and version B (all files at 10/60), fully applied,
/// pass on their own.
/// </summary>
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

        // [THEN] the total matches this version's expectation. Every addend AND this
        // expected value live in SEPARATE files, so a half-applied file set (some
        // addends switched, some not, or this file switched but an addend not) sums to
        // neither this version's total nor the other version's — a value that cannot
        // occur once the whole switch has settled.
        Assert.AreEqual('6', Format(Total),
            'sum of six addends did not match version A''s expected total');
    end;
}
