codeunit 84601 "CPR Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;
        Src: Codeunit "CPR Src";
        Config: Codeunit "AL Runner Config";

    // ── DisplayName ────────────────────────────────────────────────────────────

    [Test]
    procedure DisplayName_Default_IsNonEmpty()
    begin
        // Positive: default DisplayName stub must not be empty.
        Assert.AreNotEqual('', Src.GetDisplayName(), 'DisplayName default must not be empty');
    end;

    [Test]
    procedure DisplayName_AfterSet_ReturnsConfiguredValue()
    begin
        // Positive: after setting via AL Runner Config, DisplayName must return the configured value.
        Config.SetCompanyDisplayName('Acme Corp');
        Assert.AreEqual('Acme Corp', Src.GetDisplayName(), 'DisplayName must return configured value');
    end;

    [Test]
    procedure DisplayName_Default_IsNotConfiguredSentinel()
    begin
        // Negative: default DisplayName must not equal an unusual sentinel, proving the stub
        // returns a real value and not an empty string or a known wrong value.
        Assert.AreNotEqual('__MISSING__', Src.GetDisplayName(), 'DisplayName must not be a missing-value sentinel');
    end;

    // ── UrlName ────────────────────────────────────────────────────────────────

    [Test]
    procedure UrlName_Default_IsNonEmpty()
    begin
        // Positive: default UrlName stub must not be empty.
        Assert.AreNotEqual('', Src.GetUrlName(), 'UrlName default must not be empty');
    end;

    [Test]
    procedure UrlName_AfterSet_ReturnsConfiguredValue()
    begin
        // Positive: after setting via AL Runner Config, UrlName must return the configured value.
        Config.SetCompanyUrlName('acme-corp');
        Assert.AreEqual('acme-corp', Src.GetUrlName(), 'UrlName must return configured value');
    end;

    [Test]
    procedure UrlName_Default_IsNotConfiguredSentinel()
    begin
        // Negative: default UrlName must not equal a known wrong value.
        Assert.AreNotEqual('__MISSING__', Src.GetUrlName(), 'UrlName must not be a missing-value sentinel');
    end;

    // ── ID ─────────────────────────────────────────────────────────────────────

    [Test]
    procedure ID_Default_IsNonNullGuid()
    begin
        // Positive: default ID must be a non-null GUID.
        Assert.IsFalse(IsNullGuid(Src.GetId()), 'ID default must be a non-null GUID');
    end;

    [Test]
    procedure ID_AfterSet_ReturnsConfiguredGuid()
    var
        ConfiguredId: Guid;
    begin
        // Positive: after setting via AL Runner Config, ID must return the configured GUID.
        Evaluate(ConfiguredId, '{AABBCCDD-0000-0000-0000-000000000001}');
        Config.SetCompanyId(ConfiguredId);
        Assert.AreEqual(ConfiguredId, Src.GetId(), 'ID must return configured GUID');
    end;

    [Test]
    procedure ID_Default_IsNotNullGuid_Negative()
    begin
        // Negative: default ID must not be the null GUID.
        Assert.IsTrue(not IsNullGuid(Src.GetId()), 'ID must not be null GUID');
    end;
}
