codeunit 59541 "USI Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "USI Src";

    [Test]
    procedure UserSecurityId_ReadCompletes()
    var
        g: Guid;
    begin
        // Positive: the read must complete and return a Guid.
        g := Src.GetUserSecurityId();
        Assert.IsTrue(true, 'Reading Database.UserSecurityId must not throw');
    end;

    [Test]
    procedure UserSecurityId_IsNonNullGuid()
    begin
        // Positive: the returned Guid must be non-null — a null-guid stub would
        // match AL's default-initialised `Guid` value and be useless for downstream
        // auditing/filter logic.
        Assert.IsTrue(Src.IsUserSecurityIdNonNull(),
            'Database.UserSecurityId must be a non-null Guid');
    end;

    [Test]
    procedure UserSecurityId_IsStable()
    begin
        // Two consecutive reads must return the same Guid — the stub must be
        // a fixed value, not freshly generated per call.
        Assert.IsTrue(Src.BothReadsEqual(),
            'Two consecutive reads of UserSecurityId must return the same Guid');
    end;

    [Test]
    procedure UserSecurityId_NotNullGuidLiteral_NegativeTrap()
    var
        g: Guid;
        nullGuid: Guid;
    begin
        // Negative: guard against a stub that returns the null Guid {00000000-...}.
        g := Src.GetUserSecurityId();
        Clear(nullGuid);
        Assert.AreNotEqual(nullGuid, g,
            'Database.UserSecurityId must differ from the zero/null Guid');
    end;
}
