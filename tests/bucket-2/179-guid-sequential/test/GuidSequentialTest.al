codeunit 60041 "GCSG Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "GCSG Src";

    [Test]
    procedure CreateSequentialGuid_ReturnsNonNullGuid()
    var
        g: Guid;
    begin
        // Standalone CreateSequentialGuid delegates to Guid.NewGuid so the result is
        // always a valid, non-zero GUID. The sequential-ordering guarantee of BC is
        // not modelled — only non-null and uniqueness matter for tests.
        g := Src.GetSequentialGuid();
        Assert.IsFalse(IsNullGuid(g),
            'CreateSequentialGuid must return a non-null GUID');
    end;

    [Test]
    procedure CreateSequentialGuid_TwoCallsReturnDifferent()
    var
        g1: Guid;
        g2: Guid;
    begin
        Src.GetTwoSequentialGuids(g1, g2);
        Assert.AreNotEqual(g1, g2,
            'Two successive CreateSequentialGuid calls must return different GUIDs');
    end;

    [Test]
    procedure CreateSequentialGuid_NotNullGuid_NegativeTrap()
    begin
        // Negative trap: if CreateSequentialGuid returned the default Guid{}, this
        // would be true. A real impl must never return the null GUID.
        Assert.IsFalse(Src.SequentialGuidIsNullGuid(),
            'SequentialGuid must not be the null/default GUID');
    end;
}
