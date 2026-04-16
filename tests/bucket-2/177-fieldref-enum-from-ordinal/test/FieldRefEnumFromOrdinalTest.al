codeunit 60021 "FREO Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "FREO Src";

    [Test]
    procedure GetEnumValueCaptionFromOrdinal_Zero()
    begin
        // Enum: 0 → Open; 5 → InProgress; 10 → Closed.
        Assert.AreEqual('Open', Src.CaptionForOrdinal(0),
            'Caption for ordinal 0 must be "Open"');
    end;

    [Test]
    procedure GetEnumValueCaptionFromOrdinal_Middle()
    begin
        // Note: EnumRegistry captures names, not captions, so standalone Caption
        // returns the AL identifier ("InProgress"), not the display caption
        // ("In Progress"). Documenting the runner behaviour.
        Assert.AreEqual('InProgress', Src.CaptionForOrdinal(5),
            'Caption for ordinal 5 must be "InProgress" (standalone treats caption=name)');
    end;

    [Test]
    procedure GetEnumValueCaptionFromOrdinal_Last()
    begin
        Assert.AreEqual('Closed', Src.CaptionForOrdinal(10),
            'Caption for ordinal 10 must be "Closed"');
    end;

    [Test]
    procedure GetEnumValueNameFromOrdinal_Zero()
    begin
        // Enum member names (distinct from captions).
        Assert.AreEqual('Open', Src.NameForOrdinal(0),
            'Name for ordinal 0 must be "Open"');
    end;

    [Test]
    procedure GetEnumValueNameFromOrdinal_Middle()
    begin
        // Name uses the AL identifier ("InProgress"), while Caption is the display text.
        Assert.AreEqual('InProgress', Src.NameForOrdinal(5),
            'Name for ordinal 5 must be "InProgress"');
    end;

    [Test]
    procedure GetEnumValue_Ordinal_IsNotIndex_NegativeTrap()
    begin
        // Negative: guard against accidentally using 1-based INDEX instead of ORDINAL.
        // Ordinals are 0, 5, 10 — if the impl mistakenly treats the arg as a 1-based
        // index, passing 5 would produce "Closed" (the 5th item doesn't exist, so
        // implementations often return the last). Ordinal 5 must produce "InProgress"
        // per the enum definition.
        Assert.AreNotEqual('Closed', Src.CaptionForOrdinal(5),
            'Ordinal 5 must resolve to "In Progress", not the last/index-based entry');
    end;
}
