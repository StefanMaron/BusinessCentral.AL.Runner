codeunit 61831 "DTI Database TenantId Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TenantId_ReturnsNonEmptyStub()
    var
        Src: Codeunit "DTI Src";
    begin
        // Positive: Database.TenantId() must return a non-empty stub string.
        Assert.AreNotEqual('', Src.GetTenantId(), 'TenantId must return a non-empty stub');
    end;

    [Test]
    procedure TenantId_IsZeroGuid()
    var
        Src: Codeunit "DTI Src";
    begin
        // Positive: stub must be the zero GUID — deterministic and detectable.
        Assert.AreEqual('00000000-0000-0000-0000-000000000000', Src.GetTenantId(), 'TenantId stub must be zero GUID');
    end;

    [Test]
    procedure TenantId_NotStandaloneString()
    var
        Src: Codeunit "DTI Src";
    begin
        // Negative: must NOT return generic "STANDALONE" (guards against wrong stub).
        Assert.AreNotEqual('STANDALONE', Src.GetTenantId(), 'TenantId stub must not be STANDALONE');
    end;

    [Test]
    procedure AddWithBonus_ProvingCompilationUnitLive()
    var
        Src: Codeunit "DTI Src";
    begin
        // Proving: the codeunit is live — real computation returns a+b+1.
        Assert.AreEqual(8, Src.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 8');
        Assert.AreEqual(1, Src.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 1');
    end;

    [Test]
    procedure AddWithBonus_NotPlainSum()
    var
        Src: Codeunit "DTI Src";
    begin
        // Negative: AddWithBonus must NOT return a plain sum.
        Assert.AreNotEqual(7, Src.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;
}
